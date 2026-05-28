namespace AAEmu.ClientDb.Commands;

internal static class EncryptCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: aaemu-clientdb encrypt <input.sqlite3> <output.sqlite> [--version v]");
            return 1;
        }

        var inputPath = args[0];
        var outputPath = args[1];
        var version = ExtractFlag(args, "--version") ?? "2_0_1_7";

        var cipher = CipherRegistry.ResolveOrThrow(version);

        Console.WriteLine($"encrypt: {inputPath} -> {outputPath} (cipher: {cipher.Version})");

        var data = File.ReadAllBytes(inputPath);

        ReadOnlySpan<byte> magic = "SQLite format 3\0"u8;
        if (data.Length < magic.Length || !data[..magic.Length].SequenceEqual(magic))
        {
            Console.Error.WriteLine("input is not a valid SQLite file (missing 'SQLite format 3' header).");
            return 1;
        }

        cipher.EncryptInPlace(data);
        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"wrote {data.Length:N0} bytes");
        return 0;
    }

    private static string? ExtractFlag(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
