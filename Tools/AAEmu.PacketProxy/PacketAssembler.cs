namespace AAEmu.PacketProxy;

/// <summary>
/// Reassembles raw byte chunks into AA-wire packets and logs each one.
/// AA wire format (S->C and C->S over the world / stream channels):
///
///   [length : ushort little-endian]  bytes following (excluding the length itself)
///   [0xDD   : byte]                  magic header (some streams)
///   [level  : byte]                  protocol level (1, 2, 5...)
///   [opcode : ushort little-endian]  the rest is payload
///
/// The stream-server (port 1238/1241 in our setup) uses a simpler frame:
///   [length : ushort][opcode : ushort][payload]
///
/// We detect which format on the fly by looking at the third byte. If it's
/// 0xDD we treat it as the world frame, otherwise as the stream frame.
/// </summary>
internal sealed class PacketAssembler(int sessionId, string direction, ProxyLogger logger)
{
    private readonly List<byte> _buffer = new(8192);

    public void Feed(byte[] data, int offset, int count)
    {
        _buffer.AddRange(new ArraySegment<byte>(data, offset, count));
        Drain();
    }

    private void Drain()
    {
        while (_buffer.Count >= 2)
        {
            // Length field is ushort. Some implementations encode "length of following data
            // including opcode" vs "length of payload only" — we treat it as bytes-following
            // since that matches AAEmu's own emit/receive logic.
            var length = (ushort)(_buffer[0] | (_buffer[1] << 8));
            var packetLen = 2 + length;

            if (_buffer.Count < packetLen)
                return; // need more bytes

            var bytes = _buffer.GetRange(0, packetLen).ToArray();
            _buffer.RemoveRange(0, packetLen);
            LogPacket(bytes);
        }
    }

    private void LogPacket(byte[] bytes)
    {
        // Stream-server frame (no DD): [len:2][opcode:2][payload]
        // World frame (DD): [len:2][DD:1][level:1][opcode:2][payload]  (level 5 has more)
        var hasDdMagic = bytes.Length >= 3 && bytes[2] == 0xDD;
        ushort opcode = 0;
        byte level = 0;
        int payloadStart;

        if (hasDdMagic && bytes.Length >= 6)
        {
            level = bytes[3];
            opcode = (ushort)(bytes[4] | (bytes[5] << 8));
            payloadStart = 6;
            // Level 1 frames also carry a hash + count byte after the level (some flows).
            // We do not try to peel those off for the dump; they're visible in the hex.
        }
        else if (bytes.Length >= 4)
        {
            opcode = (ushort)(bytes[2] | (bytes[3] << 8));
            payloadStart = 4;
        }
        else
        {
            payloadStart = bytes.Length;
        }

        var name = hasDdMagic
            ? OpcodeNames.GameLookup(direction, opcode, level)
            : OpcodeNames.StreamLookup(direction, opcode);

        var hex = HexDump.Format(bytes);
        var payloadHex = bytes.Length > payloadStart
            ? HexDump.Format(bytes.AsSpan(payloadStart).ToArray())
            : "(empty)";

        logger.WritePacket(sessionId, direction, hasDdMagic ? "WORLD" : "STREAM",
            opcode, level, name, bytes.Length, hex, payloadHex);
    }
}
