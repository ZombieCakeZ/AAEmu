using AAEmu.Commons.Network;
using AAEmu.Login.Core.Network.Login;
using AAEmu.Login.Models;

namespace AAEmu.Login.Core.Packets.L2C;

/// <summary>
/// A packet sent by the login server to the client in response to an authentication request.
/// </summary>
/// <param name="accountId">The unique identifier of the account.</param>
/// <param name="slotCount"></param>
public class ACAuthResponsePacket(AccountId accountId, byte slotCount) : LoginPacket(LCOffsets.ACAuthResponsePacket)
{
    private readonly byte[] _wsk = new byte[32];

    public override PacketStream Write(PacketStream stream)
    {
        // 2.0.1.7 expects an 8-byte ulong account id. AccountId.Value is uint
        // (4 bytes) on the master branch — writing it raw on this branch
        // shifts wsk + slotCount by 4 bytes and the client throws a
        // "Serializer Mismatch" popup right after Connect. Cast explicitly.
        stream.Write((ulong)accountId.Value);
        stream.Write(_wsk, true);
        stream.Write(slotCount);

        return stream;
    }
}
