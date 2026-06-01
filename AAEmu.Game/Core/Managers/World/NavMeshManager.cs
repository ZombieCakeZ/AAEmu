using System.Collections.Concurrent;
using System.IO;
using System.Numerics;

using AAEmu.Commons.IO;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models;

using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

using NLog;

namespace AAEmu.Game.Core.Managers.World;

/// <summary>
/// Loads editor-baked DotRecast navmeshes (AAEN-wrapped DtMeshSet) per world
/// from Data/NavMesh/&lt;world&gt;.navmesh and exposes a thread-safe query surface
/// for the AI/pathfinding layer.
///
/// Coordinate system: callers pass AA-space Vector3 (Z-up). The manager swaps
/// internally to Recast Y-up before touching DotRecast and unswaps the result —
/// AI code stays in AA coordinates throughout.
///
/// Phase 6 ships load + four basic queries (FindPath / FindNearestPoly /
/// IsReachable / CapsuleSweep). Behaviour-changing AI integration is gated
/// behind World.UseNavMesh (default false).
/// </summary>
public class NavMeshManager : Singleton<NavMeshManager>, ILoadable
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const uint AaenMagic = 0x4E454141u; // "AAEN"
    private const uint SupportedVersion = 1u;
    private const int VertsPerPoly = 6;
    private const float DefaultAgentRadius = 0.6f;

    // ===== Phase 7 — rate-limit constants ==================================
    /// <summary>Per-NPC repath cooldown floor — every NavMesh query keyed by ObjId throttles to this rate.</summary>
    public const double DefaultCooldownMs = 200.0;
    /// <summary>Global tick window for the server-wide query budget.</summary>
    private const int GlobalBudgetWindowMs = 50;
    /// <summary>Max NavMesh queries per <see cref="GlobalBudgetWindowMs"/> window (server-wide).</summary>
    private const int GlobalBudgetPerWindow = 16;
    /// <summary>Run dictionary GC every Nth insert.</summary>
    private const long JanitorEveryN = 1000;
    /// <summary>Prune cooldown entries older than this.</summary>
    private const long CooldownEntryMaxAgeMs = 60_000;

    private readonly ConcurrentDictionary<string, DtNavMesh> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ThreadLocal<DtNavMeshQuery>> _queries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _loadAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDtQueryFilter _defaultFilter = new DtQueryDefaultFilter();

    // Phase 7 — Per-NPC cooldown + global query budget.
    private readonly ConcurrentDictionary<uint, long> _npcCooldownTickMs = new();
    private long _globalBudgetWindowStartMs;
    private int _globalBudgetCount;
    private long _insertCounter;

    private string _navMeshRoot = string.Empty;
    private bool _loaded;

    public void Load()
    {
        if (_loaded) return;
        _navMeshRoot = Path.Combine(FileManager.AppPath, "Data", "NavMesh");
        if (!Directory.Exists(_navMeshRoot))
        {
            Directory.CreateDirectory(_navMeshRoot);
            Logger.Info("NavMeshManager: Created Data/NavMesh/ directory (empty)");
        }
        else
        {
            var files = Directory.GetFiles(_navMeshRoot, "*.navmesh");
            Logger.Info($"NavMeshManager: ready, {files.Length} world(s) on disk, root={_navMeshRoot}");
        }
        _loaded = true;
    }

    /// <summary>True once Load() has populated <see cref="_navMeshRoot"/>.</summary>
    public bool IsReady => _loaded;

    /// <summary>True if this world has a loaded mesh in memory (lazy-loaded by EnsureLoaded).</summary>
    public bool IsLoaded(string worldName) => _meshes.ContainsKey(worldName);

    /// <summary>
    /// Try-load the world's navmesh on demand. Returns true if the mesh is available
    /// for queries afterwards. Failures are logged once per world and remembered.
    /// </summary>
    public bool EnsureLoaded(string worldName)
    {
        if (string.IsNullOrEmpty(worldName)) return false;
        if (_meshes.ContainsKey(worldName)) return true;
        if (_loadAttempted.ContainsKey(worldName)) return false;
        if (!_loaded) Load();

        var path = Path.Combine(_navMeshRoot, $"{worldName}.navmesh");
        if (!File.Exists(path))
        {
            _loadAttempted[worldName] = false;
            Logger.Debug($"NavMeshManager: no file for world '{worldName}' at {path}");
            return false;
        }

        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            var magic = br.ReadUInt32();
            if (magic != AaenMagic)
                throw new InvalidDataException($"bad magic 0x{magic:X8} (expected 'AAEN')");
            var version = br.ReadUInt32();
            if (version != SupportedVersion)
                throw new InvalidDataException($"unsupported version {version} (expected {SupportedVersion})");
            _ = br.ReadUInt32(); // tileCount, informational
            _ = br.ReadSingle(); // cs
            _ = br.ReadSingle(); // ch
            _ = br.ReadSingle(); // tileWorldSize
            _ = br.ReadUInt32(); // payloadFormat

            var reader = new DtMeshSetReader();
            var mesh = reader.Read(br, VertsPerPoly);
            _meshes[worldName] = mesh;
            _queries[worldName] = new ThreadLocal<DtNavMeshQuery>(() => new DtNavMeshQuery(mesh));
            _loadAttempted[worldName] = true;
            Logger.Info($"NavMeshManager: loaded '{worldName}' from {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex)
        {
            _loadAttempted[worldName] = false;
            Logger.Error(ex, $"NavMeshManager: failed to load {Path.GetFileName(path)}");
            return false;
        }
    }

    // ===== Phase 7 — rate-limit gates ========================================

    /// <summary>
    /// True if the given NPC is allowed to fire a NavMesh query right now. False when the
    /// per-NPC 200ms cooldown is still active OR the server-wide budget for the current 50ms
    /// window is exhausted. Always false when the feature is disabled via World.UseNavMesh.
    /// </summary>
    public bool CanRepathNow(uint objId, double cooldownMs = DefaultCooldownMs)
    {
        if (!AppConfiguration.Instance.World.UseNavMesh) return false;
        var now = Environment.TickCount64;
        if (_npcCooldownTickMs.TryGetValue(objId, out var nextOk) && now < nextOk)
            return false;
        return CheckGlobalBudget(now);
    }

    /// <summary>Record a successful query so the NPC's cooldown clamp slides forward.</summary>
    public void NotePathRequest(uint objId, double cooldownMs = DefaultCooldownMs)
    {
        var now = Environment.TickCount64;
        _npcCooldownTickMs[objId] = now + (long)cooldownMs;
        if (Interlocked.Increment(ref _insertCounter) % JanitorEveryN == 0)
            PruneCooldownDictionary(now);
    }

    /// <summary>
    /// Thin wrapper over <see cref="FindNearestPoly"/> for spawn-snap and roam-target
    /// validation. Default extents (2m XY, 4m Y/Z up). Returns false on feature-off or
    /// budget exhaustion or no poly within extents.
    /// </summary>
    public bool TrySnap(string worldName, Vector3 pos, out Vector3 snapped, Vector3? extents = null)
    {
        snapped = pos;
        if (!AppConfiguration.Instance.World.UseNavMesh) return false;
        if (!CheckGlobalBudget(Environment.TickCount64)) return false;
        // FindNearestPoly internally uses fixed (2,4,2) extents; we ignore the override for now
        // since most callers use the default — extending later is a single-line follow-up.
        _ = extents;
        return FindNearestPoly(worldName, pos, out snapped, out var polyRef) && polyRef != 0L;
    }

    /// <summary>
    /// Cooldown-gated overload of <see cref="FindPath(string,Vector3,Vector3,float)"/>. The
    /// caller's <paramref name="objId"/> participates in the per-NPC throttle so 50 NPCs in
    /// a siege can't all repath every tick. Returns an empty list on cooldown / budget /
    /// disabled / no-path so the caller stays in its existing fallback branch.
    /// </summary>
    public List<Vector3> FindPath(string worldName, Vector3 start, Vector3 end, float agentRadius, uint objId)
    {
        if (!CanRepathNow(objId)) return new List<Vector3>();
        var path = FindPath(worldName, start, end, agentRadius);
        if (path.Count > 0) NotePathRequest(objId);
        return path;
    }

    /// <summary>
    /// Sliding 50ms window, max 16 queries. Atomic via Interlocked. Returns true if the
    /// current query fits inside the budget.
    /// </summary>
    private bool CheckGlobalBudget(long nowMs)
    {
        var winStart = Volatile.Read(ref _globalBudgetWindowStartMs);
        if (nowMs - winStart >= GlobalBudgetWindowMs)
        {
            Interlocked.Exchange(ref _globalBudgetWindowStartMs, nowMs);
            Interlocked.Exchange(ref _globalBudgetCount, 0);
        }
        return Interlocked.Increment(ref _globalBudgetCount) <= GlobalBudgetPerWindow;
    }

    /// <summary>O(N) sweep over the cooldown dictionary — drops entries older than 60s.</summary>
    private void PruneCooldownDictionary(long nowMs)
    {
        foreach (var kv in _npcCooldownTickMs)
        {
            if (nowMs - kv.Value > CooldownEntryMaxAgeMs)
                _npcCooldownTickMs.TryRemove(kv.Key, out _);
        }
    }

    // ===== Query API =========================================================

    /// <summary>Coords swap: AA (Z-up) → Recast (Y-up). Pure projection — no scale.</summary>
    internal static RcVec3f ToRc(in Vector3 aa) => new(aa.X, aa.Z, aa.Y);

    /// <summary>Coords swap: Recast (Y-up) → AA (Z-up).</summary>
    internal static Vector3 ToAa(in RcVec3f rc) => new(rc.X, rc.Z, rc.Y);

    /// <summary>
    /// FindPath: returns AA-space waypoints from <paramref name="start"/> to
    /// <paramref name="end"/>, or an empty list if the navmesh isn't loaded or no
    /// path can be planned. Drop-in shape match for GridPathfinder.FindPath.
    /// </summary>
    public List<Vector3> FindPath(string worldName, Vector3 start, Vector3 end, float agentRadius = DefaultAgentRadius)
    {
        if (!EnsureLoaded(worldName)) return new List<Vector3>();
        var query = _queries[worldName].Value!;
        var filter = _defaultFilter;
        var ext = new RcVec3f(2f, 4f, 2f);

        var rcStart = ToRc(start);
        var rcEnd = ToRc(end);
        var sStatus = query.FindNearestPoly(rcStart, ext, filter, out var startRef, out var startSnap, out _);
        if (!sStatus.Succeeded() || startRef == 0) return new List<Vector3>();
        var eStatus = query.FindNearestPoly(rcEnd, ext, filter, out var endRef, out var endSnap, out _);
        if (!eStatus.Succeeded() || endRef == 0) return new List<Vector3>();

        const int maxPath = 256;
        const int maxStraight = 64;
        Span<long> pathRefs = stackalloc long[maxPath];
        var fpStatus = query.FindPath(startRef, endRef, startSnap, endSnap, filter, pathRefs, out var pathCount, maxPath);
        if (!fpStatus.Succeeded() || pathCount == 0) return new List<Vector3>();

        Span<DtStraightPath> straight = stackalloc DtStraightPath[maxStraight];
        var spStatus = query.FindStraightPath(startSnap, endSnap, pathRefs[..pathCount], pathCount, straight, out var straightCount, maxStraight, 0);
        if (!spStatus.Succeeded() || straightCount == 0) return new List<Vector3>();

        var result = new List<Vector3>(straightCount);
        for (var i = 0; i < straightCount; i++)
            result.Add(ToAa(straight[i].pos));
        return result;
    }

    /// <summary>
    /// Project an AA point onto the nearest walkable navmesh poly. Used for spawn-snap
    /// and roam-target validation. <paramref name="snapped"/> is the closest valid
    /// position; <paramref name="polyRef"/> is the Detour reference (opaque to AI).
    /// </summary>
    public bool FindNearestPoly(string worldName, Vector3 p, out Vector3 snapped, out long polyRef)
    {
        snapped = default;
        polyRef = 0L;
        if (!EnsureLoaded(worldName)) return false;
        var query = _queries[worldName].Value!;
        var ext = new RcVec3f(2f, 4f, 2f);
        var rc = ToRc(p);
        var status = query.FindNearestPoly(rc, ext, _defaultFilter, out polyRef, out var nearest, out _);
        if (!status.Succeeded() || polyRef == 0) return false;
        snapped = ToAa(nearest);
        return true;
    }

    /// <summary>Cheap connectivity test for AI target acceptance — true if a path exists at all.</summary>
    public bool IsReachable(string worldName, Vector3 a, Vector3 b, float agentRadius = DefaultAgentRadius)
    {
        if (!AppConfiguration.Instance.World.UseNavMesh) return false;
        if (!CheckGlobalBudget(Environment.TickCount64)) return false;
        return FindPath(worldName, a, b, agentRadius).Count >= 2;
    }

    /// <summary>
    /// Capsule sweep over the navmesh surface — returns true if the move from
    /// <paramref name="a"/> to <paramref name="b"/> hits a wall before reaching
    /// <paramref name="b"/>, with <paramref name="hit"/> set to the clipped position
    /// (otherwise hit = b, return false).
    /// </summary>
    public bool CapsuleSweep(string worldName, Vector3 a, Vector3 b, float agentRadius, out Vector3 hit)
    {
        hit = b;
        if (!AppConfiguration.Instance.World.UseNavMesh) return false;
        if (!CheckGlobalBudget(Environment.TickCount64)) return false;
        if (!EnsureLoaded(worldName)) return false;
        var query = _queries[worldName].Value!;
        var ext = new RcVec3f(2f, 4f, 2f);
        var rcA = ToRc(a);
        var rcB = ToRc(b);
        var s = query.FindNearestPoly(rcA, ext, _defaultFilter, out var startRef, out var startSnap, out _);
        if (!s.Succeeded() || startRef == 0) return false;
        const int maxVisited = 16;
        Span<long> visited = stackalloc long[maxVisited];
        var moveStatus = query.MoveAlongSurface(startRef, startSnap, rcB, _defaultFilter, out var resultPos, visited, out _, maxVisited);
        if (!moveStatus.Succeeded()) return false;
        var endPos = ToAa(resultPos);
        var diff = b - endPos;
        if (diff.LengthSquared() > 0.01f)
        {
            hit = endPos;
            return true;
        }
        return false;
    }
}
