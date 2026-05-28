using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// 2.0.1.7 housing-tax-payment to AA-point conversion ratio. Sent in the
/// post-FinishState config burst — without it the 2.0 client refuses to leave
/// the loading screen.
/// </summary>
public class SCTaxItemConfigPacket(ulong convertRatioToAAPoint)
    : GamePacket(SCOffsets.SCTaxItemConfigPacket, 5)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(convertRatioToAAPoint);
        return stream;
    }
}
