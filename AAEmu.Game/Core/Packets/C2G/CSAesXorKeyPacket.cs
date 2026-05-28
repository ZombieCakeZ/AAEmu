using AAEmu.Commons.Cryptography;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// 2.0.1.7 client → server: AES + XOR encryption key exchange.
/// Sent right after the client received the server's RSA public key in
/// X2EnterWorldResponsePacket. After storing the keys, proactively push the
/// character-list flow (SCGetSlotCount + SCRaceCongestion + SCCharacterList +
/// SCLoginCharInfoHouse). The 2.0.1.7 client does not send CSListCharacterPacket
/// like 1.2 — it waits for the list immediately after the key exchange and
/// times out / disconnects (pops 'Packet Error') if it doesn't arrive within
/// a few seconds. Mirrors the alpha source's handler.
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

        Connection.SendPacket(new SCGetSlotCountPacket(0));

        Connection.LoadAccount();
        Connection.SendPacket(new SCRaceCongestionPacket());

        var characters = Connection.Characters.Values.ToArray();
        if (characters.Length == 0)
        {
            Connection.SendPacket(new SCCharacterListPacket(true, characters));
        }
        else
        {
            for (var i = 0; i < characters.Length; i += 2)
            {
                var last = characters.Length - i <= 2;
                var temp = new Character[last ? characters.Length - i : 2];
                Array.Copy(characters, i, temp, 0, temp.Length);
                Connection.SendPacket(new SCCharacterListPacket(last, temp));
            }
        }

        var houses = Connection.Houses.Values.ToArray();
        foreach (var house in houses)
            Connection.SendPacket(new SCLoginCharInfoHouse(house.OwnerId, house));
    }
}
