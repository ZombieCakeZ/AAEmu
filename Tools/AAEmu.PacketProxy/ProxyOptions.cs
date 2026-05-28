using System.Net;

namespace AAEmu.PacketProxy;

internal static class ProxyOptions
{
    public static (IPEndPoint Listen, string UpstreamHost, int UpstreamPort, string LogPath, string Label)?
        Parse(string[] args)
    {
        string? listen = null;
        string? upstream = null;
        string? logPath = null;
        string? label = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--listen":
                    listen = args[++i]; break;
                case "--upstream":
                    upstream = args[++i]; break;
                case "--log":
                    logPath = args[++i]; break;
                case "--label":
                    label = args[++i]; break;
                default:
                    Console.Error.WriteLine($"unknown arg: {args[i]}");
                    return null;
            }
        }

        if (listen is null || upstream is null)
            return null;

        var listenEp = ParseEndpoint(listen);
        var (upHost, upPort) = ParseHostPort(upstream);
        logPath ??= $"packets-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        label ??= "PROXY";

        return (listenEp, upHost, upPort, logPath, label);
    }

    private static IPEndPoint ParseEndpoint(string spec)
    {
        var parts = spec.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            throw new ArgumentException($"bad listen spec: {spec}");
        var host = parts[0];
        var addr = host is "*" or "0.0.0.0" or "any"
            ? IPAddress.Any
            : IPAddress.Parse(host);
        return new IPEndPoint(addr, port);
    }

    private static (string Host, int Port) ParseHostPort(string spec)
    {
        var parts = spec.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            throw new ArgumentException($"bad upstream spec: {spec}");
        return (parts[0], port);
    }
}
