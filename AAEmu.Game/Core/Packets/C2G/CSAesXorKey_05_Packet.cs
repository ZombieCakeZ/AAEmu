using AAEmu.Commons.Cryptography;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// 2.0.1.7 client → server: AES + XOR encryption key exchange — Level 5 variant.
/// Same purpose as CSAesXorKeyPacket but with explicit length prefixes for the
/// AES + XOR blocks (so the client can choose the key size at runtime). Used
/// once the connection has the initial keys staged.
/// </summary>
public class CSAesXorKey_05_Packet() : GamePacket(CSOffsets.CSAesXorKey_05_Packet, 5)
{
    public override void Read(PacketStream stream)
    {
        var len = stream.ReadInt32();   // lenAES
        var len2 = stream.ReadInt16();  // lenXOR

        if (len == 0 || len2 == 0)
            return;

        var encAes = stream.ReadBytes(len / 2);
        var encXor = stream.ReadBytes(len2 / 2);

        EncryptionManager.Instance.StoreClientKeys(encAes, encXor, Connection.AccountId, Connection.Id);
    }
}
