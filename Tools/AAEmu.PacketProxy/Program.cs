using System.Net;
using System.Net.Sockets;
using AAEmu.PacketProxy;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintUsage();
    return 0;
}

var settings = ProxyOptions.Parse(args);
if (settings is null)
{
    PrintUsage();
    return 1;
}

var (listenEndpoint, upstreamHost, upstreamPort, logPath, label) = settings.Value;

Console.WriteLine($"== AAEmu PacketProxy ==");
Console.WriteLine($"Listen   : {listenEndpoint}");
Console.WriteLine($"Upstream : {upstreamHost}:{upstreamPort}");
Console.WriteLine($"Log file : {logPath}");
Console.WriteLine($"Label    : {label}");

var logger = new ProxyLogger(logPath, label);
logger.WriteHeader();

using var listener = new TcpListener(listenEndpoint);
listener.Start();

Console.WriteLine($"Listening. Connect your client at {listenEndpoint}");
Console.WriteLine("Ctrl+C to stop.");
Console.WriteLine();

var ctsAll = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("Shutting down...");
    ctsAll.Cancel();
    listener.Stop();
};

var sessionId = 0;
try
{
    while (!ctsAll.IsCancellationRequested)
    {
        TcpClient client;
        try
        {
            client = await listener.AcceptTcpClientAsync(ctsAll.Token);
        }
        catch (OperationCanceledException) { break; }
        catch (SocketException) { break; }

        var id = Interlocked.Increment(ref sessionId);
        _ = Task.Run(() => HandleSessionAsync(client, id, upstreamHost, upstreamPort, logger, ctsAll.Token));
    }
}
finally
{
    logger.Dispose();
}

return 0;

static async Task HandleSessionAsync(TcpClient downstream, int id, string upstreamHost, int upstreamPort,
    ProxyLogger logger, CancellationToken ct)
{
    var ip = ((IPEndPoint)downstream.Client.RemoteEndPoint!).ToString();
    Console.WriteLine($"[session {id}] client {ip} -> connecting upstream...");
    logger.WriteEvent(id, $"client {ip} connected; opening upstream {upstreamHost}:{upstreamPort}");

    using (downstream)
    {
        TcpClient upstream;
        try
        {
            upstream = new TcpClient();
            await upstream.ConnectAsync(upstreamHost, upstreamPort, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[session {id}] upstream connect failed: {ex.Message}");
            logger.WriteEvent(id, $"upstream connect failed: {ex.Message}");
            return;
        }

        using (upstream)
        {
            var downstreamStream = downstream.GetStream();
            var upstreamStream = upstream.GetStream();

            var c2s = PumpAsync(id, "C->S", downstreamStream, upstreamStream, logger, ct);
            var s2c = PumpAsync(id, "S->C", upstreamStream, downstreamStream, logger, ct);

            await Task.WhenAny(c2s, s2c);
            Console.WriteLine($"[session {id}] one side closed; shutting down session");
            logger.WriteEvent(id, "session closed");
        }
    }
}

static async Task PumpAsync(int sessionId, string direction, NetworkStream from, NetworkStream to,
    ProxyLogger logger, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    var assembler = new PacketAssembler(sessionId, direction, logger);
    try
    {
        while (true)
        {
            var n = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n == 0) return;

            // Forward immediately so the live conversation isn't slowed by the proxy.
            await to.WriteAsync(buffer.AsMemory(0, n), ct);

            assembler.Feed(buffer, 0, n);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { /* peer closed */ }
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  aaemu-packetproxy --listen <ip:port> --upstream <host:port> [--log <file>] [--label <name>]");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  Game proxy:");
    Console.WriteLine("    aaemu-packetproxy --listen 0.0.0.0:1240 --upstream 127.0.0.1:1239 --log game.log --label GAME");
    Console.WriteLine("  Stream proxy:");
    Console.WriteLine("    aaemu-packetproxy --listen 0.0.0.0:1241 --upstream 127.0.0.1:1238 --log stream.log --label STREAM");
    Console.WriteLine();
    Console.WriteLine("Then point your client at <proxy ip>:1240 for the game server etc. Every byte gets logged");
    Console.WriteLine("with hex + AA wire-format decode (opcode, level) so you can diff Vanilla vs alpha behavior.");
}
