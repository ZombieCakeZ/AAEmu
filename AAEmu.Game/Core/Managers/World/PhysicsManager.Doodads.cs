using System.Numerics;

using AAEmu.Game.Models.Game.World;

using Jitter2.Collision;
using Jitter2.LinearMath;

namespace AAEmu.Game.Core.Managers.World;

/// <summary>
/// Phase 8a — registers every editor-baked CollisionMeshInstance (from MeshCollisionManager)
/// as a custom IDynamicTreeProxy in the Jitter2 world. One proxy per instance, ~242k for
/// main_world. Each proxy holds a reference to the shared CollisionMesh template and uses
/// Phase-1's BVH for narrow-phase via MeshCollisionManager.RaycastInstance.
///
/// This is the foundation for moving NPC collision queries off the custom BVH layer onto
/// Jitter's broadphase. Public Raycast / HeightAt wrappers (this file, below) acquire
/// _worldLock so AI/game-thread callers are safe against the physics tick.
/// </summary>
public partial class PhysicsManager
{
    private readonly List<DoodadCollisionProxy> _doodadProxies = new();

    /// <summary>Result of a doodad raycast. AA-space coordinates.</summary>
    public readonly struct DoodadRaycastResult
    {
        public bool Hit { get; init; }
        public Vector3 Point { get; init; }
        public float Distance { get; init; }
        public CollisionMeshInstance Instance { get; init; }
    }

    /// <summary>
    /// Eagerly registers every MeshCollisionManager instance for this physics world as a
    /// static DynamicTree proxy. Call once during PhysicsManager.Initialize() after the
    /// terrain HeightmapTester is wired but before StartPhysics(). The physics thread is
    /// not yet running so the world mutation is safe without _pendingActions.
    /// </summary>
    public void LoadStaticDoodadsForWorld(string worldName)
    {
        if (string.IsNullOrEmpty(worldName) || _physWorld == null) return;
        if (!MeshCollisionManager.Instance.IsLoaded)
        {
            Logger.Warn($"PhysicsManager: MeshCollisionManager not loaded — doodad collision disabled for '{worldName}'");
            return;
        }

        var instances = MeshCollisionManager.Instance.GetWorldInstances(worldName);
        if (instances.Count == 0)
        {
            Logger.Info($"PhysicsManager: no doodads found for world '{worldName}'");
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var added = 0;
        var skipped = 0;
        foreach (var inst in instances)
        {
            if (inst?.Mesh == null || inst.Mesh.Vertices.Length == 0)
            {
                skipped++;
                continue;
            }
            var proxy = new DoodadCollisionProxy(inst);
            _physWorld.DynamicTree.AddProxy(proxy, false);
            _doodadProxies.Add(proxy);
            added++;
        }

        sw.Stop();
        Logger.Info($"PhysicsManager: loaded {added:N0} static doodad proxies ({skipped:N0} skipped) for world '{worldName}' in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Public raycast against the static collision world (terrain + doodads). Coordinates
    /// are in AA Z-up world space. Returns the closest hit or default(DoodadRaycastResult)
    /// when no proxy is in the ray path. Thread-safe under <see cref="_worldLock"/>.
    /// </summary>
    public DoodadRaycastResult Raycast(Vector3 from, Vector3 to)
    {
        if (_physWorld == null) return default;
        var dir = to - from;
        var len = dir.Length();
        if (len < 1e-4f) return default;

        // AA Z-up → Jitter Y-up swap.
        var origin = new JVector(from.X, from.Z, from.Y);
        var jdir = new JVector(dir.X / len, dir.Z / len, dir.Y / len);
        lock (_worldLock)
        {
            if (!_physWorld.DynamicTree.RayCast(origin, jdir, null, null, out var proxy, out _, out var lambda))
                return default;
            if (lambda > len) return default;

            var t = (float)lambda / len;
            var hitPoint = new Vector3(
                from.X + dir.X * t,
                from.Y + dir.Y * t,
                from.Z + dir.Z * t);

            var inst = proxy is DoodadCollisionProxy dcp ? dcp.Instance : null;
            return new DoodadRaycastResult
            {
                Hit = true,
                Point = hitPoint,
                Distance = (float)lambda,
                Instance = inst,
            };
        }
    }

    /// <summary>
    /// Downward ray from (x, y, fromZ). Returns the surface Z (largest hit Z within
    /// <paramref name="maxDrop"/> below fromZ) or null. AA Z-up coords.
    /// </summary>
    public float? HeightAt(float x, float y, float fromZ, float maxDrop = 50f)
    {
        var hit = Raycast(new Vector3(x, y, fromZ + 0.001f), new Vector3(x, y, fromZ - maxDrop));
        return hit.Hit ? hit.Point.Z : (float?)null;
    }
}

/// <summary>
/// One static-doodad proxy in Jitter2's DynamicTree. AABB comes from the instance's
/// pre-computed WorldBounds; raycast forwards to MeshCollisionManager.RaycastInstance which
/// reuses Phase-1's per-template BVH. Velocity is zero (static), NodePtr/SetIndex are
/// managed by DynamicTree.
/// </summary>
internal sealed class DoodadCollisionProxy : IDynamicTreeProxy, IRayCastable
{
    public CollisionMeshInstance Instance { get; }
    public int SetIndex { get; set; } = -1;
    public int NodePtr { get; set; }
    public JVector Velocity => JVector.Zero;
    public JBoundingBox WorldBoundingBox { get; }

    public DoodadCollisionProxy(CollisionMeshInstance instance)
    {
        Instance = instance;
        // AA Z-up → Jitter Y-up. WorldBounds is (MinX, MinY, MaxX, MaxY) plus WorldMinZ/MaxZ.
        var (mnx, mny, mxx, mxy) = instance.WorldBounds;
        WorldBoundingBox = new JBoundingBox(
            new JVector(mnx, instance.WorldMinZ, mny),
            new JVector(mxx, instance.WorldMaxZ, mxy));
    }

    public bool RayCast(in JVector origin, in JVector direction, out JVector normal, out float lambda)
    {
        // Jitter Y-up → AA Z-up swap.
        var aaOrigin = new Vector3((float)origin.X, (float)origin.Z, (float)origin.Y);
        var aaDir = new Vector3((float)direction.X, (float)direction.Z, (float)direction.Y);

        // FirstRayHit (via RaycastInstance) returns the local-space hit t. For our case the
        // direction passed by DynamicTree is unit-length so t ≈ world distance.
        var hitT = MeshCollisionManager.Instance.RaycastInstance(Instance, aaOrigin, aaDir, float.MaxValue);
        if (!hitT.HasValue)
        {
            normal = JVector.Zero;
            lambda = float.MaxValue;
            return false;
        }
        // We don't have a precise per-triangle normal from FirstRayHit yet — use the ray's
        // own negation as a placeholder so Jitter's broadphase keeps a sane front-face. The
        // narrowphase consumers (PhysicsManager.Raycast → DoodadRaycastResult) only read
        // Point + Distance + Instance for AI queries; normal is reserved for Phase 8b.
        normal = -direction;
        lambda = hitT.Value;
        return true;
    }
}
