using AAEmu.Commons.Cryptography;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

// 2.0.1.7 layout (per alpha source) — Level 5 frame carries the RSA public key
// the client uses to encrypt the AES + XOR seeds it sends back via
// CSAesXorKeyPacket. Sent in the clear inside the Level 5 envelope because the
// per-connection encryption ratchet has not started yet (server msgCount = 0,
// XOR seed = length ^ 0x1F2175A0 — both reproducible without a shared secret).
public class X2EnterWorldResponsePacket(short reason, bool gm, uint token, ushort port)
    : GamePacket(SCOffsets.X2EnterWorldResponsePacket, 5)
{
    private const short PubKeySize = 128 * 2 + 4; // = 260, declared key block length
    private const int DwKeySize = 1024;

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(reason);                // reason
        stream.Write(gm);                    // gm
        stream.Write(token);                 // sc — Stream Token
        stream.Write(port);                  // sp — Stream Port
        stream.Write(Helpers.UnixTimeNow()); // wf

        stream.Write(PubKeySize);            // pubKeySize (declared)
        stream.Write(PubKeySize);            // length of the key block
        stream.Write(DwKeySize);             // dwKeySize (RSA bit strength)
        EncryptionManager.Instance.WritePubKey(Connection.Id, Connection.AccountId, stream);

        return stream;
    }
}
