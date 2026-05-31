using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Utils;

using Newtonsoft.Json;

using NLog;

namespace AAEmu.Game.Core.Managers.World;

/// <summary>
/// Manages server-side collision volumes for NPC height queries and movement blocking.
/// Volumes are stored in JSON files under Data/CollisionVolumes/ and loaded at server start.
/// Provides spatial-indexed height queries called from WorldManager.GetReferenceHeight().
/// </summary>
public class CollisionVolumeManager : Singleton<CollisionVolumeManager>, ILoadable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// All loaded volumes, keyed by world name
    /// </summary>
    private readonly Dictionary<string, CollisionVolumeFile> _worldFiles = [];

    /// <summary>
    /// Spatial index: volumes indexed by 64m region grid (regionX, regionY) → list of volumes.
    /// Keyed by world name, then by (regionX, regionY).
    /// </summary>
    private readonly Dictionary<string, Dictionary<(int RX, int RY), List<CollisionVolume>>> _spatialIndex = [];

    /// <summary>
    /// Per-character polygon drawing state for the /colvol floor start/corner/finish workflow
    /// </summary>
    private readonly Dictionary<uint, PolygonDrawState> _drawStates = [];

    private bool _loaded;

    private const int RegionSize = WorldManager.REGION_SIZE; // 64m

    /// <summary>
    /// Drawing mode: Floor polygon or Wall path
    /// </summary>
    public enum DrawMode
    {
        Floor,
        Wall
    }

    /// <summary>
    /// State for an admin currently drawing a polygon/wall volume
    /// </summary>
    public class PolygonDrawState
    {
        public string Name { get; set; } = "";
        public string WorldName { get; set; } = "";
        public List<Vector3> Vertices { get; set; } = [];
        public DrawMode Mode { get; set; } = DrawMode.Floor;
        public float WallHeight { get; set; } = 1f;
    }

    /// <summary>
    /// NPC volume bindings: NPC TemplateId → set of volume IDs.
    /// Bound NPCs ONLY use these volumes for height queries and ignore terrain.
    /// Persisted in Data/CollisionVolumes/npc_bindings.json.
    /// </summary>
    private Dictionary<uint, HashSet<uint>> _npcVolumeBindings = [];

    /// <summary>
    /// NPC grid bindings: NPC TemplateId → list of grid names (or ["*"] for all grids of the world).
    /// Grid-bound NPCs prefer the height grid for height queries over terrain.
    /// If the grid has no data at the position, falls back to terrain.
    /// Persisted in Data/CollisionVolumes/npc_grid_bindings.json.
    /// </summary>
    private Dictionary<uint, List<string>> _npcGridBindings = [];

    /// <summary>
    /// Per-instance NPC grid bindings: ObjId → list of grid names.
    /// Auto-bind at spawn uses this so that only the specific NPC instance is bound,
    /// not every NPC sharing the same TemplateId.
    /// In-memory only — NOT persisted (ObjIds change on respawn).
    /// </summary>
    private readonly Dictionary<uint, List<string>> _npcInstanceGridBindings = [];

    /// <summary>
    /// Height grids per world: cell-based height map including buildings.
    /// Loaded from Data/CollisionVolumes/heightgrid_*.json
    /// Key: GridName (unique per grid). Multiple grids can belong to the same WorldName.
    /// </summary>
    private readonly Dictionary<string, HeightGridFile> _heightGrids = [];

    /// <summary>
    /// Reverse index: WorldName → list of GridNames belonging to that world.
    /// Rebuilt after loading and after adding/removing grids.
    /// </summary>
    private readonly Dictionary<string, List<string>> _worldGridIndex = [];

    /// <summary>
    /// Per-character grid scan state for the /colvol grid start/finish workflow.
    /// Records player positions into a HeightGrid with implicit wall detection.
    /// </summary>
    private readonly Dictionary<uint, GridScanState> _gridScanStates = [];

    /// <summary>
    /// State for an admin performing a height grid scan
    /// </summary>
    public class GridScanState
    {
        public string WorldName { get; set; } = "";
        /// <summary>The unique grid name (key in _heightGrids). Defaults to WorldName.</summary>
        public string GridName { get; set; } = "";
        public float CellSize { get; set; } = 1.0f;
        public int NewCells { get; set; }
        public int UpdatedCells { get; set; }
        public Vector3 LastRecordedPos { get; set; }
        public float MinPointSpacing { get; set; } = 0.3f;
        /// <summary>
        /// Fill radius in cells around each recorded position (0 = single cell, 2 = ~13 cells diamond).
        /// Higher values fill wider paths but may bleed through thin walls.
        /// </summary>
        public int ScanRadius { get; set; } = 2;
        public Models.Tasks.Task ScanTask { get; set; }
    }

    /// <summary>
    /// Per-character floor scan state for the /colvol scan start/finish workflow.
    /// Collects player positions as a point cloud, then auto-generates floor volumes.
    /// </summary>
    private readonly Dictionary<uint, FloorScanState> _scanStates = [];

    /// <summary>
    /// State for an admin performing a floor scan
    /// </summary>
    public class FloorScanState
    {
        public string Name { get; set; } = "";
        public string WorldName { get; set; } = "";
        public List<Vector3> Points { get; set; } = [];
        public Vector3 LastRecordedPos { get; set; }
        public float MinPointSpacing { get; set; } = 1.0f; // Minimum distance between recorded points (meters)
        public Models.Tasks.Task ScanTask { get; set; } // Reference to the repeating scan task for cancellation
    }

    /// <summary>
    /// Per-character wall scan state for the /colvol wall scan start/finish workflow.
    /// Records player positions along a wall, then auto-simplifies to corners.
    /// </summary>
    private readonly ConcurrentDictionary<uint, WallScanState> _wallScanStates = new();

    /// <summary>
    /// State for an admin performing a wall scan (walk along a wall)
    /// </summary>
    public class WallScanState
    {
        public string Name { get; set; } = "";
        public string WorldName { get; set; } = "";
        public List<Vector3> Points { get; set; } = [];
        public Vector3 LastRecordedPos { get; set; }
        public float WallHeight { get; set; } = 2f;
        /// <summary>Minimum distance between recorded points in meters.</summary>
        public float MinPointSpacing { get; set; } = 0.3f;
        /// <summary>Douglas-Peucker epsilon for corner simplification.</summary>
        public float SimplifyEpsilon { get; set; } = 0.1f;
        public Models.Tasks.Task ScanTask { get; set; }
    }

    /// <summary>
    /// Per-character floor perimeter scan — walk the edges, auto-create polygon floor.
    /// </summary>
    private readonly Dictionary<uint, FloorPerimeterScanState> _floorPerimeterScanStates = [];

    /// <summary>
    /// State for an admin performing a floor perimeter scan (walk along edges to define polygon floor)
    /// </summary>
    public class FloorPerimeterScanState
    {
        public string Name { get; set; } = "";
        public string WorldName { get; set; } = "";
        public List<Vector3> Points { get; set; } = [];
        public Vector3 LastRecordedPos { get; set; }
        /// <summary>Minimum distance between recorded points in meters.</summary>
        public float MinPointSpacing { get; set; } = 0.3f;
        /// <summary>Douglas-Peucker epsilon for corner simplification.</summary>
        public float SimplifyEpsilon { get; set; } = 0.5f;
        public Models.Tasks.Task ScanTask { get; set; }
    }

    /// <summary>
    /// Load all collision volume JSON files from Data/CollisionVolumes/
    /// </summary>
    public void Load()
    {
        if (_loaded)
            return;

        _worldFiles.Clear();
        _spatialIndex.Clear();

        var dataDir = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes");
        if (!Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
            Logger.Info("CollisionVolumeManager: Created Data/CollisionVolumes/ directory (empty)");
            _loaded = true;
            return;
        }

        var files = Directory.GetFiles(dataDir, "*.json");
        var totalVolumes = 0;

        // Skip non-volume files (bindings, height grids, backups)
        var skipPrefixes = new[] { "npc_bindings", "npc_grid_bindings", "heightgrid_" };

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            if (skipPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var fileSize = new FileInfo(filePath).Length;
                Logger.Info($"CollisionVolumeManager: Loading {fileName} ({fileSize / 1024:N0} KB)...");

                // Use streaming deserialization to avoid loading entire JSON string into memory
                CollisionVolumeFile file;
                using (var stream = File.OpenRead(filePath))
                using (var reader = new StreamReader(stream))
                using (var jsonReader = new JsonTextReader(reader))
                {
                    var serializer = new JsonSerializer();
                    file = serializer.Deserialize<CollisionVolumeFile>(jsonReader);
                }

                if (file == null)
                {
                    Logger.Warn($"CollisionVolumeManager: Failed to parse {filePath}");
                    continue;
                }

                // Pre-compute runtime data for each volume
                var prepared = 0;
                foreach (var vol in file.Volumes)
                {
                    PrepareVolume(vol);
                    prepared++;
                    // Log progress every 10,000 volumes for large files
                    if (prepared % 10000 == 0)
                        Logger.Info($"CollisionVolumeManager:   ...prepared {prepared:N0}/{file.Volumes.Count:N0} volumes");
                }

                // Merge volumes if a file for this world already exists (supports split files like pak_buildings.json + pak_rocks.json)
                if (_worldFiles.TryGetValue(file.WorldName, out var existing))
                {
                    existing.Volumes.AddRange(file.Volumes);
                    if (file.NextId > existing.NextId)
                        existing.NextId = file.NextId;
                }
                else
                {
                    _worldFiles[file.WorldName] = file;
                }

                RebuildSpatialIndex(file.WorldName);
                totalVolumes += file.Volumes.Count;

                Logger.Info($"CollisionVolumeManager: {fileName} complete - {file.Volumes.Count:N0} volumes for {file.WorldName} (total: {totalVolumes:N0})");

                // Force GC between files to release temporary deserialization objects
                GC.Collect(2, GCCollectionMode.Aggressive, true);
                GC.WaitForPendingFinalizers();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"CollisionVolumeManager: Error loading {filePath}");
            }
        }

        Logger.Info($"CollisionVolumeManager: Loaded {totalVolumes} total collision volumes from {files.Length} files");

        // Load NPC volume bindings
        LoadNpcBindings();
        LoadNpcGridBindings();

        // Load height grids
        LoadHeightGrids();

        _loaded = true;
    }

    /// <summary>
    /// Pre-compute triangulation and bounding box for a volume
    /// </summary>
    private static void PrepareVolume(CollisionVolume vol)
    {
        switch (vol.Shape)
        {
            case CollisionShapeType.Polygon when vol.VolumeType == CollisionVolumeType.Wall && vol.Vertices.Count >= 2:
                // Walls are open paths (not closed polygons) — only need bounding box, no triangulation
                vol.Triangles = null;
                vol.BoundingBox = PolygonUtils.CalcBoundingBox(vol.Vertices);
                break;
            case CollisionShapeType.Polygon when vol.Vertices.Count >= 3:
                vol.Triangles = PolygonUtils.EarClipTriangulate(vol.Vertices);
                vol.BoundingBox = PolygonUtils.CalcBoundingBox(vol.Vertices);
                break;
            case CollisionShapeType.Box:
            case CollisionShapeType.Ramp:
                vol.BoundingBox = PolygonUtils.CalcBoxBoundingBox(vol.Center, vol.Size, vol.RotationZ);
                vol.Triangles = null;
                break;
        }
    }

    /// <summary>
    /// Rebuild the spatial index for a specific world
    /// </summary>
    private void RebuildSpatialIndex(string worldName)
    {
        if (!_worldFiles.TryGetValue(worldName, out var file))
            return;

        var index = new Dictionary<(int RX, int RY), List<CollisionVolume>>();

        foreach (var vol in file.Volumes)
        {
            if (!vol.IsActive)
                continue;

            var bb = vol.BoundingBox;
            var minRX = (int)(bb.MinX / RegionSize);
            var minRY = (int)(bb.MinY / RegionSize);
            var maxRX = (int)(bb.MaxX / RegionSize);
            var maxRY = (int)(bb.MaxY / RegionSize);

            // Safety cap: skip volumes that span too many grid cells (likely bad data)
            var spanX = maxRX - minRX + 1;
            var spanY = maxRY - minRY + 1;
            if (spanX > 20 || spanY > 20 || (long)spanX * spanY > 100)
            {
                Logger.Warn($"CollisionVolumeManager: Skipping volume {vol.Id} '{vol.Name}' - spans {spanX}x{spanY} grid cells (bbox: {bb.MinX:F0},{bb.MinY:F0} -> {bb.MaxX:F0},{bb.MaxY:F0})");
                continue;
            }

            for (var ry = minRY; ry <= maxRY; ry++)
            {
                for (var rx = minRX; rx <= maxRX; rx++)
                {
                    var key = (rx, ry);
                    if (!index.TryGetValue(key, out var list))
                    {
                        list = [];
                        index[key] = list;
                    }
                    list.Add(vol);
                }
            }
        }

        _spatialIndex[worldName] = index;
        Logger.Info($"CollisionVolumeManager: Spatial index for {worldName}: {index.Count} grid cells, {file.Volumes.Count} volumes");
    }

    /// <summary>
    /// Query the floor height at position (x, y) considering the NPC's current Z.
    /// Returns the Z of the highest floor volume that is below or at the NPC's feet (z + tolerance).
    /// Returns null if no collision volume applies.
    /// </summary>
    /// <param name="worldName">World name (e.g., "main_world")</param>
    /// <param name="x">World X position</param>
    /// <param name="y">World Y position</param>
    /// <param name="z">Current Z height of the NPC</param>
    /// <returns>Floor height, or null if no volume covers this position</returns>
    public float? GetFloorHeight(string worldName, float x, float y, float z)
    {
        if (!_spatialIndex.TryGetValue(worldName, out var index))
            return null;

        var rx = (int)(x / RegionSize);
        var ry = (int)(y / RegionSize);

        if (!index.TryGetValue((rx, ry), out var volumes))
            return null;

        float? bestHeight = null;
        const float tolerance = 3f; // NPC can be up to 3m above a floor and still be "on" it

        foreach (var vol in volumes)
        {
            if (vol.VolumeType != CollisionVolumeType.Floor)
                continue;

            // Fast AABB rejection
            if (x < vol.BoundingBox.MinX || x > vol.BoundingBox.MaxX ||
                y < vol.BoundingBox.MinY || y > vol.BoundingBox.MaxY)
                continue;

            var volHeight = GetVolumeHeightAt(vol, x, y);
            if (!volHeight.HasValue)
                continue;

            // The floor must be below or at the NPC's feet (with tolerance)
            // and we want the highest such floor
            if (volHeight.Value <= z + tolerance)
            {
                if (!bestHeight.HasValue || volHeight.Value > bestHeight.Value)
                {
                    bestHeight = volHeight;
                }
            }
        }

        return bestHeight;
    }

    // ── Pathfinding ──

    /// <summary>
    /// Get all loaded world names (for pathfinder wall checks across worlds).
    /// </summary>
    public IEnumerable<string> GetLoadedWorldNames() => _spatialIndex.Keys;

    /// <summary>
    /// Find a path from start to target that avoids Wall volumes using A* pathfinding.
    /// Returns empty list if direct path is clear (no wall) or if no path is found.
    /// </summary>
    public List<System.Numerics.Vector3> FindWallPath(string worldName,
        System.Numerics.Vector3 start, System.Numerics.Vector3 target, float z)
    {
        // Phase 6 — navmesh first when configured + baked. The Phase-1..5 GridPathfinder fallback
        // catches worlds without a .navmesh on disk, off-mesh starts/ends, and the legacy code path
        // when the flag is off (default), so behaviour stays identical when no navmesh exists.
        if (AppConfiguration.Instance.World.UseNavMesh && NavMeshManager.Instance.EnsureLoaded(worldName))
        {
            var navPath = NavMeshManager.Instance.FindPath(worldName, start, target);
            if (navPath != null && navPath.Count > 1)
                return navPath;
        }
        return GridPathfinder.FindPath(worldName, start, target, z);
    }

    /// <summary>
    /// Check if a movement line (from->to) is blocked by any wall volume.
    /// Walls are vertical barriers defined by a path of vertices with a height.
    /// The NPC must be within the wall's Z range (baseZ to baseZ + WallHeight) to be blocked.
    /// </summary>
    /// <param name="worldName">World name</param>
    /// <param name="fromX">Start X</param>
    /// <param name="fromY">Start Y</param>
    /// <param name="toX">Destination X</param>
    /// <param name="toY">Destination Y</param>
    /// <param name="z">NPC's Z position (feet)</param>
    /// <returns>True if the movement crosses a wall and should be blocked</returns>
    public bool IsBlockedByWall(string worldName, float fromX, float fromY, float toX, float toY,
        float footZ, float npcHeight = 2.0f)
    {
        // Phase 5 — mesh fallthrough now passes the NPC's foot Z + height so IsLineBlockedByMesh
        // can capsule-sample low parapets (Z [foot..foot+0.8]) AND elevated railings (Z
        // [foot+1..foot+2.5]) — the previous z+1m fixed eye height missed both classes.
        if (AppConfiguration.Instance.World.UseMeshLineOfSight)
        {
            var meshFlags = AppConfiguration.Instance.World.MeshLosSkipFoliage
                ? MeshCollisionManager.MeshQueryFlags.SkipFoliage
                : MeshCollisionManager.MeshQueryFlags.None;
            if (MeshCollisionManager.Instance.IsLineBlockedByMesh(
                    worldName,
                    new Vector3(fromX, fromY, footZ),
                    new Vector3(toX, toY, footZ),
                    meshFlags,
                    bodyRadius: 0.45f,
                    npcHeight: npcHeight))
                return true;
        }

        if (!_spatialIndex.TryGetValue(worldName, out var index))
            return false;

        // Check regions along the movement line (start and end region)
        var rxFrom = (int)(fromX / RegionSize);
        var ryFrom = (int)(fromY / RegionSize);
        var rxTo = (int)(toX / RegionSize);
        var ryTo = (int)(toY / RegionSize);

        // Collect unique volumes from both regions (and any in between)
        var minRx = Math.Min(rxFrom, rxTo);
        var maxRx = Math.Max(rxFrom, rxTo);
        var minRy = Math.Min(ryFrom, ryTo);
        var maxRy = Math.Max(ryFrom, ryTo);

        for (var ry = minRy; ry <= maxRy; ry++)
        {
            for (var rx = minRx; rx <= maxRx; rx++)
            {
                if (!index.TryGetValue((rx, ry), out var volumes))
                    continue;

                foreach (var vol in volumes)
                {
                    if (vol.VolumeType != CollisionVolumeType.Wall)
                        continue;

                    if (IsMovementBlockedByWall(vol, fromX, fromY, toX, toY, footZ, npcHeight))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Test if a specific movement line crosses a wall volume.
    /// A wall is a series of connected line segments (open path) with a height.
    /// </summary>
    private static bool IsMovementBlockedByWall(CollisionVolume wall, float fromX, float fromY, float toX, float toY,
        float footZ, float npcHeight)
    {
        if (wall.Vertices == null || wall.Vertices.Count < 2)
            return false;

        // Quick AABB rejection for the movement line vs wall bounding box
        var moveMinX = MathF.Min(fromX, toX);
        var moveMaxX = MathF.Max(fromX, toX);
        var moveMinY = MathF.Min(fromY, toY);
        var moveMaxY = MathF.Max(fromY, toY);

        if (moveMaxX < wall.BoundingBox.MinX || moveMinX > wall.BoundingBox.MaxX ||
            moveMaxY < wall.BoundingBox.MinY || moveMinY > wall.BoundingBox.MaxY)
            return false;

        // Phase 5: NPC vertical extent [footZ - 0.5m, footZ + npcHeight]. 0.5m below the feet
        // tolerates terrain-vs-wall elevation mismatches; the top reaches the head.
        var npcMinZ = footZ - 0.5f;
        var npcMaxZ = footZ + npcHeight;

        // Check each wall segment
        for (var i = 0; i < wall.Vertices.Count - 1; i++)
        {
            var a = wall.Vertices[i];
            var b = wall.Vertices[i + 1];

            // Wall extends from the lower vertex Z up to Z + WallHeight.
            var segBaseZ = MathF.Min(a.Z, b.Z);
            var segTopZ = MathF.Max(a.Z, b.Z) + wall.WallHeight;

            // Phase 5 — interval overlap instead of point-in-window. Old test was
            //   if (footZ < segBaseZ - 0.5f || footZ > segTopZ) continue;
            // which silently missed elevated railings (Z [101..102.5] vs NPC foot Z=100):
            // foot was below segBaseZ-0.5 so the wall was skipped even though the NPC's
            // torso would hit it. The interval test asks instead "do the NPC's vertical
            // extent and the wall's vertical extent overlap at all?".
            if (segTopZ < npcMinZ || segBaseZ > npcMaxZ)
                continue;

            // 2D line segment intersection test
            if (PolygonUtils.SegmentsIntersect2D(fromX, fromY, toX, toY, a.X, a.Y, b.X, b.Y))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Get the height of a specific volume at position (x, y).
    /// Returns null if the position is outside the volume.
    /// </summary>
    private static float? GetVolumeHeightAt(CollisionVolume vol, float x, float y)
    {
        switch (vol.Shape)
        {
            case CollisionShapeType.Polygon:
                return GetPolygonHeight(vol, x, y);
            case CollisionShapeType.Box:
                return GetBoxHeight(vol, x, y);
            case CollisionShapeType.Ramp:
                return GetRampHeight(vol, x, y);
            default:
                return null;
        }
    }

    /// <summary>
    /// Get interpolated height at (x, y) for a polygon volume using pre-computed triangles
    /// </summary>
    private static float? GetPolygonHeight(CollisionVolume vol, float x, float y)
    {
        if (vol.Triangles == null || vol.Triangles.Count == 0)
            return null;

        // Quick polygon containment check
        if (!PolygonUtils.PointInPolygon2D(x, y, vol.Vertices))
            return null;

        // Find which triangle contains the point and interpolate height
        foreach (var (a, b, c) in vol.Triangles)
        {
            var va = vol.Vertices[a];
            var vb = vol.Vertices[b];
            var vc = vol.Vertices[c];

            if (PolygonUtils.PointInTriangle2D(x, y, va, vb, vc))
            {
                return PolygonUtils.InterpolateHeight(x, y, va, vb, vc);
            }
        }

        // Fallback: point is inside polygon but no triangle matched (edge case)
        // Return average height
        var avgZ = 0f;
        foreach (var v in vol.Vertices) avgZ += v.Z;
        return avgZ / vol.Vertices.Count;
    }

    /// <summary>
    /// Get height at (x, y) for a box volume (flat floor at Center.Z)
    /// </summary>
    private static float? GetBoxHeight(CollisionVolume vol, float x, float y)
    {
        if (!PolygonUtils.PointInOBB2D(x, y, vol.Center, vol.Size, vol.RotationZ))
            return null;

        return vol.Center.Z;
    }

    /// <summary>
    /// Get height at (x, y) for a ramp volume (sloped floor)
    /// </summary>
    private static float? GetRampHeight(CollisionVolume vol, float x, float y)
    {
        if (!PolygonUtils.PointInOBB2D(x, y, vol.Center, vol.Size, vol.RotationZ))
            return null;

        if (!vol.SlopeDirection.HasValue || !vol.SlopeAngle.HasValue)
            return vol.Center.Z; // No slope info, treat as flat

        // Transform to local space
        var rad = vol.RotationZ * MathF.PI / 180f;
        var cosR = MathF.Cos(-rad);
        var sinR = MathF.Sin(-rad);
        var dx = x - vol.Center.X;
        var dy = y - vol.Center.Y;
        var localX = dx * cosR - dy * sinR;
        var localY = dx * sinR + dy * cosR;

        // Calculate slope offset based on slope direction
        var slopeRad = vol.SlopeDirection.Value * MathF.PI / 180f;
        var slopeLocalX = MathF.Sin(slopeRad);
        var slopeLocalY = MathF.Cos(slopeRad);

        // Project local position onto slope direction
        var projDist = localX * slopeLocalX + localY * slopeLocalY;
        var slopeAngleRad = vol.SlopeAngle.Value * MathF.PI / 180f;
        var heightOffset = projDist * MathF.Tan(slopeAngleRad);

        return vol.Center.Z + heightOffset;
    }

    // ─────────────────────────────────────────────────────────
    // Admin command helpers
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Start polygon drawing mode for an admin (floor)
    /// </summary>
    public void StartPolygonDraw(uint characterObjId, string worldName, string name)
    {
        _drawStates[characterObjId] = new PolygonDrawState
        {
            Name = name,
            WorldName = worldName,
            Mode = DrawMode.Floor
        };
    }

    /// <summary>
    /// Start wall drawing mode for an admin
    /// </summary>
    public void StartWallDraw(uint characterObjId, string worldName, string name, float height = 1f)
    {
        _drawStates[characterObjId] = new PolygonDrawState
        {
            Name = name,
            WorldName = worldName,
            Mode = DrawMode.Wall,
            WallHeight = height
        };
    }

    /// <summary>
    /// Check if admin is in polygon drawing mode
    /// </summary>
    public bool IsDrawing(uint characterObjId) => _drawStates.ContainsKey(characterObjId);

    /// <summary>
    /// Get draw state for admin
    /// </summary>
    public PolygonDrawState GetDrawState(uint characterObjId)
    {
        return _drawStates.GetValueOrDefault(characterObjId);
    }

    /// <summary>
    /// Add a corner to the current polygon being drawn
    /// </summary>
    public int AddCorner(uint characterObjId, Vector3 position)
    {
        if (!_drawStates.TryGetValue(characterObjId, out var state))
            return -1;

        state.Vertices.Add(position);
        return state.Vertices.Count;
    }

    /// <summary>
    /// Remove the last corner from the polygon being drawn
    /// </summary>
    public bool UndoCorner(uint characterObjId)
    {
        if (!_drawStates.TryGetValue(characterObjId, out var state) || state.Vertices.Count == 0)
            return false;

        state.Vertices.RemoveAt(state.Vertices.Count - 1);
        return true;
    }

    /// <summary>
    /// Cancel polygon drawing mode
    /// </summary>
    public void CancelDraw(uint characterObjId)
    {
        _drawStates.Remove(characterObjId);
    }

    /// <summary>
    /// Finish drawing: create the volume (floor or wall), add to world, save to file.
    /// Floor requires 3+ vertices (closed polygon), Wall requires 2+ vertices (open path).
    /// </summary>
    public CollisionVolume FinishPolygonDraw(uint characterObjId)
    {
        if (!_drawStates.TryGetValue(characterObjId, out var state))
            return null;

        var minVerts = state.Mode == DrawMode.Wall ? 2 : 3;
        if (state.Vertices.Count < minVerts)
            return null;

        _drawStates.Remove(characterObjId);

        // Ensure world file exists
        if (!_worldFiles.TryGetValue(state.WorldName, out var file))
        {
            file = new CollisionVolumeFile { WorldName = state.WorldName };
            _worldFiles[state.WorldName] = file;
        }

        CollisionVolume volume;

        if (state.Mode == DrawMode.Wall)
        {
            // Wall: store vertices as a path (not a closed polygon)
            // Size.Z = wall height, Center.Z = base Z (average of vertices)
            var avgZ = state.Vertices.Average(v => v.Z);
            volume = new CollisionVolume
            {
                Id = file.NextId++,
                Name = state.Name,
                VolumeType = CollisionVolumeType.Wall,
                Shape = CollisionShapeType.Polygon, // reuse Polygon shape for vertex storage
                IsActive = true,
                Vertices = new List<Vector3>(state.Vertices),
                WallHeight = state.WallHeight
            };
        }
        else
        {
            volume = new CollisionVolume
            {
                Id = file.NextId++,
                Name = state.Name,
                VolumeType = CollisionVolumeType.Floor,
                Shape = CollisionShapeType.Polygon,
                IsActive = true,
                Vertices = new List<Vector3>(state.Vertices)
            };
        }

        PrepareVolume(volume);
        file.Volumes.Add(volume);
        RebuildSpatialIndex(state.WorldName);
        SaveWorldFile(state.WorldName);

        return volume;
    }

    /// <summary>
    /// Add a box volume at the specified position
    /// </summary>
    public CollisionVolume AddBox(string worldName, string name, Vector3 center, Vector3 halfExtents, float rotationZ, CollisionVolumeType volumeType = CollisionVolumeType.Floor)
    {
        if (!_worldFiles.TryGetValue(worldName, out var file))
        {
            file = new CollisionVolumeFile { WorldName = worldName };
            _worldFiles[worldName] = file;
        }

        var volume = new CollisionVolume
        {
            Id = file.NextId++,
            Name = name,
            VolumeType = volumeType,
            Shape = CollisionShapeType.Box,
            IsActive = true,
            Center = center,
            Size = halfExtents,
            RotationZ = rotationZ
        };

        PrepareVolume(volume);
        file.Volumes.Add(volume);
        RebuildSpatialIndex(worldName);
        SaveWorldFile(worldName);

        return volume;
    }

    /// <summary>
    /// Get a volume by ID in a specific world
    /// </summary>
    public CollisionVolume GetVolume(string worldName, uint volumeId)
    {
        if (!_worldFiles.TryGetValue(worldName, out var file))
            return null;
        return file.Volumes.FirstOrDefault(v => v.Id == volumeId);
    }

    /// <summary>
    /// Delete a volume by ID
    /// </summary>
    public bool DeleteVolume(string worldName, uint volumeId)
    {
        if (!_worldFiles.TryGetValue(worldName, out var file))
            return false;

        var removed = file.Volumes.RemoveAll(v => v.Id == volumeId);
        if (removed > 0)
        {
            RebuildSpatialIndex(worldName);
            SaveWorldFile(worldName);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Toggle a volume's active state
    /// </summary>
    public bool ToggleVolume(string worldName, uint volumeId)
    {
        var vol = GetVolume(worldName, volumeId);
        if (vol == null) return false;

        vol.IsActive = !vol.IsActive;
        RebuildSpatialIndex(worldName);
        SaveWorldFile(worldName);
        return true;
    }

    /// <summary>
    /// List all volumes in a world within a radius of a position
    /// </summary>
    public List<CollisionVolume> ListVolumesNear(string worldName, float x, float y, float radius)
    {
        if (!_worldFiles.TryGetValue(worldName, out var file))
            return [];

        var radiusSq = radius * radius;
        var result = new List<CollisionVolume>();

        foreach (var vol in file.Volumes)
        {
            // Calculate distance to volume center (approximate)
            float cx, cy;
            if (vol.Shape == CollisionShapeType.Polygon && vol.Vertices.Count > 0)
            {
                cx = vol.Vertices.Average(v => v.X);
                cy = vol.Vertices.Average(v => v.Y);
            }
            else
            {
                cx = vol.Center.X;
                cy = vol.Center.Y;
            }

            var dx = cx - x;
            var dy = cy - y;
            if (dx * dx + dy * dy <= radiusSq)
            {
                result.Add(vol);
            }
        }

        return result;
    }

    /// <summary>
    /// Get total volume count across all worlds
    /// </summary>
    public int GetTotalVolumeCount()
    {
        return _worldFiles.Values.Sum(f => f.Volumes.Count);
    }

    /// <summary>
    /// Reload all collision volume files from disk
    /// </summary>
    public void Reload()
    {
        _loaded = false;
        _heightGrids.Clear();
        Load();
    }

    // ─────────────────────────────────────────────────────────
    // NPC Volume Bindings
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Get the set of bound volume IDs for an NPC template, or null if not bound.
    /// </summary>
    public HashSet<uint> GetNpcBinding(uint npcTemplateId)
    {
        return _npcVolumeBindings.GetValueOrDefault(npcTemplateId);
    }

    /// <summary>
    /// Check if an NPC template has volume bindings.
    /// </summary>
    public bool HasNpcBinding(uint npcTemplateId) => _npcVolumeBindings.ContainsKey(npcTemplateId);

    /// <summary>
    /// Bind an NPC template to specific collision volume IDs.
    /// The NPC will ONLY use these volumes for height queries and ignore terrain.
    /// </summary>
    public void BindNpcToVolumes(uint npcTemplateId, IEnumerable<uint> volumeIds)
    {
        _npcVolumeBindings[npcTemplateId] = [..volumeIds];
        SaveNpcBindings();
        Logger.Info($"CollisionVolumeManager: Bound NPC template {npcTemplateId} to volumes [{string.Join(", ", volumeIds)}]");
    }

    /// <summary>
    /// Remove all volume bindings for an NPC template.
    /// </summary>
    public void UnbindNpc(uint npcTemplateId)
    {
        if (_npcVolumeBindings.Remove(npcTemplateId))
        {
            SaveNpcBindings();
            Logger.Info($"CollisionVolumeManager: Unbound NPC template {npcTemplateId}");
        }
    }

    /// <summary>
    /// Get all NPC volume bindings (for listing).
    /// </summary>
    public IReadOnlyDictionary<uint, HashSet<uint>> GetAllNpcBindings() => _npcVolumeBindings;

    /// <summary>
    /// Get the floor height for an NPC that is bound to specific volumes.
    /// Only checks the specified volume IDs, ignoring all others.
    /// Returns null if no bound volume covers the position.
    /// </summary>
    public float? GetBoundFloorHeight(string worldName, float x, float y, float z, HashSet<uint> volumeIds)
    {
        if (!_worldFiles.TryGetValue(worldName, out var file))
            return null;

        float? bestHeight = null;
        const float tolerance = 3f;

        // Search only the bound volumes (by ID) — no spatial index needed since bound sets are small
        foreach (var vol in file.Volumes)
        {
            if (!volumeIds.Contains(vol.Id))
                continue;

            if (vol.VolumeType != CollisionVolumeType.Floor)
                continue;

            if (!vol.IsActive)
                continue;

            // Fast AABB rejection
            if (x < vol.BoundingBox.MinX || x > vol.BoundingBox.MaxX ||
                y < vol.BoundingBox.MinY || y > vol.BoundingBox.MaxY)
                continue;

            var volHeight = GetVolumeHeightAt(vol, x, y);
            if (!volHeight.HasValue)
                continue;

            if (volHeight.Value <= z + tolerance)
            {
                if (!bestHeight.HasValue || volHeight.Value > bestHeight.Value)
                    bestHeight = volHeight;
            }
        }

        return bestHeight;
    }

    /// <summary>
    /// Load NPC volume bindings from Data/CollisionVolumes/npc_bindings.json
    /// </summary>
    private void LoadNpcBindings()
    {
        var filePath = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes", "npc_bindings.json");
        if (!File.Exists(filePath))
        {
            _npcVolumeBindings = [];
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var raw = JsonConvert.DeserializeObject<Dictionary<string, List<uint>>>(json);
            _npcVolumeBindings = [];
            if (raw != null)
            {
                foreach (var kvp in raw)
                {
                    if (uint.TryParse(kvp.Key, out var templateId))
                        _npcVolumeBindings[templateId] = [..kvp.Value];
                }
            }
            Logger.Info($"CollisionVolumeManager: Loaded {_npcVolumeBindings.Count} NPC volume bindings");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "CollisionVolumeManager: Failed to load npc_bindings.json");
            _npcVolumeBindings = [];
        }
    }

    /// <summary>
    /// Save NPC volume bindings to Data/CollisionVolumes/npc_bindings.json
    /// </summary>
    private void SaveNpcBindings()
    {
        var dataDir = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes");
        if (!Directory.Exists(dataDir))
            Directory.CreateDirectory(dataDir);

        var filePath = Path.Combine(dataDir, "npc_bindings.json");

        try
        {
            // Serialize as { "templateId": [volId1, volId2, ...] }
            var raw = new Dictionary<string, List<uint>>();
            foreach (var kvp in _npcVolumeBindings.OrderBy(k => k.Key))
                raw[kvp.Key.ToString()] = kvp.Value.OrderBy(v => v).ToList();

            var json = JsonConvert.SerializeObject(raw, Formatting.Indented);
            File.WriteAllText(filePath, json);
            Logger.Debug($"CollisionVolumeManager: Saved {_npcVolumeBindings.Count} NPC volume bindings");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "CollisionVolumeManager: Failed to save npc_bindings.json");
        }
    }

    // ─────────────────────────────────────────────────────────
    // Wall Scanner
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Start wall scanning mode: walk along a wall and positions are recorded automatically.
    /// Use FinishWallScan to simplify the path (Douglas-Peucker) and create the wall volume.
    /// </summary>
    public void StartWallScan(uint characterObjId, string worldName, string name, float height = 2f, float epsilon = 0.1f)
    {
        _wallScanStates[characterObjId] = new WallScanState
        {
            Name = name,
            WorldName = worldName,
            WallHeight = height,
            SimplifyEpsilon = epsilon
        };
    }

    /// <summary>Check if an admin is in wall scan mode.</summary>
    public bool IsWallScanning(uint characterObjId) => _wallScanStates.ContainsKey(characterObjId);

    /// <summary>Get wall scan state for an admin.</summary>
    public WallScanState GetWallScanState(uint characterObjId)
    {
        return _wallScanStates.GetValueOrDefault(characterObjId);
    }

    /// <summary>
    /// Record a position during wall scanning.
    /// Only records if the player moved at least MinPointSpacing from the last recorded position.
    /// </summary>
    public void RecordWallScanPosition(uint characterObjId, float x, float y, float z)
    {
        if (!_wallScanStates.TryGetValue(characterObjId, out var state))
            return;

        var pos = new Vector3(x, y, z);

        // Skip if too close to the last recorded point
        if (state.LastRecordedPos != Vector3.Zero)
        {
            var dist = Vector3.Distance(pos, state.LastRecordedPos);
            if (dist < state.MinPointSpacing)
                return;
        }

        state.Points.Add(pos);
        state.LastRecordedPos = pos;
    }

    /// <summary>
    /// Finish wall scanning: simplify the recorded path using Douglas-Peucker,
    /// create a wall volume from the simplified corners.
    /// Returns (volume, rawPoints, simplifiedPoints) or (null, 0, 0) on failure.
    /// </summary>
    public (CollisionVolume Volume, int RawPoints, int SimplifiedPoints) FinishWallScan(uint characterObjId)
    {
        if (!_wallScanStates.TryGetValue(characterObjId, out var state))
            return (null, 0, 0);

        state.ScanTask?.Cancel();
        _wallScanStates.TryRemove(characterObjId, out _);

        if (state.Points.Count < 2)
            return (null, state.Points.Count, 0);

        // Simplify path: Douglas-Peucker algorithm reduces points while preserving corners
        var simplified = AAEmu.Game.Utils.PolygonUtils.SimplifyPath(state.Points, state.SimplifyEpsilon);

        if (simplified.Count < 2)
            return (null, state.Points.Count, simplified.Count);

        // Create a wall volume from the simplified corners
        if (!_worldFiles.TryGetValue(state.WorldName, out var file))
        {
            file = new CollisionVolumeFile { WorldName = state.WorldName };
            _worldFiles[state.WorldName] = file;
        }

        var volume = new CollisionVolume
        {
            Id = file.NextId++,
            Name = state.Name,
            VolumeType = CollisionVolumeType.Wall,
            Shape = CollisionShapeType.Polygon,
            IsActive = true,
            Vertices = new List<Vector3>(simplified),
            WallHeight = state.WallHeight
        };

        PrepareVolume(volume);
        file.Volumes.Add(volume);
        RebuildSpatialIndex(state.WorldName);
        SaveWorldFile(state.WorldName);

        Logger.Info($"CollisionVolumeManager: Wall scan finished - {state.Points.Count} raw -> {simplified.Count} corners, " +
                    $"Name=\"{state.Name}\", Height={state.WallHeight}m");

        return (volume, state.Points.Count, simplified.Count);
    }

    /// <summary>Cancel wall scanning mode.</summary>
    public void CancelWallScan(uint characterObjId)
    {
        if (_wallScanStates.TryGetValue(characterObjId, out var state))
        {
            state.ScanTask?.Cancel();
            _wallScanStates.TryRemove(characterObjId, out _);
        }
    }

    // ─────────────────────────────────────────────────────────
    // Floor Scan — walk the perimeter to create a polygon floor
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Start floor scanning mode: walk along the edges/perimeter and positions are recorded.
    /// Use FinishFloorScan to simplify the polygon and create the floor volume.
    /// </summary>
    public void StartFloorScan(uint characterObjId, string worldName, string name, float epsilon = 0.5f)
    {
        _floorPerimeterScanStates[characterObjId] = new FloorPerimeterScanState
        {
            Name = name,
            WorldName = worldName,
            SimplifyEpsilon = epsilon
        };
    }

    /// <summary>Check if an admin is in floor scan mode.</summary>
    public bool IsFloorScanning(uint characterObjId) => _floorPerimeterScanStates.ContainsKey(characterObjId);

    /// <summary>Get floor scan state for an admin.</summary>
    public FloorPerimeterScanState GetFloorPerimeterScanState(uint characterObjId)
    {
        return _floorPerimeterScanStates.GetValueOrDefault(characterObjId);
    }

    /// <summary>
    /// Record a position during floor scanning.
    /// Only records if the player moved at least MinPointSpacing from the last recorded position.
    /// </summary>
    public void RecordFloorScanPosition(uint characterObjId, float x, float y, float z)
    {
        if (!_floorPerimeterScanStates.TryGetValue(characterObjId, out var state))
            return;

        var pos = new Vector3(x, y, z);

        // Skip if too close to the last recorded point
        if (state.LastRecordedPos != Vector3.Zero)
        {
            var dist = Vector3.Distance(pos, state.LastRecordedPos);
            if (dist < state.MinPointSpacing)
                return;
        }

        state.Points.Add(pos);
        state.LastRecordedPos = pos;
    }

    /// <summary>
    /// Finish floor scanning: simplify the recorded perimeter path using Douglas-Peucker,
    /// close the polygon, triangulate, and create a floor volume.
    /// Returns (volume, rawPoints, simplifiedPoints) or (null, rawCount, 0) on failure.
    /// </summary>
    public (CollisionVolume Volume, int RawPoints, int SimplifiedPoints) FinishFloorScan(uint characterObjId)
    {
        if (!_floorPerimeterScanStates.TryGetValue(characterObjId, out var state))
            return (null, 0, 0);

        state.ScanTask?.Cancel();
        _floorPerimeterScanStates.Remove(characterObjId);

        if (state.Points.Count < 3)
            return (null, state.Points.Count, 0);

        // Simplify path: Douglas-Peucker algorithm reduces points while preserving corners
        var simplified = AAEmu.Game.Utils.PolygonUtils.SimplifyPath(state.Points, state.SimplifyEpsilon);

        if (simplified.Count < 3)
            return (null, state.Points.Count, simplified.Count);

        // Triangulate the polygon
        var triangles = AAEmu.Game.Utils.PolygonUtils.EarClipTriangulate(simplified);
        if (triangles == null || triangles.Count == 0)
            return (null, state.Points.Count, simplified.Count);

        // Create a floor volume
        if (!_worldFiles.TryGetValue(state.WorldName, out var file))
        {
            file = new CollisionVolumeFile { WorldName = state.WorldName };
            _worldFiles[state.WorldName] = file;
        }

        var volume = new CollisionVolume
        {
            Id = file.NextId++,
            Name = state.Name,
            VolumeType = CollisionVolumeType.Floor,
            Shape = CollisionShapeType.Polygon,
            IsActive = true,
            Vertices = new List<Vector3>(simplified),
            Triangles = triangles
        };

        PrepareVolume(volume);
        file.Volumes.Add(volume);
        RebuildSpatialIndex(state.WorldName);
        SaveWorldFile(state.WorldName);

        Logger.Info($"CollisionVolumeManager: Floor scan finished - {state.Points.Count} raw -> {simplified.Count} corners, " +
                    $"{triangles.Count} triangles, Name=\"{state.Name}\"");

        return (volume, state.Points.Count, simplified.Count);
    }

    /// <summary>Cancel floor scanning mode.</summary>
    public void CancelFloorScan(uint characterObjId)
    {
        if (_floorPerimeterScanStates.TryGetValue(characterObjId, out var state))
        {
            state.ScanTask?.Cancel();
            _floorPerimeterScanStates.Remove(characterObjId);
        }
    }

    // ─────────────────────────────────────────────────────────
    // NPC Grid Bindings
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Check if an NPC template is bound to any height grid.
    /// </summary>
    public bool IsNpcGridBound(uint npcTemplateId) => _npcGridBindings.ContainsKey(npcTemplateId);

    /// <summary>
    /// Check if an NPC instance (by ObjId) is bound to any height grid.
    /// </summary>
    public bool IsNpcInstanceGridBound(uint objId) => _npcInstanceGridBindings.ContainsKey(objId);

    /// <summary>
    /// Check if an NPC is bound to any grid — by instance ObjId first, then by TemplateId.
    /// </summary>
    public bool IsNpcBoundToAnyGrid(uint objId, uint templateId)
        => _npcInstanceGridBindings.ContainsKey(objId) || _npcGridBindings.ContainsKey(templateId);

    /// <summary>
    /// Get the grid names an NPC is bound to. Contains "*" if bound to all grids.
    /// Returns null if not bound.
    /// </summary>
    public List<string> GetNpcGridBinding(uint npcTemplateId)
    {
        return _npcGridBindings.GetValueOrDefault(npcTemplateId);
    }

    /// <summary>
    /// Get the grid names an NPC instance (ObjId) is bound to.
    /// Returns null if not bound.
    /// </summary>
    public List<string> GetNpcInstanceGridBinding(uint objId)
    {
        return _npcInstanceGridBindings.GetValueOrDefault(objId);
    }

    /// <summary>
    /// Get the effective grid binding for an NPC: instance binding (ObjId) takes priority,
    /// falls back to template binding (TemplateId).
    /// Returns null if neither exists.
    /// </summary>
    public List<string> GetEffectiveNpcGridBinding(uint objId, uint templateId)
    {
        return _npcInstanceGridBindings.GetValueOrDefault(objId)
            ?? _npcGridBindings.GetValueOrDefault(templateId);
    }

    /// <summary>
    /// Get the grid height for a grid-bound NPC at position (x, y).
    /// Checks collision volumes first, then height grids based on the NPC's bindings.
    /// </summary>
    /// <param name="currentZ">NPC's current Z — used to pick the closest floor in multi-floor areas</param>
    /// <returns>Grid height, or null if no grid data at this position</returns>
    public float? GetGridHeightForNpc(uint objId, uint templateId, float x, float y, float currentZ)
    {
        var bindings = GetEffectiveNpcGridBinding(objId, templateId);
        if (bindings == null || bindings.Count == 0)
            return null;

        // 1. Check collision volumes first (floors in buildings)
        var worldName = "main_world";
        var floorHeight = GetFloorHeight(worldName, x, y, currentZ);
        if (floorHeight.HasValue)
            return floorHeight.Value;

        // 2. Check height grids based on bindings
        float? bestHeight = null;
        var bestDist = float.MaxValue;

        foreach (var binding in bindings)
        {
            if (binding == "*")
            {
                // Wildcard: search all grids in world
                var h = GetGridHeight(worldName, x, y, currentZ);
                if (h.HasValue)
                    return h.Value;
            }
            else
            {
                // Specific grid name
                if (!_heightGrids.TryGetValue(binding, out var grid))
                    continue;

                var h = grid.GetBestFloorHeight(x, y, currentZ);
                if (h.HasValue)
                {
                    var dist = MathF.Abs(h.Value - currentZ);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestHeight = h;
                    }
                }
            }
        }

        return bestHeight;
    }

    /// <summary>
    /// Bind an NPC template to a height grid (adds to existing bindings).
    /// It will prefer grid height over terrain.
    /// </summary>
    /// <param name="npcTemplateId">NPC template ID</param>
    /// <param name="gridName">Grid name, or "*" for all grids of the world (default)</param>
    public void BindNpcToGrid(uint npcTemplateId, string gridName = "*")
    {
        if (!_npcGridBindings.TryGetValue(npcTemplateId, out var list))
        {
            list = [];
            _npcGridBindings[npcTemplateId] = list;
        }

        // "*" replaces all specific grids; a specific grid alongside "*" is redundant
        if (gridName == "*")
        {
            list.Clear();
            list.Add("*");
        }
        else
        {
            // If currently "*", switch to specific list
            if (list.Contains("*"))
                list.Remove("*");
            if (!list.Contains(gridName))
                list.Add(gridName);
        }

        SaveNpcGridBindings();
        Logger.Info($"CollisionVolumeManager: Grid-bound NPC template {npcTemplateId} -> [{string.Join(", ", list)}]");
    }

    /// <summary>
    /// Unbind an NPC template from a specific grid, or remove all bindings if gridName is null.
    /// </summary>
    public void UnbindNpcFromGrid(uint npcTemplateId, string gridName = null)
    {
        if (gridName == null)
        {
            // Remove ALL bindings
            if (_npcGridBindings.Remove(npcTemplateId))
            {
                SaveNpcGridBindings();
                Logger.Info($"CollisionVolumeManager: Grid-unbound NPC template {npcTemplateId} (all grids)");
            }
        }
        else
        {
            // Remove just one grid from the list
            if (_npcGridBindings.TryGetValue(npcTemplateId, out var list))
            {
                list.Remove(gridName);
                if (list.Count == 0)
                    _npcGridBindings.Remove(npcTemplateId);
                SaveNpcGridBindings();
                Logger.Info($"CollisionVolumeManager: Grid-unbound NPC template {npcTemplateId} from '{gridName}'");
            }
        }
    }

    /// <summary>
    /// Get all grid-bound NPC template IDs with their grid name lists.
    /// </summary>
    public IReadOnlyDictionary<uint, List<string>> GetAllNpcGridBindings() => _npcGridBindings;

    /// <summary>
    /// Get all instance-level NPC grid bindings (ObjId -> grid names). In-memory only.
    /// </summary>
    public IReadOnlyDictionary<uint, List<string>> GetAllNpcInstanceGridBindings() => _npcInstanceGridBindings;

    /// <summary>
    /// Bind an NPC instance (by ObjId) to a height grid. In-memory only, not persisted.
    /// Used by auto-bind at spawn so only this specific instance is affected.
    /// </summary>
    public void BindNpcInstanceToGrid(uint objId, string gridName)
    {
        if (!_npcInstanceGridBindings.TryGetValue(objId, out var list))
        {
            list = [];
            _npcInstanceGridBindings[objId] = list;
        }

        if (gridName == "*")
        {
            list.Clear();
            list.Add("*");
        }
        else
        {
            if (list.Contains("*"))
                list.Remove("*");
            if (!list.Contains(gridName))
                list.Add(gridName);
        }

        Logger.Debug($"CollisionVolumeManager: Instance-bound NPC ObjId {objId} -> [{string.Join(", ", list)}]");
    }

    /// <summary>
    /// Remove all instance-level grid bindings for an NPC (e.g., on despawn/death).
    /// </summary>
    public void UnbindNpcInstance(uint objId)
    {
        if (_npcInstanceGridBindings.Remove(objId))
            Logger.Debug($"CollisionVolumeManager: Instance-unbound NPC ObjId {objId}");
    }

    /// <summary>
    /// Load NPC grid bindings from Data/CollisionVolumes/npc_grid_bindings.json
    /// Supports 3 formats with automatic migration:
    ///   v3 (current): {"templateId": ["Grid1", "Grid2", ...], ...}
    ///   v2 (named):   {"templateId": "gridName", ...}
    ///   v1 (legacy):  [templateId1, templateId2, ...]
    /// </summary>
    private void LoadNpcGridBindings()
    {
        var filePath = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes", "npc_grid_bindings.json");
        if (!File.Exists(filePath))
        {
            _npcGridBindings = [];
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);

            // Try v3 format first: {"templateId": ["Grid1", "Grid2"], ...}
            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<uint, List<string>>>(json);
                if (dict != null)
                {
                    _npcGridBindings = dict;
                    Logger.Info($"CollisionVolumeManager: Loaded {_npcGridBindings.Count} NPC grid bindings (multi-grid format)");
                    return;
                }
            }
            catch { /* not v3, try v2 */ }

            // Try v2 format: {"templateId": "gridName", ...} -> migrate to list
            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<uint, string>>(json);
                if (dict != null)
                {
                    _npcGridBindings = dict.ToDictionary(kvp => kvp.Key, kvp => new List<string> { kvp.Value });
                    Logger.Info($"CollisionVolumeManager: Loaded {_npcGridBindings.Count} NPC grid bindings (v2 named format, migrating)");
                    SaveNpcGridBindings();
                    return;
                }
            }
            catch { /* not v2, try v1 */ }

            // v1 legacy format: [templateId1, templateId2, ...] -> bind all to "*"
            var list = JsonConvert.DeserializeObject<List<uint>>(json);
            if (list != null)
            {
                _npcGridBindings = list.ToDictionary(id => id, _ => new List<string> { "*" });
                Logger.Info($"CollisionVolumeManager: Loaded {_npcGridBindings.Count} NPC grid bindings (v1 legacy format, migrating)");
                SaveNpcGridBindings();
                return;
            }

            _npcGridBindings = [];
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "CollisionVolumeManager: Failed to load npc_grid_bindings.json");
            _npcGridBindings = [];
        }
    }

    /// <summary>
    /// Save NPC grid bindings to Data/CollisionVolumes/npc_grid_bindings.json
    /// </summary>
    private void SaveNpcGridBindings()
    {
        var dataDir = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes");
        if (!Directory.Exists(dataDir))
            Directory.CreateDirectory(dataDir);

        var filePath = Path.Combine(dataDir, "npc_grid_bindings.json");

        try
        {
            // v3 format: {"templateId": ["Grid1", "Grid2"], ...}
            var sorted = _npcGridBindings.OrderBy(kvp => kvp.Key)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var json = JsonConvert.SerializeObject(sorted, Formatting.Indented);
            File.WriteAllText(filePath, json);
            Logger.Debug($"CollisionVolumeManager: Saved {_npcGridBindings.Count} NPC grid bindings");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "CollisionVolumeManager: Failed to save npc_grid_bindings.json");
        }
    }

    // ─────────────────────────────────────────────────────────
    // Floor Scanner
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Start floor scanning mode for an admin.
    /// While scanning, call RecordScanPosition() every tick to collect the player's position.
    /// </summary>
    public void StartScan(uint characterObjId, string worldName, string name, float pointSpacing = 1.0f)
    {
        _scanStates[characterObjId] = new FloorScanState
        {
            Name = name,
            WorldName = worldName,
            MinPointSpacing = pointSpacing
        };
    }

    /// <summary>
    /// Check if an admin is in scan mode
    /// </summary>
    public bool IsScanning(uint characterObjId) => _scanStates.ContainsKey(characterObjId);

    /// <summary>
    /// Get scan state for an admin
    /// </summary>
    public FloorScanState GetScanState(uint characterObjId)
    {
        return _scanStates.GetValueOrDefault(characterObjId);
    }

    /// <summary>
    /// Record a position during floor scanning.
    /// Only records if the player has moved at least MinPointSpacing from the last recorded position.
    /// Returns true if a new point was recorded.
    /// </summary>
    public bool RecordScanPosition(uint characterObjId, Vector3 position)
    {
        if (!_scanStates.TryGetValue(characterObjId, out var state))
            return false;

        // Check minimum distance from last recorded point
        if (state.Points.Count > 0)
        {
            var dx = position.X - state.LastRecordedPos.X;
            var dy = position.Y - state.LastRecordedPos.Y;
            var dz = position.Z - state.LastRecordedPos.Z;
            var distSq = dx * dx + dy * dy + dz * dz;
            if (distSq < state.MinPointSpacing * state.MinPointSpacing)
                return false;
        }

        state.Points.Add(position);
        state.LastRecordedPos = position;
        return true;
    }

    /// <summary>
    /// Cancel floor scanning
    /// </summary>
    public void CancelScan(uint characterObjId)
    {
        if (_scanStates.TryGetValue(characterObjId, out var state))
        {
            state.ScanTask?.Cancel();
            _scanStates.Remove(characterObjId);
        }
    }

    /// <summary>
    /// Finish floor scanning: cluster points by Z-height, compute convex hull per floor,
    /// create volumes, save to file.
    /// Returns the list of created volumes (one per detected floor).
    /// </summary>
    /// <param name="characterObjId">Admin character ObjId</param>
    /// <param name="minPointsPerFloor">Minimum points required to create a floor (default 10)</param>
    /// <param name="zTolerance">Maximum Z difference for same floor clustering (default 1.5m)</param>
    public List<CollisionVolume> FinishScan(uint characterObjId, int minPointsPerFloor = 10, float zTolerance = 1.5f)
    {
        if (!_scanStates.TryGetValue(characterObjId, out var state))
            return null;

        _scanStates.Remove(characterObjId);

        if (state.Points.Count < minPointsPerFloor)
            return null;

        // Ensure world file exists
        if (!_worldFiles.TryGetValue(state.WorldName, out var file))
        {
            file = new CollisionVolumeFile { WorldName = state.WorldName };
            _worldFiles[state.WorldName] = file;
        }

        // 1. Cluster points by Z-height (multi-floor detection)
        var floors = PolygonUtils.ClusterByFloor(state.Points, zTolerance);

        var createdVolumes = new List<CollisionVolume>();
        var floorIndex = 0;

        foreach (var floorPoints in floors)
        {
            if (floorPoints.Count < minPointsPerFloor)
            {
                floorIndex++;
                continue; // Skip floors with too few points (probably stairs/transition)
            }

            // 2. Compute convex hull from this floor's point cloud
            var hull = PolygonUtils.ConvexHull2D(floorPoints);

            // 3. Simplify hull to reduce vertex count (remove nearly-collinear vertices)
            hull = PolygonUtils.SimplifyHull(hull, 8f);

            if (hull.Count < 3)
            {
                floorIndex++;
                continue;
            }

            // 4. Set Z of each hull vertex to the average Z of nearby floor points
            var avgZ = floorPoints.Average(p => p.Z);
            for (var i = 0; i < hull.Count; i++)
            {
                // Find nearby floor points and use their average Z for this vertex
                var hv = hull[i];
                var nearbyZ = floorPoints
                    .Where(p => MathF.Abs(p.X - hv.X) < 3f && MathF.Abs(p.Y - hv.Y) < 3f)
                    .Select(p => p.Z)
                    .DefaultIfEmpty(avgZ)
                    .Average();
                hull[i] = new Vector3(hv.X, hv.Y, nearbyZ);
            }

            // 5. Create volume
            var floorSuffix = floors.Count > 1 ? $"_F{floorIndex}" : "";
            var volume = new CollisionVolume
            {
                Id = file.NextId++,
                Name = $"{state.Name}{floorSuffix}",
                GroupId = state.Name,
                FloorIndex = floorIndex,
                VolumeType = CollisionVolumeType.Floor,
                Shape = CollisionShapeType.Polygon,
                IsActive = true,
                Vertices = hull
            };

            PrepareVolume(volume);
            file.Volumes.Add(volume);
            createdVolumes.Add(volume);

            Logger.Info($"CollisionVolumeManager: Scan created floor '{volume.Name}' ID={volume.Id}, " +
                        $"Vertices={hull.Count}, AvgZ={avgZ:F1}, Points={floorPoints.Count}");

            floorIndex++;
        }

        if (createdVolumes.Count > 0)
        {
            RebuildSpatialIndex(state.WorldName);
            SaveWorldFile(state.WorldName);
        }

        return createdVolumes;
    }

    // ─────────────────────────────────────────────────────────
    // Height Grid
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Get the first HeightGrid for a world (for backward compatibility), or null if none loaded.
    /// If the world has multiple grids, returns the first one found.
    /// </summary>
    public HeightGridFile GetHeightGrid(string worldName)
    {
        // First try direct lookup (gridName == worldName, legacy case)
        if (_heightGrids.TryGetValue(worldName, out var grid))
            return grid;

        // Then check world index
        if (_worldGridIndex.TryGetValue(worldName, out var gridNames) && gridNames.Count > 0)
            return _heightGrids.GetValueOrDefault(gridNames[0]);

        return null;
    }

    /// <summary>
    /// Get a specific named grid by its GridName.
    /// </summary>
    public HeightGridFile GetHeightGridByName(string gridName)
    {
        return _heightGrids.GetValueOrDefault(gridName);
    }

    /// <summary>
    /// Get all grid names belonging to a world.
    /// </summary>
    public IReadOnlyList<string> GetGridNamesForWorld(string worldName)
    {
        return _worldGridIndex.TryGetValue(worldName, out var names) ? names : Array.Empty<string>();
    }

    /// <summary>
    /// Get all loaded grids (all worlds).
    /// </summary>
    public IReadOnlyDictionary<string, HeightGridFile> GetAllHeightGrids() => _heightGrids;

    /// <summary>
    /// Query ALL grids for a world for floor height at a position.
    /// Returns the best (closest to z) height from any grid, or null if no data.
    /// </summary>
    public float? GetGridHeight(string worldName, float x, float y, float z)
        => GetGridHeight(worldName, x, y, z, HeightGridFile.DefaultLookupTolerance);

    public float? GetGridHeight(string worldName, float x, float y, float z, float tolerance)
    {
        if (!_worldGridIndex.TryGetValue(worldName, out var gridNames))
            return null;

        float? bestHeight = null;
        var bestDist = float.MaxValue;

        foreach (var gn in gridNames)
        {
            if (!_heightGrids.TryGetValue(gn, out var grid))
                continue;

            var h = grid.GetBestFloorHeight(x, y, z, tolerance);
            if (h.HasValue)
            {
                var dist = MathF.Abs(h.Value - z);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestHeight = h;
                }
            }
        }

        return bestHeight;
    }

    /// <summary>
    /// Search ALL grids for a world at a position and return the best matching height
    /// AND the grid name. Used at NPC spawn to auto-bind NPCs to their grid.
    /// </summary>
    /// <returns>(height, gridName) or (null, null) if no grid covers this position.</returns>
    public (float? Height, string GridName) FindGridAtPosition(string worldName, float x, float y, float z)
    {
        if (!_worldGridIndex.TryGetValue(worldName, out var gridNames))
            return (null, null);

        float? bestHeight = null;
        string bestGridName = null;
        var bestDist = float.MaxValue;

        foreach (var gn in gridNames)
        {
            if (!_heightGrids.TryGetValue(gn, out var grid))
                continue;

            var h = grid.GetBestFloorHeight(x, y, z);
            if (h.HasValue)
            {
                var dist = MathF.Abs(h.Value - z);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestHeight = h;
                    bestGridName = gn;
                }
            }
        }

        return (bestHeight, bestGridName);
    }

    /// <summary>
    /// Check if a position has grid data (is walkable) in ANY grid for the world.
    /// </summary>
    public bool HasGridData(string worldName, float x, float y)
    {
        if (!_worldGridIndex.TryGetValue(worldName, out var gridNames))
            return false;

        foreach (var gn in gridNames)
        {
            if (_heightGrids.TryGetValue(gn, out var grid) && grid.HasData(x, y))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if an NPC movement from one position to another would cross
    /// from a grid cell WITH data to a cell WITHOUT data (implicit wall).
    /// Returns true if the movement should be blocked.
    /// Checks ALL grids for the world.
    /// </summary>
    public bool IsBlockedByGrid(string worldName, float fromX, float fromY, float toX, float toY)
    {
        if (!_worldGridIndex.TryGetValue(worldName, out var gridNames))
            return false;

        foreach (var gn in gridNames)
        {
            if (!_heightGrids.TryGetValue(gn, out var grid))
                continue;

            // Only check if the NPC is currently ON this grid
            if (!grid.IsWalkable(fromX, fromY))
                continue;

            // Destination is a wall if it has no data AND has fewer than 3 cardinal neighbors with data
            if (grid.IsWall(toX, toY))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a grid-bound NPC is blocked from moving to the destination.
    /// Grid-bound NPCs can only transition to terrain through a Floor collision volume.
    /// Returns true if the movement should be blocked.
    /// Checks instance binding (ObjId) first, then template binding.
    /// </summary>
    public bool IsGridBoundNpcBlocked(uint objId, uint templateId, string worldName, float fromX, float fromY, float fromZ, float toX, float toY, float toZ)
    {
        var bindings = GetEffectiveNpcGridBinding(objId, templateId);
        if (bindings == null || bindings.Count == 0)
            return false; // Not grid-bound — not our concern

        // Check if destination has a Floor collision volume → always allowed
        var destFloor = GetFloorHeight(worldName, toX, toY, toZ);
        if (destFloor.HasValue)
            return false;

        // Check if destination has grid data in ANY of the bound grids → allowed
        var destHasGrid = false;
        if (bindings.Contains("*"))
        {
            destHasGrid = HasGridData(worldName, toX, toY);
        }
        else
        {
            foreach (var gridName in bindings)
            {
                var grid = GetHeightGridByName(gridName);
                if (grid != null && grid.HasData(toX, toY))
                {
                    destHasGrid = true;
                    break;
                }
            }
        }

        if (destHasGrid)
            return false;

        // Destination has no floor and no grid data.
        // Only allow terrain transition if NPC is currently standing on a Floor (doorway).
        var currentFloor = GetFloorHeight(worldName, fromX, fromY, fromZ);
        if (currentFloor.HasValue)
            return false; // NPC is on a floor → allow transition to terrain

        // Block: grid-bound NPC trying to leave grid without going through a floor
        return true;
    }

    /// <summary>
    /// Rebuild the worldName -> gridName reverse index after loading or modifying grids.
    /// </summary>
    private void RebuildWorldGridIndex()
    {
        _worldGridIndex.Clear();
        foreach (var (gridName, grid) in _heightGrids)
        {
            if (!_worldGridIndex.TryGetValue(grid.WorldName, out var list))
            {
                list = [];
                _worldGridIndex[grid.WorldName] = list;
            }

            if (!list.Contains(gridName))
                list.Add(gridName);
        }
    }

    /// <summary>
    /// Start a grid scan for an admin. Records positions into the world's height grid.
    /// </summary>
    public void StartGridScan(uint characterObjId, string worldName, float cellSize = 1.0f, int scanRadius = 2, string gridName = null)
    {
        // Use gridName if provided, otherwise default to worldName (backward compatible)
        var effectiveGridName = string.IsNullOrWhiteSpace(gridName) ? worldName : gridName;

        // Ensure grid exists
        if (!_heightGrids.ContainsKey(effectiveGridName))
        {
            _heightGrids[effectiveGridName] = new HeightGridFile
            {
                WorldName = worldName,
                GridName = effectiveGridName,
                CellSize = cellSize
            };
            RebuildWorldGridIndex();
        }

        _gridScanStates[characterObjId] = new GridScanState
        {
            WorldName = worldName,
            GridName = effectiveGridName,
            CellSize = cellSize,
            ScanRadius = scanRadius
        };
    }

    /// <summary>
    /// Check if an admin is in grid scan mode.
    /// </summary>
    public bool IsGridScanning(uint characterObjId) => _gridScanStates.ContainsKey(characterObjId);

    /// <summary>
    /// Get grid scan state for an admin.
    /// </summary>
    public GridScanState GetGridScanState(uint characterObjId)
    {
        return _gridScanStates.GetValueOrDefault(characterObjId);
    }

    /// <summary>
    /// Record a position during grid scanning.
    /// Records on EVERY call — no minimum distance filter.
    /// Uses line interpolation between consecutive positions (no gaps from movement speed),
    /// and radius fill around each point (wider coverage per step).
    /// Also triggers on Z changes (stairs, ramps, elevators).
    /// </summary>
    public void RecordGridPosition(uint characterObjId, float x, float y, float z)
    {
        if (!_gridScanStates.TryGetValue(characterObjId, out var state))
            return;

        if (!_heightGrids.TryGetValue(state.GridName, out var grid))
            return;

        int newCells;
        if (state.LastRecordedPos != Vector3.Zero)
        {
            // Line interpolation from previous position to current — fills all cells in between
            newCells = grid.RecordLine(
                state.LastRecordedPos.X, state.LastRecordedPos.Y, state.LastRecordedPos.Z,
                x, y, z,
                state.ScanRadius);
        }
        else
        {
            // First recording — just fill with radius
            newCells = grid.RecordWithRadius(x, y, z, state.ScanRadius);
        }

        state.LastRecordedPos = new Vector3(x, y, z);
        state.NewCells += newCells;
    }

    /// <summary>
    /// Finish grid scanning — save the grid to disk.
    /// Returns (newCells, updatedCells, totalCells).
    /// </summary>
    public (int NewCells, int UpdatedCells, int GapsFilled, int TotalCells) FinishGridScan(uint characterObjId)
    {
        if (!_gridScanStates.TryGetValue(characterObjId, out var state))
            return (0, 0, 0, 0);

        state.ScanTask?.Cancel();
        _gridScanStates.Remove(characterObjId);

        if (!_heightGrids.TryGetValue(state.GridName, out var grid))
            return (0, 0, 0, 0);

        // Fill small gaps (1-cell holes surrounded by data) to improve stair/ramp coverage
        var filledGaps = grid.FillGaps();

        grid.LastUpdated = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        SaveHeightGrid(state.GridName);

        Logger.Info($"CollisionVolumeManager: Grid scan finished for {state.GridName} ({state.WorldName}) - " +
                    $"New={state.NewCells}, Updated={state.UpdatedCells}, GapsFilled={filledGaps}, Total={grid.CellCount}");

        return (state.NewCells, state.UpdatedCells, filledGaps, grid.CellCount);
    }

    /// <summary>
    /// Cancel grid scanning — discard session changes (grid data already merged is kept).
    /// </summary>
    public void CancelGridScan(uint characterObjId)
    {
        if (_gridScanStates.TryGetValue(characterObjId, out var state))
        {
            state.ScanTask?.Cancel();
            _gridScanStates.Remove(characterObjId);
        }
    }

    /// <summary>
    /// Load all height grid files from Data/CollisionVolumes/heightgrid_*.json.
    /// Auto-generated height grids (auto_*.json from the offline NavMeshTool) are intentionally NOT loaded —
    /// only manually recorded /cv grid sessions are honored.
    /// </summary>
    private void LoadHeightGrids()
    {
        var dataDir = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes");
        if (!Directory.Exists(dataDir))
            return;

        // Load manually created height grids (heightgrid_*.json) only.
        var files = Directory.GetFiles(dataDir, "heightgrid_*.json").ToList();

        Logger.Info($"CollisionVolumeManager: Found {files.Count} manual height grid files (auto-generated grids disabled)");

        foreach (var filePath in files)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var grid = JsonConvert.DeserializeObject<HeightGridFile>(json);
                if (grid == null)
                {
                    Logger.Warn($"CollisionVolumeManager: Failed to parse height grid {filePath}");
                    continue;
                }

                // Ensure GridName is set (backward compat: old files may not have it)
                if (string.IsNullOrWhiteSpace(grid.GridName))
                    grid.GridName = grid.WorldName;

                _heightGrids[grid.GridName] = grid;
                grid.EnsureSampleCounts(); // Legacy migration: fill in sample counts for older grids
                Logger.Info($"CollisionVolumeManager: Loaded height grid '{grid.GridName}' for {grid.WorldName} - {grid.CellCount} cells");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"CollisionVolumeManager: Error loading height grid {filePath}");
            }
        }

        RebuildWorldGridIndex();
    }

    /// <summary>
    /// Save a grid to disk by its grid name (key in _heightGrids).
    /// </summary>
    public void SaveHeightGrid(string gridName)
    {
        if (!_heightGrids.TryGetValue(gridName, out var grid))
            return;

        var dataDir = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes");
        if (!Directory.Exists(dataDir))
            Directory.CreateDirectory(dataDir);

        var filePath = Path.Combine(dataDir, $"heightgrid_{gridName}.json");

        // Backup existing file
        if (File.Exists(filePath))
        {
            try { File.Copy(filePath, filePath + ".bak", true); }
            catch { /* ignore */ }
        }

        try
        {
            var json = JsonConvert.SerializeObject(grid, Formatting.Indented, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            File.WriteAllText(filePath, json);
            Logger.Debug($"CollisionVolumeManager: Saved height grid '{gridName}' - {grid.CellCount} cells");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"CollisionVolumeManager: Failed to save height grid {filePath}");
        }
    }

    /// <summary>
    /// Delete a named grid from memory and disk.
    /// </summary>
    public bool DeleteHeightGrid(string gridName)
    {
        if (!_heightGrids.Remove(gridName))
            return false;

        RebuildWorldGridIndex();

        var filePath = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes", $"heightgrid_{gridName}.json");
        if (File.Exists(filePath))
        {
            try { File.Delete(filePath); }
            catch (Exception ex) { Logger.Error(ex, $"CollisionVolumeManager: Failed to delete {filePath}"); }
        }

        Logger.Info($"CollisionVolumeManager: Deleted height grid '{gridName}'");
        return true;
    }

    // ─────────────────────────────────────────────────────────
    // File I/O
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Save a world's collision volumes to its JSON file
    /// </summary>
    public void SaveWorldFile(string worldName)
    {
        if (!_worldFiles.TryGetValue(worldName, out var file))
            return;

        var dataDir = Path.Combine(FileManager.AppPath, "Data", "CollisionVolumes");
        if (!Directory.Exists(dataDir))
            Directory.CreateDirectory(dataDir);

        var filePath = Path.Combine(dataDir, $"{worldName}.json");

        // Create backup of existing file
        if (File.Exists(filePath))
        {
            var bakPath = filePath + ".bak";
            try { File.Copy(filePath, bakPath, true); }
            catch { /* ignore backup failure */ }
        }

        try
        {
            var json = JsonConvert.SerializeObject(file, Formatting.Indented, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Include
            });
            File.WriteAllText(filePath, json);
            Logger.Debug($"CollisionVolumeManager: Saved {file.Volumes.Count} volumes to {filePath}");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"CollisionVolumeManager: Failed to save {filePath}");
        }
    }

    /// <summary>
    /// Resolve a world name from a world template ID
    /// </summary>
    public static string GetWorldNameFromId(uint worldId)
    {
        var worldTemplate = WorldManager.Instance.WorldTemplates.Values
            .FirstOrDefault(w => w.Id == worldId);
        return worldTemplate?.Name ?? "main_world";
    }
}
