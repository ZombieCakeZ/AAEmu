using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Tells the client to apply a physics impulse to a unit. Used by skills that emit
/// <see cref="Models.Game.Skills.Effects.ImpulseEffect"/> — vehicle Jump (22284), Super
/// Charge (22293), Roll (22483) and any NPC/Character knockback / pull / lift — where
/// the client owns the trajectory animation off the velocity and angular components.
///
/// Wire format (per ZeromusXYZ's review):
///   - WriteBc(unitObjId)         // 3 bytes — target unit BC id
///   - SkillCasterType (byte)     // source caster type
///   - WriteBc(sourceObjId)       // 3 bytes — source caster BC id
///   - 12 × float32               // VelImpulse XYZ, AngvelImpulse XYZ, Impulse XYZ, AngImpulse XYZ
///
/// The 4 bytes after the target id are a SkillCaster.Write() (byte type + WriteBc id),
/// NOT a shifted-uint-as-float. Those two encodings coincide ONLY for the case
/// SkillCasterType.Unit (0) + sourceObjId == targetObjId — which is the vehicle self-cast
/// flow Jump/Charge/Roll happen to hit. Pass the actual source via the constructor so
/// attacker→victim impulses (pull, knockback, lift) carry correct attribution; a null
/// source falls back to the self-sentinel for backwards-compatibility with that path.
/// </summary>
public class SCUnitImpulsePacket(
    uint unitObjId,
    SkillCaster source,
    float velImpulseX,
    float velImpulseY,
    float velImpulseZ,
    float angvelImpulseX,
    float angvelImpulseY,
    float angvelImpulseZ,
    float impulseX,
    float impulseY,
    float impulseZ,
    float angImpulseX,
    float angImpulseY,
    float angImpulseZ)
    : GamePacket(SCOffsets.SCUnitImpulsePacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.WriteBc(unitObjId);
        // SkillCaster.Write emits the byte SkillCasterType followed by WriteBc(ObjId) — the
        // 4-byte "source" field. Null falls back to the self-sentinel (Unit-typed caster
        // with the target's own ObjId), which is exactly the byte sequence the previous
        // EncodeUnitObjIdFloat helper happened to emit for vehicle self-casts.
        if (source != null)
            stream.Write(source);
        else
        {
            stream.Write((byte)SkillCasterType.Unit);
            stream.WriteBc(unitObjId);
        }
        stream.Write(velImpulseX);
        stream.Write(velImpulseY);
        stream.Write(velImpulseZ);
        stream.Write(angvelImpulseX);
        stream.Write(angvelImpulseY);
        stream.Write(angvelImpulseZ);
        stream.Write(impulseX);
        stream.Write(impulseY);
        stream.Write(impulseZ);
        stream.Write(angImpulseX);
        stream.Write(angImpulseY);
        stream.Write(angImpulseZ);
        return stream;
    }
}
