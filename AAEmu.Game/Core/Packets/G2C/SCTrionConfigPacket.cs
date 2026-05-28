using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

// 2.0.1.7 layout (per alpha): 5 strings instead of 1.2's 4. authUrl is gone,
// wikiUrl + csUrl are added at the end. Level bumped to 5 like all post-handshake packets.
public class SCTrionConfigPacket(bool activate, string platformUrl, string commerceUrl, string wikiUrl, string csUrl)
    : GamePacket(SCOffsets.SCTrionConfigPacket, 5)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(activate);
        stream.Write(platformUrl);
        stream.Write(commerceUrl);
        stream.Write(wikiUrl);
        stream.Write(csUrl);
        return stream;
    }
}
