using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Units.Route;
using AAEmu.Game.Models.Game.World;

using NLog;

namespace AAEmu.Game.Models.Game.NPChar;

public class NpcSpawnerNpc : Spawner<Npc>
{
    // ReSharper disable once InconsistentNaming
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// NpcSpawnerTemplateId
    /// </summary>
    public uint NpcSpawnerTemplateId { get; init; }
    /// <summary>
    /// NpcTemplateId
    /// </summary>
    public uint MemberId { get; set; }
    /// <summary>
    /// MemberType should be "Npc" here
    /// </summary>
    public string MemberType { get; set; }
    /// <summary>
    /// Spawn priority weight
    /// </summary>
    public float Weight { get; init; }

    public NpcSpawnerNpc()
    {
        //
    }

    /// <summary>
    /// Creates a new instance of NpcSpawnerNpcs with a Spawner template id (npc_spanwers)
    /// </summary>
    /// <param name="spawnerTemplateId"></param>
    public NpcSpawnerNpc(uint spawnerTemplateId)
    {
        NpcSpawnerTemplateId = spawnerTemplateId;
    }

    public NpcSpawnerNpc(uint spawnerTemplateId, uint npcTemplateId)
    {
        NpcSpawnerTemplateId = spawnerTemplateId;
        MemberId = npcTemplateId;
        MemberType = "Npc";
    }

    /// <summary>
    /// Spawns Npcs from a NpcSpawner
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns>List of newly spawned NPCs</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public List<Npc> Spawn(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        switch (MemberType)
        {
            case "Npc":
                return SpawnNpc(npcSpawner, ownerId);
            case "NpcGroup":
                return SpawnNpcGroup(npcSpawner, ownerId);
            default:
                throw new InvalidOperationException($"Tried spawning an unsupported line from NpcSpawnerNpc - Id: {Id}");
        }
    }

