using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;

namespace AAEmu.Game.Core.Packets.Proxy;

public class FinishStatePacket() : GamePacket(PPOffsets.FinishStatePacket, 2)
{
    private readonly bool[] _scAccountInitPacket = [false, true];
    private readonly byte[] _scLevelRestrictionInitPacket = [0, 15, 15, 15, 0, 0, 15, 0, 0, 0, 0, 0, 0, 0, 15];

    public override void Read(PacketStream stream)
    {
        var state = stream.ReadInt32();

        switch (state)
        {
            case 0:
                Connection.SendPacket(new ChangeStatePacket(1));
                Connection.SendPacket(new SCHackGuardRetAddrsRequestPacket(false, false));
                var levelname = string.Empty;
                if (Connection.ActiveChar != null)
                {
                    levelname = ZoneManager.Instance.GetZoneByKey(Connection.ActiveChar.Transform.ZoneId)?.Name ?? "w_hanuimaru_1";
                }
                else
                {
                    levelname = "w_hanuimaru_1";
                }
                Connection.SendPacket(new SetGameTypePacket(levelname, 0, 1)); // TODO - level
                Connection.SendPacket(new SCInitialConfigPacket());

                // 2.0.1.7 SCTrionConfigPacket layout: (activate, platformUrl, commerceUrl, wikiUrl, csUrl).
                // authUrl is gone in 2.0.
                var platformUrl = "http://localhost/aaemu/platform";
                var commerceUrl = "http://localhost/aaemu/shop";
                var wikiUrl = "http://localhost/aaemu/wiki";
                var csUrl = "http://localhost/aaemu/cs";

                Connection.SendPacket(new SCTrionConfigPacket(true, platformUrl, commerceUrl, wikiUrl, csUrl));
                Connection.SendPacket(new SCAccountInfoPacket(
                        (int)Connection.Payment.Method,
                        Connection.Payment.Location,
                        Connection.Payment.StartTime,
                        Connection.Payment.EndTime)
                );
                // SCChatSpamDelayPacket (1.2 opcode in the 0xFF00 sentinel range) is
                // replaced by SCChatSpamConfigPacket — the 2.0 client refuses to leave
                // the loading screen without it.
                Connection.SendPacket(new SCChatSpamConfigPacket());
                Connection.SendPacket(new SCAccountAttributeConfigPacket(_scAccountInitPacket));
                Connection.SendPacket(new SCLevelRestrictionConfigPacket(10, 10, 10, 10, 10, _scLevelRestrictionInitPacket));
                Connection.SendPacket(new SCTaxItemConfigPacket(10000));
                Connection.SendPacket(new SCInGameShopConfigPacket(1, 0, 0));
                break;
            case 1:
                Connection.SendPacket(new ChangeStatePacket(2));
                break;
            case 2:
                Connection.SendPacket(new ChangeStatePacket(3));
                break;
            case 3:
            case 4:
            case 5:
            case 6:
                Connection.SendPacket(new ChangeStatePacket(state + 1));
                break;
            case 7:
                Connection.SendPacket(new SCUpdatePremiumPointPacket(1, 1, 1));
                break;
            default:
                Logger.Info("Unknown state: {0}", state);
                break;
        }
    }
}
