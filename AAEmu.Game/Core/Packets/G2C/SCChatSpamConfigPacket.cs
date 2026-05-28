using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// 2.0.1.7 chat spam configuration. Replaces 1.2's SCChatSpamDelayPacket which
/// the 2.0 client does not recognise. Payload comes verbatim from the alpha
/// source — the detectConfig blob is a Trion-tuned spam-classifier vector; the
/// client refuses to render the character-selection screen without it.
/// </summary>
public class SCChatSpamConfigPacket() : GamePacket(SCOffsets.SCChatSpamConfigPacket, 5)
{
    private static readonly byte[] ApplyConfig = [0x00];
    private static readonly byte[] DetectConfig =
    [
        0x00, 0x00, 0x70, 0x42, 0x05, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x16, 0x44, 0xCD, 0xCC, 0x4C, 0x3F,
        0x0A, 0xC8, 0x03
    ];

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write((byte)2);          // version (2 for 2.0+)
        stream.Write((short)60);        // reportDelay

        // chatTypeGroup — 17 bytes in 2.0 (was 15 in 1.2)
        for (var i = 0; i < 17; i++)
            stream.Write((byte)0);

        // chatGroupDelay — 17 floats
        for (var i = 0; i < 17; i++)
            stream.Write(0f);

        stream.Write(ApplyConfig, true);   // applyConfig + length prefix
        stream.Write(DetectConfig, true);  // detectConfig + length prefix
        return stream;
    }
}
