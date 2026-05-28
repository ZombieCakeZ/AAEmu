using System.Collections.Concurrent;
using System.Text;

using AAEmu.Commons.Exceptions;
using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;

using NLog;

namespace AAEmu.Game.Core.Network.Game;

public class GameProtocolHandler : BaseProtocolHandler
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// List of packet handlers (level, id, class type)
    /// </summary>
    private readonly ConcurrentDictionary<byte, ConcurrentDictionary<uint, Type>> _packets;

    public GameProtocolHandler()
    {
        _packets = new ConcurrentDictionary<byte, ConcurrentDictionary<uint, Type>>();
        // 2.0.1.7 uses Level 1 for the join handshake, Level 2 for legacy ping,
        // and Level 5 for the encrypted main protocol (post-AES handshake).
        _packets.TryAdd(1, new ConcurrentDictionary<uint, Type>());
        _packets.TryAdd(2, new ConcurrentDictionary<uint, Type>());
        _packets.TryAdd(5, new ConcurrentDictionary<uint, Type>());
        _packetsMirror = _packets;
    }

    /// <summary>
    /// On connect event
    /// </summary>
    /// <param name="session"></param>
    public override void OnConnect(ISession session)
    {
        Logger.Info($"Connect from {session.Ip} established, session id: {session.SessionId}");
        try
        {
            var con = new GameConnection(session);
            con.OnConnect();
            GameConnectionTable.Instance.AddConnection(con);
        }
        catch (Exception e)
        {
            session.Close();
            Logger.Error(e);
        }
    }

    /// <summary>
    /// On disconnect event
    /// </summary>
    /// <param name="session"></param>
    public override void OnDisconnect(ISession session)
    {
        try
        {
            var con = GameConnectionTable.Instance.GetConnection(session.SessionId);
            if (con != null)
            {
                if (con.ActiveChar != null)
                {
                    // On crash, force people out of the chat channels so we don't get phantom or duplicates
                    Managers.ChatManager.Instance.LeaveAllChannels(con.ActiveChar);
                    // ObjectIdManager.Instance.ReleaseId(con.ActiveChar.BcId);
                }
                con.OnDisconnect();
                StreamManager.Instance.RemoveToken(con.Id);
                GameConnectionTable.Instance.RemoveConnection(session.SessionId);
            }
            else
            {
                Logger.Error($"{nameof(OnDisconnect)}: connection for session id {session.SessionId} is null");
            }
        }
        catch (Exception e)
        {
            session.Close();
            Logger.Error(e);
        }

        Logger.Info($"Client from {session.Ip} disconnected");
    }

    /// <summary>
    /// Handle incoming data for session
    /// </summary>
    /// <param name="session"></param>
    /// <param name="buf"></param>
    /// <param name="offset"></param>
    /// <param name="bytes"></param>
    public override void OnReceive(ISession session, byte[] buf, int offset, int bytes)
    {
        try
        {
            var connection = GameConnectionTable.Instance.GetConnection(session.SessionId);
            if (connection == null)
            {
                Logger.Error($"{nameof(OnReceive)}: connection for session id {session.SessionId} is null");
                return;
            }

            OnReceive(connection, buf, offset, bytes);
        }
        catch (Exception e)
        {
            session.Close();
            Logger.Error(e);
        }
    }

    /// <summary>
    /// Handle incoming data for GameConnection
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="buf"></param>
    /// <param name="offset"></param>
    /// <param name="bytes"></param>
    public void OnReceive(GameConnection connection, byte[] buf, int offset, int bytes)
    {
        try
        {
            var stream = new PacketStream();
            if (connection.LastPacket != null)
            {
                stream.Insert(0, connection.LastPacket);
                connection.LastPacket = null;
            }
            stream.Insert(stream.Count, buf, offset, bytes);
            while (stream is { Count: > 0 })
            {
                ushort len;
                try
                {
                    len = stream.ReadUInt16();
                }
                catch (MarshalException)
                {
                    //Logger.Warn("Error on reading type {0}", type);
                    stream.Rollback();
                    connection.LastPacket = stream;
                    stream = null;
                    continue;
                }
                var packetLen = len + stream.Pos;
                if (packetLen <= stream.Count)
                {
                    stream.Rollback();
                    var stream2 = new PacketStream();
                    stream2.Replace(stream, 0, packetLen);
                    if (stream.Count > packetLen)
                    {
                        var stream3 = new PacketStream();
                        stream3.Replace(stream, packetLen, stream.Count - packetLen);
                        stream = stream3;
                    }
                    else
                        stream = null;
                    stream2.ReadUInt16(); //len
                    stream2.ReadByte(); //unk
                    var level = stream2.ReadByte();

                    //byte crc = 0;
                    //byte counter = 0;
                    if (level == 1)
                    {
                        _ = stream2.ReadByte(); // TODO: verify 1.2 crc
                        _ = stream2.ReadByte(); // TODO: verify 1.2 counter
                    }

                    if (level == 5)
                    {
                        // 2.0.1.7 incoming Level-5 frame is XOR + AES encrypted.
                        // EncryptionManager.Decode expects [0xDD][level][crcKey][cipher...]
                        // so we hand it the buffer from absolute offset 2 (where the
                        // 0xDD byte lives). The plaintext we get back is
                        // [msgCount:1][type:2][payload:N]; we splice the type +
                        // payload back into stream2 in place of the encrypted body
                        // and let the normal type-dispatch path continue.
                        var stream2Bytes = stream2.GetBytes();
                        var input = new byte[stream2Bytes.Length - 2];
                        System.Buffer.BlockCopy(stream2Bytes, 2, input, 0, input.Length);
                        var output = AAEmu.Commons.Cryptography.EncryptionManager.Instance.Decode(
                            input, connection.Id, connection.AccountId);

                        var outBytes = new byte[output.Length + 5];
                        System.Buffer.BlockCopy(stream2Bytes, 0, outBytes, 0, 5);
                        System.Buffer.BlockCopy(output, 1, outBytes, 5, output.Length - 1);

                        var replacement = new PacketStream();
                        replacement.Write(outBytes);
                        stream2.Replace(replacement, 0, outBytes.Length);
                        // Skip the trailing crcKey byte; pointer lands on the type.
                        stream2.ReadUInt16();
                    }

                    var type = stream2.ReadUInt16();
                    _packets[level].TryGetValue(type, out var classType);
                    if (classType == null)
                    {
                        HandleUnknownPacket(connection, type, level, stream2);
                    }
                    else
                    {
                        var packet = (GamePacket)Activator.CreateInstance(classType);
                        packet!.Level = level;
                        packet.Connection = connection;
                        packet.Decode(stream2);
                    }
                }
                else
                {
                    stream.Rollback();
                    connection.LastPacket = stream;
                    stream = null;
                }
            }
        }
        catch (Exception e)
        {
            connection?.Shutdown();
            Logger.Error(e);
        }
    }

    /// <summary>
    /// Registers a GamePacket handler by Id and Level
    /// </summary>
    /// <param name="type"></param>
    /// <param name="level"></param>
    /// <param name="classType"></param>
    public void RegisterPacket(uint type, byte level, Type classType)
    {
        _packets[level][type] = classType;
    }

    /// <summary>
    /// Handle and Log unknown packet data
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="type"></param>
    /// <param name="level"></param>
    /// <param name="stream"></param>
    private static void HandleUnknownPacket(GameConnection connection, uint type, byte level, PacketStream stream)
    {
        var len = stream.Count - stream.Pos;
        var hex = new StringBuilder(len * 3);
        var ascii = new StringBuilder(len);
        for (var i = stream.Pos; i < stream.Count; i++)
        {
            var b = stream.Buffer[i];
            hex.AppendFormat("{0:x2} ", b);
            ascii.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');
        }

        // Try to find the packet name from CSOffsets / SCOffsets so we don't have to grep.
        // If the opcode is registered on a DIFFERENT level we hint at it — that's the
        // 'registered at Level 1 but client sends Level 5' situation.
        var name = LookupOpcodeName(type);
        var registeredOnOtherLevel = false;
        foreach (var lv in new byte[] { 1, 2, 5 })
        {
            if (lv == level) continue;
            if (_packetsMirror is { } pm && pm.TryGetValue(lv, out var map) && map.ContainsKey(type))
            {
                registeredOnOtherLevel = true;
                break;
            }
        }
        var hint = registeredOnOtherLevel ? "  (registered at a DIFFERENT level — try moving registration to this level)" : "";

        Logger.Error("Unknown packet 0x{0:X3}({1}) name={2} from {3}, {4} bytes:{5}\n  hex   : {6}\n  ascii : {7}",
            type, level, name, connection.Ip, len, hint, hex.ToString().TrimEnd(), ascii);
    }

    // Mirror reference so the helper above can introspect the dispatch table without
    // forcing every caller to thread it in. Set by the constructor on first build.
    private static ConcurrentDictionary<byte, ConcurrentDictionary<uint, Type>>? _packetsMirror;

    private static string LookupOpcodeName(uint opcode)
    {
        // Scan the constants in CSOffsets / SCOffsets via reflection so the name list
        // stays current automatically. Cheap because both classes are static + small.
        foreach (var fi in typeof(Packets.C2G.CSOffsets).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (fi.FieldType == typeof(ushort) && (ushort?)fi.GetValue(null) == opcode)
                return "C2G." + fi.Name;
        }
        foreach (var fi in typeof(Packets.G2C.SCOffsets).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (fi.FieldType == typeof(ushort) && (ushort?)fi.GetValue(null) == opcode)
                return "G2C." + fi.Name;
        }
        return "(unknown)";
    }
}
