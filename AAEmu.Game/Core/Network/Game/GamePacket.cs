using AAEmu.Commons.Cryptography;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Connections;

namespace AAEmu.Game.Core.Network.Game;

public abstract class GamePacket(ushort typeId, byte level) : PacketBase<GameConnection>(typeId)
{
    public byte Level { get; set; } = level;

    /// <summary>
    /// This is called in Encode after Read() in the case of GamePackets
    /// The purpose is to separate packet data from packet behavior
    /// </summary>
    public virtual void Execute() { }

    public override PacketStream Encode()
    {
        var ps = new PacketStream();
        try
        {
            var packet = new PacketStream()
                .Write((byte)0xdd)
                .Write(Level);

            var body = new PacketStream()
                .Write(TypeId)
                .Write(this);

            if (Level == 1)
            {
                packet
                    .Write((byte)0) // hash
                    .Write((byte)0); // count
            }
            else if (Level == 5)
            {
                // 2.0.1.7 S->C encrypted frame: rebuild body as
                //   [msgCount][TypeId][payload]
                // CRC8 the lot, prefix with the CRC byte, XOR-encrypt the whole
                // thing with the per-connection ratcheting cipher. The 0xDD 0x05
                // header stays in the clear.
                var msgCount = EncryptionManager.Instance.GetSCMessageCount(Connection.Id, Connection.AccountId);
                var bodyCrcStream = new PacketStream()
                    .Write(msgCount)
                    .Write(TypeId)
                    .Write(this);
                var bodyCrcBytes = bodyCrcStream.GetBytes();
                var crc8 = EncryptionManager.Instance.Crc8(bodyCrcBytes);
                var data = new PacketStream()
                    .Write(crc8)
                    .Write(bodyCrcBytes, false);
                var encrypted = EncryptionManager.Instance.StoCEncrypt(data.GetBytes());
                body = new PacketStream();
                body.Write(encrypted, false);
                EncryptionManager.Instance.IncSCMsgCount(Connection.Id, Connection.AccountId);
            }

            packet.Write(body, false);

            ps.Write(packet);
        }
        catch (Exception ex)
        {
            Logger.Fatal(ex);
            throw;
        }

        var logString = $"GamePacket: S->C type {TypeId:X3} {ToString()?.Substring(23)}{Verbose()}";
        switch (LogLevel)
        {
            case PacketLogLevel.Trace:
                Logger.Trace(logString);
                break;
            case PacketLogLevel.Debug:
                Logger.Debug(logString);
                break;
            case PacketLogLevel.Info:
                Logger.Info(logString);
                break;
            case PacketLogLevel.Warning:
                Logger.Warn(logString);
                break;
            case PacketLogLevel.Error:
                Logger.Error(logString);
                break;
            case PacketLogLevel.Fatal:
                Logger.Fatal(logString);
                break;
            case PacketLogLevel.Off:
            default:
                break;
        }

        return ps;
    }

    public override PacketBase<GameConnection> Decode(PacketStream ps)
    {
        try
        {
            Read(ps);

            var logString = $"GamePacket: C->S type {TypeId:X3} {ToString()?.Substring(23)}{Verbose()}";
            switch (LogLevel)
            {
                case PacketLogLevel.Trace:
                    Logger.Trace(logString);
                    break;
                case PacketLogLevel.Debug:
                    Logger.Debug(logString);
                    break;
                case PacketLogLevel.Info:
                    Logger.Info(logString);
                    break;
                case PacketLogLevel.Warning:
                    Logger.Warn(logString);
                    break;
                case PacketLogLevel.Error:
                    Logger.Error(logString);
                    break;
                case PacketLogLevel.Fatal:
                    Logger.Fatal(logString);
                    break;
                case PacketLogLevel.Off:
                default:
                    break;
            }

            Execute();
        }
        catch (Exception ex)
        {
            Logger.Error("GamePacket: C->S type {0:X3} {1}", TypeId, ToString()?.Substring(23));
            Logger.Fatal(ex);
            throw;
        }

        return this;
    }
}
