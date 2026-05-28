using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Stream;

namespace AAEmu.Game.Core.Packets.C2S;

public class CTJoinPacket() : StreamPacket(CTOffsets.CTJoinPacket)
{
    public override void Read(PacketStream stream)
    {
        // Same uint -> ulong shift as X2EnterWorldPacket (a61e03f4), this time
        // on the stream protocol. 2.0.1.7 sends accountId as ulong (8 bytes)
        // before the uint cookie. Vanilla read 4 bytes, so cookie picked up the
        // upper 4 bytes of accountId (= 0 for any reasonable account id) and
        // StreamManager.Login rejected the connection — the stream server then
        // dropped the socket and the client popped 'Packet Error'.
        var accountId = stream.ReadUInt64();
        var cookie = stream.ReadUInt32();

        StreamManager.Instance.Login(Connection, (uint)accountId, cookie);
    }
}
