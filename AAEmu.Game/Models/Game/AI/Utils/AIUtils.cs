using System;
using System.Numerics;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.AI.Enums;
using AAEmu.Game.Models.Game.AI.v2.AiCharacters;
using AAEmu.Game.Models.Game.AI.v2.Framework;
using AAEmu.Game.Models.Game.NPChar;

namespace AAEmu.Game.Models.Game.AI.Utils;

public static class AiUtils
{

    // This is taken from x2ai.lua
    public static Vector3 CalcNextRoamingPosition(NpcAi ai)
    {
        var maxRoamingDistance = 6;
        var maxAttempts = ai.Owner.IsAquatic ? 10 : 6;

        var cvWorldName = CollisionVolumeManager.GetWorldNameFromId(
            WorldManager.Instance.GetWorldIdByZoneKey(ai.Owner.Transform.ZoneId));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var roamZ = ai.IdlePosition.Z;
            if (ai.Owner.IsAquatic)
                roamZ += (Random.Shared.NextSingle() - 0.5f) * maxRoamingDistance;

            var newPosition = new Vector3(
                (Random.Shared.NextSingle() - 0.5f) * maxRoamingDistance * 2 + ai.IdlePosition.X,
                (Random.Shared.NextSingle() - 0.5f) * maxRoamingDistance * 2 + ai.IdlePosition.Y,
                roamZ);

            newPosition.Z = WorldManager.Instance.GetReferenceHeight(ai, newPosition.X, newPosition.Y, newPosition.Z, ai.Owner.Transform.ZoneId);

            if (ai.Owner.IsAquatic)
            {
                var world = WorldManager.Instance.GetWorld(ai.Owner.Transform.InstanceId);
                if (world != null && !world.IsWater(newPosition))
                    continue;
            }

            if (!string.IsNullOrEmpty(cvWorldName))
            {
                var curPos = ai.Owner.Transform.World.Position;
                if (CollisionVolumeManager.Instance.IsBlockedByWall(cvWorldName,
                        curPos.X, curPos.Y,
                        newPosition.X, newPosition.Y, newPosition.Z) ||
                    CollisionVolumeManager.Instance.IsBlockedByWall(cvWorldName,
                        ai.IdlePosition.X, ai.IdlePosition.Y,
                        newPosition.X, newPosition.Y, newPosition.Z))
                {
                    continue;
                }

                if (CollisionVolumeManager.Instance.IsGridBoundNpcBlocked(
                        ai.Owner.ObjId, ai.Owner.TemplateId, cvWorldName,
                        ai.IdlePosition.X, ai.IdlePosition.Y, ai.IdlePosition.Z,
                        newPosition.X, newPosition.Y, newPosition.Z))
                {
                    continue;
                }
            }

            return newPosition;
        }

        return ai.IdlePosition;
    }

    public static NpcAi GetAiByType(AiParamType type, Npc owner)
    {
        switch (type)
        {
            case AiParamType.AlmightyNpc:
                return new AlmightyNpcAiCharacter { Owner = owner };
            case AiParamType.ArcherHoldPosition:
                return new ArcherHoldPositionAiCharacter { Owner = owner };
            case AiParamType.ArcherRoaming:
                return new ArcherRoamingAiCharacter { Owner = owner };
            case AiParamType.BigMonsterRoaming:
                return new BigMonsterRoamingAiCharacter { Owner = owner };
            case AiParamType.BigMonsterHoldPosition:
                return new BigMonsterHoldPositionAiCharacter { Owner = owner };
            case AiParamType.Default:
                return new DefaultAiCharacter { Owner = owner };
            case AiParamType.Dummy:
                return new DummyAiCharacter { Owner = owner };
            case AiParamType.Flytrap:
                return new FlytrapAiCharacter { Owner = owner };
            case AiParamType.HoldPosition:
                return new HoldPositionAiCharacter { Owner = owner };
            case AiParamType.Roaming:
                return new RoamingAiCharacter { Owner = owner };
            case AiParamType.TowerDefenseAttacker:
                return new TowerDefenseAttackerAiCharacter { Owner = owner };
            case AiParamType.WildBoarHoldPosition:
                return new WildBoarHoldPositionAiCharacter { Owner = owner };
            case AiParamType.WildBoarRoaming:
                return new WildBoarRoamingAiCharacter { Owner = owner };
            default:
                return null;
        }
    }
}
