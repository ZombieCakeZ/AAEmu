using AAEmu.ClientDb.Commands;

namespace AAEmu.ClientDb;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "decrypt" => DecryptCommand.Run(args[1..]),
                "encrypt" => EncryptCommand.Run(args[1..]),
                "diff" => DiffCommand.Run(args[1..]),
                "schema" => SchemaCommand.Run(args[1..]),
                "help" or "--help" or "-h" => PrintUsage(),
                _ => Fail($"unknown subcommand: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine("aaemu-clientdb - AA client compact.sqlite utility");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  aaemu-clientdb decrypt <input.sqlite>  <output.sqlite3>  [--version 1_2|2_0_1_7]");
        Console.WriteLine("  aaemu-clientdb encrypt <input.sqlite3> <output.sqlite>   [--version 1_2|2_0_1_7]");
        Console.WriteLine("  aaemu-clientdb diff    <old.sqlite3>   <new.sqlite3>     [--report out.md]");
        Console.WriteLine("  aaemu-clientdb schema  <file.sqlite3>                     [--table name]");
        Console.WriteLine();
        Console.WriteLine("Currently supported cipher versions: (none — see Docs/Decryptor_RE_Status.md)");
        return 0;
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 1;
    }
}
