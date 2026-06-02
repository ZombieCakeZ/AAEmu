using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.World;

using Newtonsoft.Json;

using NLog;

namespace AAEmu.Game.Core.Managers.World;

/// <summary>
/// Loads editor-baked collision meshes (meshcache_&lt;world&gt;.bin + editor_&lt;world&gt;_&lt;cx&gt;_&lt;cy&gt;.json)
/// produced by AAEditor.Sandbox and exposes a per-world 64m-region spatial index of
/// <see cref="CollisionMeshInstance"/> records for height / blocker queries.
///
/// Pattern mirrors <see cref="CollisionVolumeManager"/> — Singleton + ILoadable, streaming
/// reader, per-world Dictionary&lt;(int RX, int RY), List&lt;T&gt;&gt; spatial index keyed by
/// WorldManager.REGION_SIZE (64m). Bin file is binary (AAMC magic, byte-for-byte mirror of
/// AAEditor.Core.Collision.MeshCacheWriter); per-cell JSON is Newtonsoft.
///
/// Phase 2 — load + index only. Query API (RaycastMesh / IsSegmentBlocked / SweepCapsule /
/// QueryFloorHeight / PointInMesh) lands in Phase 3 when NPC AI gets wired to consult it.
/// </summary>
public class MeshCollisionManager : Singleton<MeshCollisionManager>, ILoadable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const uint MeshCacheMagic = 0x434D4141u; // "AAMC" little-endian
    private const uint MeshCacheVersion = 1u;

    private const uint FlagIsTree = 1u << 0;
    private const uint FlagIsRock = 1u << 1;
    private const uint FlagNoCollision = 1u << 2;
    private const uint FlagTrunkOnly = 1u << 3;

    private const int RegionSize = WorldManager.REGION_SIZE; // 64m
    private const int SoftSpanWarn = 400;                    // cells; ~25km² covers any real-world AA asset

    /// <summary>Templates keyed by FNV-1a64 of normalized lowercase CGF path. Shared across instances and worlds.</summary>
    private readonly Dictionary<ulong, CollisionMesh> _templatesByHash = [];

    /// <summary>All loaded instances grouped by world name.</summary>
    private readonly Dictionary<string, List<CollisionMeshInstance>> _worldInstances = [];

    /// <summary>
    /// 64m-region spatial index per world. Mirrors CollisionVolumeManager._spatialIndex
    /// (D:/Vanilla/AAEmu.Game/Core/Managers/World/CollisionVolumeManager.cs:37) so debug
    /// overlays + region-walk math match.
    /// </summary>
    private readonly Dictionary<string, Dictionary<(int RX, int RY), List<CollisionMeshInstance>>> _spatialIndex = [];

    /// <summary>
    /// Overlay for instances whose WorldBounds span more cells than the spatial-index soft cap.
    /// Always unioned into GetInstancesInRegion results so very large buildings / cliffs are
    /// never silently dropped (the volume manager's spanX*spanY > 100 reject would hit them).
    /// </summary>
    private readonly Dictionary<string, List<CollisionMeshInstance>> _largeInstances = [];

    private bool _loaded;

    public void Load()
    {
        if (_loaded) return;

        _templatesByHash.Clear();
        _worldInstances.Clear();
        _spatialIndex.Clear();
        _largeInstances.Clear();

        var dataDir = Path.Combine(FileManager.AppPath, "Data", "CollisionMeshes");
        if (!Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
            Logger.Info("MeshCollisionManager: Created Data/CollisionMeshes/ directory (empty)");
            _loaded = true;
            return;
        }

        // 1) Load every meshcache_<world>.bin first so the JSON loader has templates to resolve.
        var binFiles = Directory.GetFiles(dataDir, "meshcache_*.bin");
        foreach (var binPath in binFiles)
        {
            try
            {
                var templates = ReadMeshCache(binPath);
                foreach (var (hash, tpl) in templates)
                {
                    if (!_templatesByHash.TryAdd(hash, tpl))
                    {
                        Logger.Warn($"MeshCollisionManager: duplicate template 0x{hash:X16} across meshcache files; keeping first");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"MeshCollisionManager: failed to read {Path.GetFileName(binPath)}");
            }

            // Match CollisionVolumeManager.cs:279-280 — per-file GC between large reads.
            GC.Collect(2, GCCollectionMode.Aggressive, true);
            GC.WaitForPendingFinalizers();
        }

        Logger.Info($"MeshCollisionManager: {_templatesByHash.Count} templates loaded from {binFiles.Length} meshcache files");

        // 2) Glob editor_<world>_<cx>_<cy>.json. Skip transient editor temp files.
        var jsonFiles = Directory.GetFiles(dataDir, "editor_*.json");
        var totalInstances = 0;
        var skippedExt = new[] { ".bak", ".tmp", ".partial", ".lock" };
        foreach (var jsonPath in jsonFiles)
        {
            var fileName = Path.GetFileName(jsonPath);
            if (skippedExt.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            try
            {
                var instances = ReadInstanceFile(jsonPath, out var worldName);
                if (instances.Count == 0) continue;

                if (!_worldInstances.TryGetValue(worldName, out var worldList))
                {
                    worldList = new List<CollisionMeshInstance>();
                    _worldInstances[worldName] = worldList;
                }
                worldList.AddRange(instances);
                totalInstances += instances.Count;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"MeshCollisionManager: failed to read {fileName}");
            }
        }

        // 3) Build spatial index per world.
        foreach (var worldName in _worldInstances.Keys.ToList())
            RebuildSpatialIndex(worldName);

        Logger.Info($"MeshCollisionManager: {totalInstances:N0} instances across {_worldInstances.Count} world(s)");
        _loaded = true;
    }

    public void Reload()
    {
        Logger.Info("MeshCollisionManager: Reload requested");
        _loaded = false;
        Load();
    }

    /// <summary>
    /// Reads a meshcache_&lt;world&gt;.bin file byte-for-byte matching
    /// AAEditor.Core.Collision.MeshCacheWriter.Write. Throws on bad magic or unsupported version.
    /// </summary>
    private static Dictionary<ulong, CollisionMesh> ReadMeshCache(string path)
    {
        var sizeKb = new FileInfo(path).Length / 1024;
        Logger.Info($"MeshCollisionManager: Loading {Path.GetFileName(path)} ({sizeKb:N0} KB)...");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

        var magic = br.ReadUInt32();
        if (magic != MeshCacheMagic)
            throw new InvalidDataException($"bad magic 0x{magic:X8} in {Path.GetFileName(path)} (expected 'AAMC')");
        var version = br.ReadUInt32();
        if (version != MeshCacheVersion)
            throw new InvalidDataException($"unsupported meshcache version {version} (expected {MeshCacheVersion})");

        var templateCount = br.ReadUInt32();
        var result = new Dictionary<ulong, CollisionMesh>((int)Math.Min(templateCount, int.MaxValue));

        for (var t = 0u; t < templateCount; t++)
        {
            var pathHash = br.ReadUInt64();
            var pathLen = br.ReadUInt32();
            var pathBytes = br.ReadBytes((int)pathLen);
            var cgfPath = Encoding.UTF8.GetString(pathBytes);

            var localMin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var localMax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

            var flags = br.ReadUInt32();
            var trunkCutoffLocalZ = br.ReadSingle();

            var vertCount = br.ReadUInt32();
            var vertices = new float[vertCount * 3];
            for (var i = 0; i < vertices.Length; i++) vertices[i] = br.ReadSingle();

            var triCount = br.ReadUInt32();
            var indices = new int[triCount * 3];
            for (var i = 0; i < indices.Length; i++) indices[i] = (int)br.ReadUInt32();

            var nodeCount = br.ReadUInt32();
            var nodes = new BvhNode[nodeCount];
            for (var i = 0; i < nodeCount; i++)
            {
                nodes[i] = new BvhNode
                {
                    Min = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    Max = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    LeftOrFirstTri = br.ReadInt32(),
                    RightOrTriCount = br.ReadInt32(),
                };
            }

            var template = new CollisionMesh
            {
                FileName = cgfPath,
                PathHash = pathHash,
                Vertices = vertices,
                Indices = indices,
                LocalMin = localMin,
                LocalMax = localMax,
                IsTree = (flags & FlagIsTree) != 0,
                IsRock = (flags & FlagIsRock) != 0,
                TrunkCutoffLocalZ = trunkCutoffLocalZ,
                BvhNodes = nodes,
            };

            if (!result.TryAdd(pathHash, template))
                Logger.Warn($"MeshCollisionManager: duplicate hash 0x{pathHash:X16} for '{cgfPath}' in {Path.GetFileName(path)}");
        }

        Logger.Info($"MeshCollisionManager: {result.Count} templates loaded from {Path.GetFileName(path)}");
        return result;
    }

    /// <summary>
    /// Streams an editor_&lt;world&gt;_&lt;cx&gt;_&lt;cy&gt;.json, resolves PathHashHex against
    /// <see cref="_templatesByHash"/>, computes WorldBounds, returns the instance list.
    /// Unresolved hashes are logged once per file then skipped.
    /// </summary>
    private List<CollisionMeshInstance> ReadInstanceFile(string path, out string worldName)
    {
        worldName = "";
        var result = new List<CollisionMeshInstance>();

        CollisionInstanceFile file;
        using (var stream = File.OpenRead(path))
        using (var reader = new StreamReader(stream))
        using (var jsonReader = new JsonTextReader(reader))
        {
            var serializer = new JsonSerializer();
            file = serializer.Deserialize<CollisionInstanceFile>(jsonReader);
        }
        if (file == null) return result;

        worldName = file.World ?? "";
        if (file.Instances.Count == 0) return result;

        var missingTemplates = new HashSet<ulong>();
        foreach (var dto in file.Instances)
        {
            if ((dto.Flags & FlagNoCollision) != 0) continue;

            if (!TryParseHash(dto.PathHashHex, out var hash))
            {
                Logger.Warn($"MeshCollisionManager: bad hash '{dto.PathHashHex}' in {Path.GetFileName(path)}");
                continue;
            }
            if (!_templatesByHash.TryGetValue(hash, out var template))
            {
                missingTemplates.Add(hash);
                continue;
            }

            var instance = new CollisionMeshInstance
            {
                Mesh = template,
                Position = new Vector3(dto.Pos[0], dto.Pos[1], dto.Pos[2]),
                R00 = dto.Rot[0], R01 = dto.Rot[1], R10 = dto.Rot[2], R11 = dto.Rot[3],
                Scale = dto.Scale <= 0f ? 1f : dto.Scale,
            };
            instance.ComputeWorldBounds();
            result.Add(instance);
        }

        if (missingTemplates.Count > 0)
            Logger.Warn($"MeshCollisionManager: {missingTemplates.Count} unresolved template hash(es) in {Path.GetFileName(path)} — meshcache out of date?");
        return result;
    }

    /// <summary>
    /// Rebuilds the 64m spatial index for one world. Mirrors
    /// CollisionVolumeManager.RebuildSpatialIndex (CollisionVolumeManager.cs:327) with a
    /// soft span cap — instances larger than <see cref="SoftSpanWarn"/> region cells
    /// fall into the <see cref="_largeInstances"/> overlay rather than being dropped.
    /// </summary>
    private void RebuildSpatialIndex(string worldName)
    {
        if (!_worldInstances.TryGetValue(worldName, out var instances)) return;

        var index = new Dictionary<(int RX, int RY), List<CollisionMeshInstance>>();
        var large = new List<CollisionMeshInstance>();

        foreach (var inst in instances)
        {
            var (minX, minY, maxX, maxY) = inst.WorldBounds;
            var minRx = (int)MathF.Floor(minX / RegionSize);
            var minRy = (int)MathF.Floor(minY / RegionSize);
            var maxRx = (int)MathF.Floor(maxX / RegionSize);
            var maxRy = (int)MathF.Floor(maxY / RegionSize);
            var spanX = maxRx - minRx + 1;
            var spanY = maxRy - minRy + 1;

            if (spanX <= 0 || spanY <= 0) continue;

            if (spanX * spanY > SoftSpanWarn)
            {
                large.Add(inst);
                Logger.Trace($"MeshCollisionManager: large instance spans {spanX}x{spanY} regions, routed to overlay (template hash 0x{inst.Mesh?.PathHash:X16})");
                continue;
            }

            for (var ry = minRy; ry <= maxRy; ry++)
            {
                for (var rx = minRx; rx <= maxRx; rx++)
                {
                    var key = (RX: rx, RY: ry);
                    if (!index.TryGetValue(key, out var list))
                    {
                        list = new List<CollisionMeshInstance>();
                        index[key] = list;
                    }
                    list.Add(inst);
                }
            }
        }

        _spatialIndex[worldName] = index;
        _largeInstances[worldName] = large;
        Logger.Info($"MeshCollisionManager: spatial index for {worldName}: {index.Count} grid cells, {instances.Count:N0} instances ({large.Count} routed to large-overlay)");
    }

    /// <summary>Try parse "0xABCDEF..." or "ABCDEF..." into a u64.</summary>
    private static bool TryParseHash(string hex, out ulong value)
    {
        if (string.IsNullOrEmpty(hex)) { value = 0; return false; }
        var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.Substring(2) : hex;
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public bool IsLoaded => _loaded;

    /// <summary>
    /// Phase 8 hook — returns the loaded instance list for a world so PhysicsManager can
    /// register each as a Jitter DynamicTree proxy. Returns empty when not loaded or unknown world.
    /// </summary>
    public IReadOnlyList<CollisionMeshInstance> GetWorldInstances(string worldName)
    {
        if (_worldInstances.TryGetValue(worldName, out var list)) return list;
        return Array.Empty<CollisionMeshInstance>();
    }

    /// <summary>
    /// Phase 8 hook — raycast against a single CollisionMeshInstance using the existing
    /// per-template BVH + Möller-Trumbore. Origin/direction in AA world space, Z-up. Returns
    /// the parametric hit-t along <paramref name="direction"/> within [0..maxT], or null.
    /// </summary>
    public float? RaycastInstance(CollisionMeshInstance inst, Vector3 origin, Vector3 direction, float maxT,
        MeshQueryFlags flags = MeshQueryFlags.None)
    {
        if (inst?.Mesh == null) return null;
        return FirstRayHit(inst, origin, direction, 0f, maxT, flags);
    }

    // ===== Phase 3 — query API (BVH-accelerated where available) =============

    [Flags]
    public enum MeshQueryFlags
    {
        None = 0,
        /// <summary>Reject triangles whose ALL vertices sit above CollisionMesh.TrunkCutoffLocalZ on IsTree meshes.</summary>
        SkipFoliage = 1 << 0,
    }

    /// <summary>All instances overlapping the requested 64m region (plus the overlay for very large instances).</summary>
    public IReadOnlyList<CollisionMeshInstance> GetInstancesInRegion(string worldName, int rx, int ry)
    {
        var result = new List<CollisionMeshInstance>();
        if (_spatialIndex.TryGetValue(worldName, out var index) && index.TryGetValue((rx, ry), out var bucket))
            result.AddRange(bucket);
        if (_largeInstances.TryGetValue(worldName, out var overlay))
            result.AddRange(overlay);
        return result;
    }

    /// <summary>
    /// Returns true if the segment from→to is blocked by any loaded mesh triangle in this world.
    /// When <paramref name="bodyRadius"/> > 0, also runs two parallel offset segments at
    /// ±radius perpendicular to the motion. When <paramref name="npcHeight"/> > 0, the test is
    /// repeated at TWO Z heights (foot Z + 0.1m and foot Z + npcHeight - 0.1m) so low parapets
    /// AND elevated railings both block — single-Z testing previously let NPCs walk through any
    /// wall whose mesh sat outside the eye-height band. from.Z is interpreted as the NPC's foot Z.
    /// </summary>
    public bool IsLineBlockedByMesh(string worldName, Vector3 from, Vector3 to,
        MeshQueryFlags flags = MeshQueryFlags.SkipFoliage, float bodyRadius = 0f, float npcHeight = 0f)
    {
        if (!_loaded) return false;
        if (!_spatialIndex.TryGetValue(worldName, out var index)) return false;

        if (npcHeight > 0f)
        {
            // Capsule sampling: low Z catches knee-height walls, high Z catches overhead railings.
            // A wall mesh spanning [footZ..footZ+ceiling] is hit by at least one sample as long
            // as ceiling is wider than the two-sample stride.
            var lowDz = new Vector3(0f, 0f, 0.1f);
            var highDz = new Vector3(0f, 0f, npcHeight - 0.1f);
            if (IsLineBlockedAtZ(worldName, from + lowDz, to + lowDz, flags, bodyRadius, index)) return true;
            if (IsLineBlockedAtZ(worldName, from + highDz, to + highDz, flags, bodyRadius, index)) return true;
            return false;
        }

        return IsLineBlockedAtZ(worldName, from, to, flags, bodyRadius, index);
    }

    private bool IsLineBlockedAtZ(string worldName, Vector3 from, Vector3 to,
        MeshQueryFlags flags, float bodyRadius,
        Dictionary<(int RX, int RY), List<CollisionMeshInstance>> index)
    {

        // Broad phase: widen by bodyRadius so neighbour-cell instances grazed by the offset rays are included.
        var minRx = (int)MathF.Floor((MathF.Min(from.X, to.X) - bodyRadius) / RegionSize);
        var maxRx = (int)MathF.Floor((MathF.Max(from.X, to.X) + bodyRadius) / RegionSize);
        var minRy = (int)MathF.Floor((MathF.Min(from.Y, to.Y) - bodyRadius) / RegionSize);
        var maxRy = (int)MathF.Floor((MathF.Max(from.Y, to.Y) + bodyRadius) / RegionSize);
        var segMinX = MathF.Min(from.X, to.X) - bodyRadius;
        var segMaxX = MathF.Max(from.X, to.X) + bodyRadius;
        var segMinY = MathF.Min(from.Y, to.Y) - bodyRadius;
        var segMaxY = MathF.Max(from.Y, to.Y) + bodyRadius;
        var segMinZ = MathF.Min(from.Z, to.Z);
        var segMaxZ = MathF.Max(from.Z, to.Z);

        // Compute perpendicular offset once. Rotate move direction 90° CCW, scale by radius.
        var perpX = 0f;
        var perpY = 0f;
        if (bodyRadius > 0f)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > 1e-4f)
            {
                perpX = -dy / len * bodyRadius;
                perpY = dx / len * bodyRadius;
            }
        }

        // Dedup instances that straddle multiple region cells.
        var seen = new HashSet<CollisionMeshInstance>();

        for (var ry = minRy; ry <= maxRy; ry++)
        {
            for (var rx = minRx; rx <= maxRx; rx++)
            {
                if (!index.TryGetValue((rx, ry), out var bucket)) continue;
                foreach (var inst in bucket)
                {
                    if (!seen.Add(inst)) continue;
                    if (RejectByWorldBounds(inst, segMinX, segMinY, segMaxX, segMaxY, segMinZ, segMaxZ)) continue;
                    if (InstanceBlocksAnyParallel(inst, from, to, flags, perpX, perpY)) return true;
                }
            }
        }

        if (_largeInstances.TryGetValue(worldName, out var overlay))
        {
            foreach (var inst in overlay)
            {
                if (!seen.Add(inst)) continue;
                if (RejectByWorldBounds(inst, segMinX, segMinY, segMaxX, segMaxY, segMinZ, segMaxZ)) continue;
                if (InstanceBlocksAnyParallel(inst, from, to, flags, perpX, perpY)) return true;
            }
        }

        return false;
    }

    private static bool InstanceBlocksAnyParallel(CollisionMeshInstance inst, Vector3 from, Vector3 to,
        MeshQueryFlags flags, float perpX, float perpY)
    {
        if (InstanceBlocksSegment(inst, from, to, flags)) return true;
        if (perpX == 0f && perpY == 0f) return false;
        var off = new Vector3(perpX, perpY, 0f);
        if (InstanceBlocksSegment(inst, from + off, to + off, flags)) return true;
        if (InstanceBlocksSegment(inst, from - off, to - off, flags)) return true;
        return false;
    }

    /// <summary>
    /// Mesh surface Z at (x,y) closest to <paramref name="probeZ"/> inside the asymmetric slab
    /// [probeZ - maxBelow, probeZ + maxAbove]. Mirrors CollisionVolumeManager.GetGridHeight's
    /// NEAREST-Z disambiguation (CollisionVolumeManager.cs:1816-1832), so on a 2-story building
    /// an NPC at Z=100.5 finds the ground floor (Z=100) instead of teleporting up to the upper
    /// floor (Z=104) — the previous MAX-Z bias was the root of the "90° wall climbing" bug.
    ///
    /// maxAbove defaults small (1.5m): NPC almost never wants to snap up through a ceiling but
    /// 1.5m headroom lets him stand on the surface he's just touched. maxBelow is generous
    /// (6m) because integration drift / animation root-motion routinely puts NPCs a couple
    /// meters above their actual standing surface.
    /// </summary>
    public float? QueryNearestFloorHeight(string worldName, float x, float y, float probeZ,
        float maxAbove = 1.5f, float maxBelow = 6f)
    {
        if (!_loaded) return null;
        if (!_spatialIndex.TryGetValue(worldName, out var index)) return null;

        var rx = (int)MathF.Floor(x / RegionSize);
        var ry = (int)MathF.Floor(y / RegionSize);
        var slabMin = probeZ - maxBelow;
        var slabMax = probeZ + maxAbove;

        float? best = null;
        var bestDist = float.MaxValue;

        if (index.TryGetValue((rx, ry), out var bucket))
            CollectNearestHitsInBucket(bucket, x, y, probeZ, slabMin, slabMax, ref best, ref bestDist);
        if (_largeInstances.TryGetValue(worldName, out var overlay))
            CollectNearestHitsInBucket(overlay, x, y, probeZ, slabMin, slabMax, ref best, ref bestDist);

        return best;
    }

    /// <summary>
    /// Gravity / "where would I land" semantic: highest mesh surface Z at (x,y) that lies
    /// AT OR BELOW <paramref name="probeZ"/>, within <paramref name="maxDrop"/> meters. Used
    /// after a knockback/launch when the NPC's resolved Z is far above ground and we want to
    /// snap to whatever's actually under his feet — bridge, rooftop, terrain stand-in.
    /// </summary>
    public float? GetClosestFloorBelow(string worldName, float x, float y, float probeZ, float maxDrop = 50f)
    {
        if (!_loaded) return null;
        if (!_spatialIndex.TryGetValue(worldName, out var index)) return null;

        var rx = (int)MathF.Floor(x / RegionSize);
        var ry = (int)MathF.Floor(y / RegionSize);
        var floorMin = probeZ - maxDrop;
        var origin = new Vector3(x, y, probeZ + 0.001f);
        var dir = new Vector3(0f, 0f, -1f);
        var maxT = probeZ + 0.001f - floorMin;

        float? best = null;
        if (index.TryGetValue((rx, ry), out var bucket))
            best = SamplePointDownwardInBucket(bucket, origin, dir, x, y, floorMin, probeZ + 0.001f, maxT, best);
        if (_largeInstances.TryGetValue(worldName, out var overlay))
            best = SamplePointDownwardInBucket(overlay, origin, dir, x, y, floorMin, probeZ + 0.001f, maxT, best);
        return best;
    }

    private static void CollectNearestHitsInBucket(IReadOnlyList<CollisionMeshInstance> bucket,
        float x, float y, float probeZ, float slabMin, float slabMax,
        ref float? best, ref float bestDist)
    {
        foreach (var inst in bucket)
        {
            if (inst.WorldMaxZ < slabMin || inst.WorldMinZ > slabMax) continue;
            var (mnx, mny, mxx, mxy) = inst.WorldBounds;
            if (x < mnx || x > mxx || y < mny || y > mxy) continue;

            CollectInstanceNearestHits(inst, x, y, probeZ, slabMin, slabMax, ref best, ref bestDist);
        }
    }

    /// <summary>
    /// Walk every triangle of the instance, keep the hit Z minimising |hitZ - probeZ| within the slab.
    /// Linear over triangles (BVH first-hit short-circuit would give us only the highest or lowest
    /// hit, not the nearest-to-probeZ). Per-instance broad-phase already rejected most candidates.
    /// </summary>
    private static void CollectInstanceNearestHits(CollisionMeshInstance inst,
        float x, float y, float probeZ, float slabMin, float slabMax,
        ref float? best, ref float bestDist)
    {
        var originLocal = WorldToLocal(inst, new Vector3(x, y, probeZ));
        var dirLocal = WorldToLocalDir(inst, new Vector3(0f, 0f, -1f));
        var verts = inst.Mesh.Vertices;
        var idx = inst.Mesh.Indices;
        var triCount = idx.Length / 3;
        for (var t = 0; t < triCount; t++)
        {
            var v0 = ReadVertex(verts, idx[t * 3]);
            var v1 = ReadVertex(verts, idx[t * 3 + 1]);
            var v2 = ReadVertex(verts, idx[t * 3 + 2]);
            // tMin/tMax = full range — convert hit param to world Z by walking the inverse transform.
            if (!RayTriangle(originLocal, dirLocal, v0, v1, v2, float.NegativeInfinity, float.PositiveInfinity, out var tHit))
                continue;
            // World hit Z = probeZ + dir.Z * tHit; dir.Z = -1 ⇒ hitZ = probeZ - tHit (works even when Scale != 1
            // because tHit is in dir-units which we built from the same WorldToLocalDir-inverted axis).
            var localHitZ = originLocal.Z + dirLocal.Z * tHit;
            var hitZ = inst.Position.Z + localHitZ * inst.Scale;
            if (hitZ < slabMin || hitZ > slabMax) continue;
            var dist = MathF.Abs(hitZ - probeZ);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = hitZ;
            }
        }
    }

    private static float? SamplePointDownwardInBucket(IReadOnlyList<CollisionMeshInstance> bucket,
        Vector3 origin, Vector3 dir, float x, float y, float slabMin, float slabMax, float maxT, float? best)
    {
        foreach (var inst in bucket)
        {
            if (inst.WorldMaxZ < slabMin || inst.WorldMinZ > slabMax) continue;
            var (mnx, mny, mxx, mxy) = inst.WorldBounds;
            if (x < mnx || x > mxx || y < mny || y > mxy) continue;

            var hitT = FirstRayHit(inst, origin, dir, 0f, maxT, MeshQueryFlags.None);
            if (!hitT.HasValue) continue;

            var hitZ = origin.Z + dir.Z * hitT.Value;
            if (hitZ < slabMin || hitZ > slabMax) continue;
            if (!best.HasValue || hitZ > best.Value) best = hitZ;
        }
        return best;
    }

    /// <summary>Inside-mesh parity test via upward-ray triangle crossings — debug/admin only, no BVH speed-up.</summary>
    public bool PointInMesh(string worldName, float x, float y, float z)
    {
        if (!_loaded) return false;
        if (!_spatialIndex.TryGetValue(worldName, out var index)) return false;

        var rx = (int)MathF.Floor(x / RegionSize);
        var ry = (int)MathF.Floor(y / RegionSize);
        if (!index.TryGetValue((rx, ry), out var bucket)) return false;

        var origin = new Vector3(x, y, z);
        var dir = new Vector3(0f, 0f, 1f);
        foreach (var inst in bucket)
        {
            if (z < inst.WorldMinZ || z > inst.WorldMaxZ) continue;
            var (mnx, mny, mxx, mxy) = inst.WorldBounds;
            if (x < mnx || x > mxx || y < mny || y > mxy) continue;
            var crossings = CountTriangleCrossings(inst, origin, dir, 0f, float.MaxValue);
            if ((crossings & 1) == 1) return true;
        }
        return false;
    }

    // ===== Internal traversal ================================================

    private static bool RejectByWorldBounds(CollisionMeshInstance inst,
        float segMinX, float segMinY, float segMaxX, float segMaxY, float segMinZ, float segMaxZ)
    {
        var (mnx, mny, mxx, mxy) = inst.WorldBounds;
        if (segMaxX < mnx || segMinX > mxx) return true;
        if (segMaxY < mny || segMinY > mxy) return true;
        if (segMaxZ < inst.WorldMinZ || segMinZ > inst.WorldMaxZ) return true;
        return false;
    }

    private static float? SamplePointInBucket(IReadOnlyList<CollisionMeshInstance> bucket,
        Vector3 origin, Vector3 dir, float x, float y, float slabMin, float slabMax, float maxT, float? best)
    {
        foreach (var inst in bucket)
        {
            if (inst.WorldMaxZ < slabMin || inst.WorldMinZ > slabMax) continue;
            var (mnx, mny, mxx, mxy) = inst.WorldBounds;
            if (x < mnx || x > mxx || y < mny || y > mxy) continue;

            var hitT = FirstRayHit(inst, origin, dir, 0f, maxT, MeshQueryFlags.None);
            if (!hitT.HasValue) continue;

            var hitZ = origin.Z + dir.Z * hitT.Value;
            if (hitZ < slabMin || hitZ > slabMax) continue;
            if (!best.HasValue || hitZ > best.Value) best = hitZ;
        }
        return best;
    }

    /// <summary>
    /// Tests whether the world-space segment from→to is blocked by any triangle of this instance.
    /// Uses the per-template BVH when present (built by the editor), falls back to a linear scan
    /// otherwise. The query is transformed into the mesh's local space once and the rest is local.
    /// </summary>
    private static bool InstanceBlocksSegment(CollisionMeshInstance inst, Vector3 from, Vector3 to, MeshQueryFlags flags)
    {
        var fromLocal = WorldToLocal(inst, from);
        var toLocal = WorldToLocal(inst, to);
        var dir = toLocal - fromLocal;
        var skipFoliage = (flags & MeshQueryFlags.SkipFoliage) != 0 && inst.Mesh.IsTree;
        var trunkCutoff = inst.Mesh.TrunkCutoffLocalZ;

        if (inst.Mesh.BvhNodes.Length > 0)
            return BvhAnyHit(inst.Mesh, fromLocal, dir, skipFoliage, trunkCutoff, 0f, 1f);

        return LinearAnyHit(inst.Mesh, fromLocal, dir, skipFoliage, trunkCutoff, 0f, 1f);
    }

    private static float? FirstRayHit(CollisionMeshInstance inst, Vector3 origin, Vector3 dir, float tMin, float tMax, MeshQueryFlags flags)
    {
        var originLocal = WorldToLocal(inst, origin);
        var dirLocal = WorldToLocalDir(inst, dir);
        var skipFoliage = (flags & MeshQueryFlags.SkipFoliage) != 0 && inst.Mesh.IsTree;
        var trunkCutoff = inst.Mesh.TrunkCutoffLocalZ;

        if (inst.Mesh.BvhNodes.Length > 0)
            return BvhFirstHit(inst.Mesh, originLocal, dirLocal, skipFoliage, trunkCutoff, tMin, tMax);

        return LinearFirstHit(inst.Mesh, originLocal, dirLocal, skipFoliage, trunkCutoff, tMin, tMax);
    }

    private static int CountTriangleCrossings(CollisionMeshInstance inst, Vector3 origin, Vector3 dir, float tMin, float tMax)
    {
        var originLocal = WorldToLocal(inst, origin);
        var dirLocal = WorldToLocalDir(inst, dir);
        var verts = inst.Mesh.Vertices;
        var idx = inst.Mesh.Indices;
        var crossings = 0;
        var triCount = idx.Length / 3;
        for (var t = 0; t < triCount; t++)
        {
            var v0 = ReadVertex(verts, idx[t * 3]);
            var v1 = ReadVertex(verts, idx[t * 3 + 1]);
            var v2 = ReadVertex(verts, idx[t * 3 + 2]);
            if (RayTriangle(originLocal, dirLocal, v0, v1, v2, tMin, tMax, out _)) crossings++;
        }
        return crossings;
    }

    private static bool BvhAnyHit(CollisionMesh mesh, Vector3 origin, Vector3 dir,
        bool skipFoliage, float trunkCutoff, float tMin, float tMax)
    {
        var nodes = mesh.BvhNodes;
        Span<int> stack = stackalloc int[64];
        var sp = 0;
        stack[sp++] = 0;

        var invDir = new Vector3(
            dir.X == 0 ? float.PositiveInfinity : 1f / dir.X,
            dir.Y == 0 ? float.PositiveInfinity : 1f / dir.Y,
            dir.Z == 0 ? float.PositiveInfinity : 1f / dir.Z);

        while (sp > 0)
        {
            var n = nodes[stack[--sp]];
            if (!RaySlab(origin, invDir, n.Min, n.Max, tMin, tMax)) continue;
            if (skipFoliage && n.Min.Z > trunkCutoff) continue;

            if (n.IsLeaf)
            {
                if (LinearAnyHitRange(mesh, origin, dir, skipFoliage, trunkCutoff, tMin, tMax, n.FirstTri, n.TriCount))
                    return true;
            }
            else
            {
                if (sp < stack.Length) stack[sp++] = n.LeftOrFirstTri;
                if (sp < stack.Length) stack[sp++] = n.RightOrTriCount;
            }
        }
        return false;
    }

    private static float? BvhFirstHit(CollisionMesh mesh, Vector3 origin, Vector3 dir,
        bool skipFoliage, float trunkCutoff, float tMin, float tMax)
    {
        var nodes = mesh.BvhNodes;
        Span<int> stack = stackalloc int[64];
        var sp = 0;
        stack[sp++] = 0;

        var invDir = new Vector3(
            dir.X == 0 ? float.PositiveInfinity : 1f / dir.X,
            dir.Y == 0 ? float.PositiveInfinity : 1f / dir.Y,
            dir.Z == 0 ? float.PositiveInfinity : 1f / dir.Z);

        float? best = null;
        while (sp > 0)
        {
            var n = nodes[stack[--sp]];
            if (!RaySlab(origin, invDir, n.Min, n.Max, tMin, tMax)) continue;
            if (skipFoliage && n.Min.Z > trunkCutoff) continue;

            if (n.IsLeaf)
            {
                var hit = LinearFirstHitRange(mesh, origin, dir, skipFoliage, trunkCutoff, tMin, tMax, n.FirstTri, n.TriCount);
                if (hit.HasValue && (!best.HasValue || hit.Value < best.Value)) best = hit;
            }
            else
            {
                if (sp < stack.Length) stack[sp++] = n.LeftOrFirstTri;
                if (sp < stack.Length) stack[sp++] = n.RightOrTriCount;
            }
        }
        return best;
    }

    private static bool LinearAnyHit(CollisionMesh mesh, Vector3 origin, Vector3 dir,
        bool skipFoliage, float trunkCutoff, float tMin, float tMax)
        => LinearAnyHitRange(mesh, origin, dir, skipFoliage, trunkCutoff, tMin, tMax, 0, mesh.Indices.Length / 3);

    private static float? LinearFirstHit(CollisionMesh mesh, Vector3 origin, Vector3 dir,
        bool skipFoliage, float trunkCutoff, float tMin, float tMax)
        => LinearFirstHitRange(mesh, origin, dir, skipFoliage, trunkCutoff, tMin, tMax, 0, mesh.Indices.Length / 3);

    private static bool LinearAnyHitRange(CollisionMesh mesh, Vector3 origin, Vector3 dir,
        bool skipFoliage, float trunkCutoff, float tMin, float tMax, int firstTri, int triCount)
    {
        var verts = mesh.Vertices;
        var idx = mesh.Indices;
        var end = firstTri + triCount;
        for (var t = firstTri; t < end; t++)
        {
            var i0 = idx[t * 3];
            var i1 = idx[t * 3 + 1];
            var i2 = idx[t * 3 + 2];
            if (skipFoliage &&
                verts[i0 * 3 + 2] > trunkCutoff &&
                verts[i1 * 3 + 2] > trunkCutoff &&
                verts[i2 * 3 + 2] > trunkCutoff) continue;
            var v0 = ReadVertex(verts, i0);
            var v1 = ReadVertex(verts, i1);
            var v2 = ReadVertex(verts, i2);
            if (RayTriangle(origin, dir, v0, v1, v2, tMin, tMax, out _)) return true;
        }
        return false;
    }

    private static float? LinearFirstHitRange(CollisionMesh mesh, Vector3 origin, Vector3 dir,
        bool skipFoliage, float trunkCutoff, float tMin, float tMax, int firstTri, int triCount)
    {
        var verts = mesh.Vertices;
        var idx = mesh.Indices;
        var end = firstTri + triCount;
        float? best = null;
        for (var t = firstTri; t < end; t++)
        {
            var i0 = idx[t * 3];
            var i1 = idx[t * 3 + 1];
            var i2 = idx[t * 3 + 2];
            if (skipFoliage &&
                verts[i0 * 3 + 2] > trunkCutoff &&
                verts[i1 * 3 + 2] > trunkCutoff &&
                verts[i2 * 3 + 2] > trunkCutoff) continue;
            var v0 = ReadVertex(verts, i0);
            var v1 = ReadVertex(verts, i1);
            var v2 = ReadVertex(verts, i2);
            if (RayTriangle(origin, dir, v0, v1, v2, tMin, tMax, out var hitT))
            {
                if (!best.HasValue || hitT < best.Value) best = hitT;
            }
        }
        return best;
    }

    /// <summary>Möller-Trumbore ray-triangle. Backface-cull OFF (CGF triangles aren't guaranteed CCW).</summary>
    private static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2,
        float tMin, float tMax, out float t)
    {
        t = 0f;
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = Vector3.Cross(dir, edge2);
        var det = Vector3.Dot(edge1, h);
        if (MathF.Abs(det) < 1e-7f) return false;
        var invDet = 1f / det;
        var s = origin - v0;
        var u = Vector3.Dot(s, h) * invDet;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(s, edge1);
        var v = Vector3.Dot(dir, q) * invDet;
        if (v < 0f || u + v > 1f) return false;
        var hitT = Vector3.Dot(edge2, q) * invDet;
        if (hitT < tMin || hitT > tMax) return false;
        t = hitT;
        return true;
    }

    /// <summary>Ray-vs-AABB slab test (uses 1/dir to avoid per-call division).</summary>
    private static bool RaySlab(Vector3 origin, Vector3 invDir, Vector3 min, Vector3 max, float tMin, float tMax)
    {
        var tx1 = (min.X - origin.X) * invDir.X;
        var tx2 = (max.X - origin.X) * invDir.X;
        tMin = MathF.Max(tMin, MathF.Min(tx1, tx2));
        tMax = MathF.Min(tMax, MathF.Max(tx1, tx2));
        var ty1 = (min.Y - origin.Y) * invDir.Y;
        var ty2 = (max.Y - origin.Y) * invDir.Y;
        tMin = MathF.Max(tMin, MathF.Min(ty1, ty2));
        tMax = MathF.Min(tMax, MathF.Max(ty1, ty2));
        var tz1 = (min.Z - origin.Z) * invDir.Z;
        var tz2 = (max.Z - origin.Z) * invDir.Z;
        tMin = MathF.Max(tMin, MathF.Min(tz1, tz2));
        tMax = MathF.Min(tMax, MathF.Max(tz1, tz2));
        return tMax >= MathF.Max(tMin, 0f);
    }

    /// <summary>
    /// Inverse of CollisionMeshInstance.TransformVertex — maps a world point into local-mesh space.
    /// R is orthonormal in XY (rotation by yaw + uniform XY scale 1), so transpose suffices for the inverse.
    /// </summary>
    private static Vector3 WorldToLocal(CollisionMeshInstance inst, Vector3 world)
    {
        var dx = world.X - inst.Position.X;
        var dy = world.Y - inst.Position.Y;
        var lx = inst.R00 * dx + inst.R10 * dy;
        var ly = inst.R01 * dx + inst.R11 * dy;
        var lz = inst.Scale != 0f ? (world.Z - inst.Position.Z) / inst.Scale : 0f;
        return new Vector3(lx, ly, lz);
    }

    /// <summary>Like WorldToLocal but for a direction vector (no translation).</summary>
    private static Vector3 WorldToLocalDir(CollisionMeshInstance inst, Vector3 dir)
    {
        var lx = inst.R00 * dir.X + inst.R10 * dir.Y;
        var ly = inst.R01 * dir.X + inst.R11 * dir.Y;
        var lz = inst.Scale != 0f ? dir.Z / inst.Scale : 0f;
        return new Vector3(lx, ly, lz);
    }

    private static Vector3 ReadVertex(float[] verts, int vIdx)
        => new(verts[vIdx * 3], verts[vIdx * 3 + 1], verts[vIdx * 3 + 2]);

    // -------- /meshstats consumed surface -----------------------------------

    public int TemplateCount => _templatesByHash.Count;

    public long TotalTriangleCount
    {
        get
        {
            long sum = 0;
            foreach (var t in _templatesByHash.Values) sum += t.TriangleCount;
            return sum;
        }
    }

    public IReadOnlyDictionary<string, int> InstanceCountsPerWorld
    {
        get
        {
            var d = new Dictionary<string, int>(_worldInstances.Count);
            foreach (var (k, v) in _worldInstances) d[k] = v.Count;
            return d;
        }
    }

    /// <summary>The top-N (RX,RY) cells by instance count in the requested world.</summary>
    public IEnumerable<((int RX, int RY) Cell, int Count)> TopCellsByInstanceCount(string worldName, int top)
    {
        if (!_spatialIndex.TryGetValue(worldName, out var index)) yield break;
        foreach (var (key, list) in index.OrderByDescending(kv => kv.Value.Count).Take(top))
            yield return (key, list.Count);
    }

    /// <summary>
    /// Estimates total heap footprint of the loaded data — verts + indices + BVH nodes for
    /// every template, plus the per-instance fixed cost (≈ 96 bytes including object header).
    /// Useful for /meshstats sanity checks before Phase 3 ships per-tick queries.
    /// </summary>
    public long EstimateMemoryBytes()
    {
        long bytes = 0;
        foreach (var t in _templatesByHash.Values)
        {
            bytes += (long)t.Vertices.Length * sizeof(float);
            bytes += (long)t.Indices.Length * sizeof(int);
            bytes += (long)t.BvhNodes.Length * 32; // sizeof(BvhNode) = 32
        }
        foreach (var (_, list) in _worldInstances)
            bytes += list.Count * 96L;
        return bytes;
    }
}
