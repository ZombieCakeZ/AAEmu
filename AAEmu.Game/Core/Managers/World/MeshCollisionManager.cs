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

    // -------- Read-side query helpers (Phase 3 wires AI into these) ----------

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
