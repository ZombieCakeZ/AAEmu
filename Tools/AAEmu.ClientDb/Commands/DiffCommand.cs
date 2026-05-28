using Microsoft.Data.Sqlite;

namespace AAEmu.ClientDb.Commands;

internal static class DiffCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: aaemu-clientdb diff <old.sqlite3> <new.sqlite3> [--report out.md]");
            return 1;
        }

        var oldPath = args[0];
        var newPath = args[1];
        var reportPath = ExtractFlag(args, "--report");

        var oldTables = ReadTables(oldPath);
        var newTables = ReadTables(newPath);

        var oldNames = oldTables.Keys.ToHashSet();
        var newNames = newTables.Keys.ToHashSet();

        var added = newNames.Except(oldNames).OrderBy(s => s).ToList();
        var removed = oldNames.Except(newNames).OrderBy(s => s).ToList();
        var common = newNames.Intersect(oldNames).OrderBy(s => s).ToList();

        var writer = reportPath != null ? new StreamWriter(reportPath) : null;
        void Out(string s) { Console.WriteLine(s); writer?.WriteLine(s); }

        Out($"# Schema diff: {Path.GetFileName(oldPath)} -> {Path.GetFileName(newPath)}");
        Out("");
        Out($"- Added tables: {added.Count}");
        Out($"- Removed tables: {removed.Count}");
        Out($"- Tables in both: {common.Count}");
        Out("");

        if (added.Count > 0)
        {
            Out("## Added");
            foreach (var t in added)
                Out($"- {t} ({newTables[t]} rows)");
            Out("");
        }

        if (removed.Count > 0)
        {
            Out("## Removed");
            foreach (var t in removed)
                Out($"- {t} ({oldTables[t]} rows)");
            Out("");
        }

        Out("## Row-count delta (common tables)");
        var rowDelta = common
            .Select(t => (Name: t, Old: oldTables[t], New: newTables[t]))
            .Where(x => x.Old != x.New)
            .OrderByDescending(x => Math.Abs(x.New - x.Old));

        foreach (var (n, o, ne) in rowDelta)
            Out($"- {n}: {o:N0} -> {ne:N0} ({ne - o:+#;-#;0})");

        writer?.Dispose();
        return 0;
    }

    private static Dictionary<string, long> ReadTables(string path)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        using var conn = new SqliteConnection($"Data Source=file:{path}?mode=ro;");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = 0;
        }

        foreach (var name in result.Keys.ToList())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{name}\"";
            result[name] = (long)cmd.ExecuteScalar()!;
        }

        return result;
    }

    private static string? ExtractFlag(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
