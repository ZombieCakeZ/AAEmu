using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

public class X2EnterWorldPacket() : GamePacket(CSOffsets.X2EnterWorldPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var pFrom = stream.ReadUInt32();
        var pTo = stream.ReadUInt32();
        // 2.0.1.7 sends accountId as ulong (8 bytes). Reading it as uint left the
        // upper 4 bytes in the stream so the next ReadUInt32() picked them up as
        // 'cookie' (= 0 for a small accountId), causing EnterWorldManager.Login
        // to reject the connection with 'Invalid token, Token: 0, AccountId: 1'.
        var accountId = stream.ReadUInt64();
        var cookie = stream.ReadUInt32();
        var zoneId = stream.ReadInt32();
        var tb = stream.ReadByte();
        var revision = stream.ReadUInt64();
        // 2.0.1.7 added a trailing 'index' byte to disambiguate connection slots.
        var index = stream.ReadByte();

        // AccountId values in the DB are uint; the upper 32 bits are unused. Cast
        // back so EnterWorldManager.Login keeps its uint signature for the master
        // branch's sake.
        EnterWorldManager.Instance.Login(Connection, (uint)accountId, cookie);
    }
}
