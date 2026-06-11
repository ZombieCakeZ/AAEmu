using AAEmu.Commons.Network;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Skills;

namespace AAEmu.UnitTests.Game.Core.Packets.G2C;

public class SCUnitImpulsePacketTests
{
    [Test]
    public async Task Write_NullSource_EmitsSelfSentinel()
    {
        const uint objId = 0x123456;
        var packet = new SCUnitImpulsePacket(
            objId,
            null,
            1f, 2f, 3f,
            4f, 5f, 6f,
            7f, 8f, 9f,
            10f, 11f, 12f);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadBc()).IsEqualTo(objId);
        // Self-sentinel layout: SkillCasterType.Unit (0) + WriteBc(objId). Same byte sequence
        // the legacy EncodeUnitObjIdFloat helper emitted for vehicle self-casts.
        await Assert.That(stream.ReadByte()).IsEqualTo((byte)SkillCasterType.Unit);
        await Assert.That(stream.ReadBc()).IsEqualTo(objId);
        await Assert.That(stream.ReadSingle()).IsEqualTo(1f);
        await Assert.That(stream.ReadSingle()).IsEqualTo(2f);
        await Assert.That(stream.ReadSingle()).IsEqualTo(3f);
    }

    [Test]
    public async Task Write_WithSource_EmitsCallerProvidedAttribution()
    {
        const uint targetObjId = 0x123456;
        const uint attackerObjId = 0xABCDEF;
        var source = new SkillCasterUnit { ObjId = attackerObjId };

        var packet = new SCUnitImpulsePacket(
            targetObjId,
            source,
            1f, 2f, 3f,
            4f, 5f, 6f,
            7f, 8f, 9f,
            10f, 11f, 12f);

        var stream = packet.Write(new PacketStream());
        stream.Rollback();

        await Assert.That(stream.ReadBc()).IsEqualTo(targetObjId);
        await Assert.That(stream.ReadByte()).IsEqualTo((byte)SkillCasterType.Unit);
        await Assert.That(stream.ReadBc()).IsEqualTo(attackerObjId);
        await Assert.That(stream.ReadSingle()).IsEqualTo(1f);
    }
}