    /// <summary>
    /// Internal Spawn Npc function for Spawn
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns></returns>
    private List<Npc> SpawnNpc(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        var npcs = new List<Npc>();
        var npc = NpcManager.Instance.Create(npcSpawner.ParentWorld, 0, MemberId);
        if (npc == null)
        {
            Logger.Warn($"Npc {MemberId}, from spawner Id {npcSpawner.Id} not exist at db. Spawner Position: {npcSpawner.Position}");
            return null;
        }

        npc.ParentWorld = npcSpawner.ParentWorld;
        npc.OwnerId = ownerId;

        npc.RegisterNpcEvents();

        Logger.Trace($"Spawn npc templateId {MemberId} objId {npc.ObjId} from spawnerId {NpcSpawnerTemplateId} at Position: {npcSpawner.Position}");

        // Determine if NPC is aquatic based on spawn position relative to water surface.
        // CanFly NPCs in water are always aquatic (fish, rays, etc.)
        // Non-CanFly NPCs are aquatic only if spawned deep underwater (>10m below surface)
        // to avoid false-tagging coastal/beach NPCs whose feet are in shallow water.
        var preSpawnWorld = npcSpawner.ParentWorld != null
            ? WorldManager.Instance.GetWorld(npcSpawner.ParentWorld.Id)
            : null;
        var spawnVec = npcSpawner.Position.AsPositionVector();
        if (preSpawnWorld != null && preSpawnWorld.IsWater(spawnVec))
        {
            if (npc.CanFly)
            {
                npc.IsAquatic = true;
            }
            else
            {
                // Check depth: only tag as aquatic if spawn is >10m below water surface
                var waterSurface = preSpawnWorld.Water != null
                    ? preSpawnWorld.Water.GetWaterSurface(spawnVec, out _)
                    : preSpawnWorld.Template.OceanLevel;
                if (npcSpawner.Position.Z < waterSurface - 10f)
                    npc.IsAquatic = true;
            }
        }

        if (!npc.CanFly && !npc.IsAquatic)
        {
            // Check height grids first — if a grid covers the spawn position,
            // snap the NPC to the grid height instead of falling back to terrain.
            var spawnWorldId = npcSpawner.ParentWorld?.Id ?? 0;
            var cvWorldName = CollisionVolumeManager.GetWorldNameFromId(spawnWorldId);

            // Check template-level bindings (manual /cv gridbind) first — they always win.
            var gridBindings = CollisionVolumeManager.Instance.GetNpcGridBinding(MemberId);

            float? gridHeight = null;
            if (gridBindings != null && gridBindings.Count > 0)
            {
                // NPC has template-level grid bindings — check those grids
                if (gridBindings.Contains("*"))
                {
                    gridHeight = CollisionVolumeManager.Instance.GetGridHeight(
                        cvWorldName, npcSpawner.Position.X, npcSpawner.Position.Y, npcSpawner.Position.Z);
                }
                else
                {
                    foreach (var gn in gridBindings)
                    {
                        var g = CollisionVolumeManager.Instance.GetHeightGridByName(gn);
                        if (g != null)
                        {
                            var h = g.GetBestFloorHeight(npcSpawner.Position.X, npcSpawner.Position.Y, npcSpawner.Position.Z);
                            if (h.HasValue)
                            {
                                gridHeight = h;
                                break;
                            }
                        }
                    }
                }
            }
            // Auto-bind disabled by design: only manual /cv gridbind template-level bindings
            // attach NPCs to grids. Open-world NPCs use the Heightmap System only.

            if (gridHeight.HasValue)
            {
                // Grid found (template) — snap spawn Z to grid height directly.
                // No terrain fallback runs after this; the grid IS the ground truth here.
                npcSpawner.Position.Z = gridHeight.Value;
            }
            else
            {
                // No grid covers this spawn position — fall back to terrain height.
                // Only correct when the DB Z is already very close to terrain (<1m), so we
                // don't yank an NPC off a building/structure that lacks a grid.
                var newZ = npcSpawner.ParentWorld?.Template?.GeoData?.GetHeight(npcSpawner.Position.AsPositionVector())
                           ?? 0f;
                if (newZ > 0f && Math.Abs(npcSpawner.Position.Z - newZ) < 1f)
                {
                    npcSpawner.Position.Z = newZ;
                }
            }
        }

        // ── Phase 7: NavMesh spawn snap ──
        // Snap spawner coords to nearest walkable poly BEFORE ApplyWorldSpawnPosition so the
        // AI's HomePosition / IdlePosition (assigned right after from Transform.World.Position)
        // inherit the snapped values. Hard 5m Δ cap on XY+Z so a navmesh hole inside a building
        // can NEVER teleport the NPC across an architectural gap.
        if (AppConfiguration.Instance.World.UseNavMesh)
        {
            var spawnWorldName = CollisionVolumeManager.GetWorldNameFromId(npcSpawner.ParentWorld?.Id ?? 0);
            if (!string.IsNullOrEmpty(spawnWorldName))
            {
                var rawSpawn = new System.Numerics.Vector3(
                    npcSpawner.Position.X, npcSpawner.Position.Y, npcSpawner.Position.Z);
                if (NavMeshManager.Instance.TrySnap(spawnWorldName, rawSpawn, out var snapped))
                {
                    var dxy = MathF.Sqrt(
                        (snapped.X - rawSpawn.X) * (snapped.X - rawSpawn.X) +
                        (snapped.Y - rawSpawn.Y) * (snapped.Y - rawSpawn.Y));
                    var dz = MathF.Abs(snapped.Z - rawSpawn.Z);
                    if (dxy < 5f && dz < 5f)
                    {
                        npcSpawner.Position.X = snapped.X;
                        npcSpawner.Position.Y = snapped.Y;
                        npcSpawner.Position.Z = snapped.Z;
                    }
                    else
                    {
                        Logger.Warn($"NavMesh spawn snap rejected for NPC {MemberId} @ spawner " +
                                    $"{NpcSpawnerTemplateId}: Δxy={dxy:F2}m Δz={dz:F2}m (>5m cap). Using raw spawner.");
                    }
                }
            }
        }

        npc.Transform.ApplyWorldSpawnPosition(npcSpawner.Position);
        if (npc.Transform == null)
        {
            Logger.Error($"Can't spawn npc {MemberId} from spawnerId {NpcSpawnerTemplateId}. Transform is null.");
            return null;
        }

        npc.Transform.InstanceId = npc.Transform.InstanceId;

        if (npc.Ai != null)
        {
            npc.Ai.HomePosition = npc.Transform.World.Position;
            npc.Ai.IdlePosition = npc.Ai.HomePosition;
            npc.Ai.GoToSpawn();
        }

        npc.Spawner = npcSpawner;
        npc.Spawner.RespawnTime = (int)Random.Shared.Next(npc.Spawner.Template.SpawnDelayMin, npc.Spawner.Template.SpawnDelayMax);
        npc.Spawn();

        var world = WorldManager.Instance.GetWorld(npc.Transform.InstanceId);
        world.Events.OnUnitSpawn(world, new OnUnitSpawnArgs { Npc = npc });
        npc.Simulation = new Simulation(npc);

        if (npc.Ai != null && !string.IsNullOrWhiteSpace(npcSpawner.FollowPath))
        {
            if (!npc.Ai.LoadAiPathPoints(npcSpawner.FollowPath, false))
                Logger.Warn($"Failed to load {npcSpawner.FollowPath} for NPC {npc.TemplateId} ({npc.ObjId})");
        }

        npcs.Add(npc);
        return npcs;
    }

    /// <summary>
    /// Internal Spawn NpcGroup function for Spawn
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns></returns>
    private List<Npc> SpawnNpcGroup(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        return SpawnNpc(npcSpawner, ownerId);
    }
}
