using AAEmu.Commons.Cryptography;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// 2.0.1.7 client → server: AES + XOR encryption key exchange.
/// Sent right after the client received the server's RSA public key in
/// X2EnterWorldResponsePacket. The two key blobs are RSA-encrypted with that
/// public key; we hand them off to EncryptionManager.StoreClientKeys which
/// decrypts them and stores the derived AES + XOR working keys on the
/// per-connection keychain. All subsequent Level-5 packets in either direction
/// use those keys.
///
/// Level 1 variant — fixed 128-byte blobs. See CSAesXorKey_05_Packet for the
/// Level 5 variant the client uses once the ratchet is running.
/// </summary>
public class CSAesXorKeyPacket() : GamePacket(CSOffsets.CSAesXorKeyPacket, 1)
{
    public override void Read(PacketStream stream)
    {
        var len = stream.ReadInt32();   // lenAES (informational)
        var len2 = stream.ReadInt16();  // lenXOR (informational)

        var encAes = stream.ReadBytes(128);
        var encXor = stream.ReadBytes(128);

        EncryptionManager.Instance.StoreClientKeys(encAes, encXor, Connection.AccountId, Connection.Id);
    }
}
