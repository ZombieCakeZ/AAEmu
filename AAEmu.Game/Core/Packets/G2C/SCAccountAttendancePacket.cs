using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// 2.0.1.7 server → client: account attendance counter array.
/// Sent in the post-key-exchange burst alongside SCGetSlotCount / SCRaceCongestion
/// / SCCharacterList. The 2.0 client refuses to render the character-selection
/// screen until it has received the attendance data — without this packet the
/// connection drops with a 'Packet Error' a second after the char list arrives.
/// Payload: <c>count</c> consecutive ulongs of attendance bitmasks (all zero is
/// fine for a fresh account).
/// </summary>
public class SCAccountAttendancePacket(uint count) : GamePacket(SCOffsets.SCAccountAttendancePacket, 5)
{
    public override PacketStream Write(PacketStream stream)
    {
        for (var i = 0; i < count; i++)
            stream.Write((ulong)0);
        return stream;
    }
}
