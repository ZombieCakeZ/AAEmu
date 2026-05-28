using Microsoft.Data.Sqlite;

namespace AAEmu.ClientDb.Commands;

internal static class SchemaCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: aaemu-clientdb schema <file.sqlite3> [--table name]");
            return 1;
        }

        var path = args[0];
        var tableFilter = ExtractFlag(args, "--table");

        using var conn = new SqliteConnection($"Data Source=file:{path}?mode=ro;");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = tableFilter == null
            ? "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
            : "SELECT name, sql FROM sqlite_master WHERE type='table' AND name = @name";

        if (tableFilter != null)
            cmd.Parameters.AddWithValue("@name", tableFilter);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            Console.WriteLine($"-- {reader.GetString(0)}");
            Console.WriteLine(reader.GetString(1));
            Console.WriteLine();
        }

        return 0;
    }

    private static string? ExtractFlag(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
