namespace AAEmu.ClientDb.Commands;

internal static class DecryptCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: aaemu-clientdb decrypt <input.sqlite> <output.sqlite3> [--version v]");
            return 1;
        }

        var inputPath = args[0];
        var outputPath = args[1];
        var version = ExtractFlag(args, "--version") ?? "2_0_1_7";

        var cipher = CipherRegistry.ResolveOrThrow(version);

        Console.WriteLine($"decrypt: {inputPath} -> {outputPath} (cipher: {cipher.Version})");

        var data = File.ReadAllBytes(inputPath);
        cipher.DecryptInPlace(data);

        if (!HasSqliteHeader(data))
        {
            Console.Error.WriteLine("warning: output does not start with the SQLite header — cipher or key is wrong.");
        }

        File.WriteAllBytes(outputPath, data);
        Console.WriteLine($"wrote {data.Length:N0} bytes");
        return 0;
    }

    private static bool HasSqliteHeader(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> magic = "SQLite format 3\0"u8;
        return data.Length >= magic.Length && data[..magic.Length].SequenceEqual(magic);
    }

    private static string? ExtractFlag(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
