using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// 2.0.1.7 in-game cash-shop configuration. Three small bytes; client treats
/// it as a feature toggle for the shop UI.
/// </summary>
public class SCInGameShopConfigPacket(byte ingameShopVersion, byte secondPriceType, byte askBuyLaborPowerPotion)
    : GamePacket(SCOffsets.SCInGameShopConfigPacket, 5)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(ingameShopVersion);
        stream.Write(secondPriceType);
        stream.Write(askBuyLaborPowerPotion);
        return stream;
    }
}
