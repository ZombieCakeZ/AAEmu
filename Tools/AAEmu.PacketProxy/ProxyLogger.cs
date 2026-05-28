namespace AAEmu.PacketProxy;

/// <summary>
/// Writes a flat, grep-friendly trace to disk + mirrors a summary to stdout.
/// Format per packet:
///
///   [time] [session N] [LABEL] DIR FRAME opcode=0xNNN(LEVEL) name="..." len=B
///     hex   : ...
///     body  : ...
///
/// Body is the bytes after the header so you can diff payload-only.
/// </summary>
internal sealed class ProxyLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string _label;
    private readonly object _gate = new();

    public ProxyLogger(string path, string label)
    {
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        _label = label;
    }

    public void WriteHeader()
    {
        Write($"--- proxy run started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
    }

    public void WriteEvent(int sessionId, string message)
    {
        Write($"[{DateTime.Now:HH:mm:ss.fff}] [session {sessionId}] [{_label}] {message}");
    }

    public void WritePacket(int sessionId, string direction, string frame, ushort opcode, byte level,
        string name, int totalLen, string hex, string body)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var levelStr = frame == "WORLD" ? $"({level})" : "";
        var summary = $"[{ts}] [session {sessionId}] [{_label}] {direction} {frame} opcode=0x{opcode:X3}{levelStr} name=\"{name}\" len={totalLen}";

        lock (_gate)
        {
            Console.WriteLine(summary);
            _writer.WriteLine(summary);
            _writer.WriteLine($"  hex  : {hex}");
            _writer.WriteLine($"  body : {body}");
            _writer.WriteLine();
        }
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer.Dispose();
}
