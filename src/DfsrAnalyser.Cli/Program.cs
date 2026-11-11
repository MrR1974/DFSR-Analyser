using System.Diagnostics;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Cli;

internal static class Program
{
    private const string Usage = """
        dfsra — DFSR Analyser console harness

        Usage:
          dfsra import --case <folder> [--server <name>] [--per-folder] [--drop-plumbing-detail] <file-or-folder>...
              Import Dfsr*.log / Dfsr*.log.gz into a case (created if missing).
              --server               file every log under this server name (default: header Computer name)
              --per-folder           each immediate sub-folder is one server, named after the folder
              --drop-plumbing-detail do not store nested detail of Plumbing-category events
          dfsra stats --case <folder> [--all]
              Print per-server totals, category counts (--all includes hidden categories),
              signature counts, field coverage and top methods.
          dfsra rules --case <folder> apply [<rules.json>]
              Re-categorise the case with its rules.json, or with the given file (which becomes the case's rules).
          dfsra names --case <folder>
              Re-resolve GUIDs to names (runs automatically after import) and print what was found.
          dfsra rules export-default <path>
              Write the built-in default rules to a file.
          dfsra files --case <folder>
              List imported files.
          dfsra sql --case <folder> "<query>"
              Run an ad-hoc read query against the case and print the rows (tab-separated).
          dfsra bench <file-or-folder>...
              Time each import stage (decompress+read, parse, extract) without writing a case.
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var cmd = args[0];
        string? casePath = null, server = null;
        var perFolder = false;
        var dropPlumbing = false;
        var all = false;
        var inputs = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--case": casePath = args[++i]; break;
                case "--server": server = args[++i]; break;
                case "--per-folder": perFolder = true; break;
                case "--drop-plumbing-detail": dropPlumbing = true; break;
                case "--all": all = true; break;
                default: inputs.Add(args[i]); break;
            }
        }

        var needsCase = cmd != "bench" && !(cmd == "rules" && inputs.FirstOrDefault() == "export-default");
        if (casePath is null && needsCase)
        {
            Console.Error.WriteLine("--case is required.\n\n" + Usage);
            return 2;
        }

        try
        {
            return cmd switch
            {
                "import" => RunImport(casePath!, server, perFolder, dropPlumbing, inputs),
                "stats" => RunStats(casePath!, all),
                "rules" => RunRules(casePath, inputs),
                "names" => RunNames(casePath!),
                "files" => RunFiles(casePath!),
                "bench" => RunBench(inputs),
                "sql" => RunSql(casePath!, string.Join(' ', inputs)),
                _ => Fail($"Unknown command '{cmd}'."),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Core.Taxonomy.RuleSetException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message + "\n\n" + Usage);
        return 2;
    }

    private static int RunImport(string casePath, string? server, bool perFolder, bool dropPlumbing, List<string> inputs)
    {
        if (inputs.Count == 0) return Fail("No input files or folders given.");

        using var store = CaseStore.Open(casePath);
        var importer = new Importer(store) { DropPlumbingDetail = dropPlumbing };
        var sw = Stopwatch.StartNew();
        var results = new List<FileImportResult>();

        if (perFolder)
        {
            foreach (var root in inputs)
                foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var files = Importer.FindLogFiles([dir]);
                    if (files.Count > 0) results.AddRange(importer.Import(files, Path.GetFileName(dir), ConsoleProgress()));
                }
        }
        else
        {
            results.AddRange(importer.Import(Importer.FindLogFiles(inputs), server, ConsoleProgress()));
        }
        sw.Stop();
        Console.Error.WriteLine();

        Console.WriteLine($"{"Server",-16} {"Idx",4} {"Seq",4} {"Outcome",-10} {"Events",9} {"Lines",9} {"Secs",7} {"Parse",6} {"Commit",6} {"Load",6}  File");
        foreach (var r in results)
            Console.WriteLine($"{r.Server,-16} {r.Index,4} {r.Sequence,4} {r.Outcome,-10} {r.Events,9:N0} {r.Lines,9:N0} {r.Elapsed.TotalSeconds,7:F2} {r.ParseTime.TotalSeconds,6:F2} {r.CommitTime.TotalSeconds,6:F2} {r.LoadTime.TotalSeconds,6:F2}  {Path.GetFileName(r.Path)}{(r.Error is null ? "" : "  ERROR: " + r.Error)}");

        var lines = results.Sum(r => (long)r.Lines);
        var events = results.Sum(r => (long)r.Events);
        Console.WriteLine();
        Console.WriteLine($"Imported {events:N0} events from {lines:N0} lines in {sw.Elapsed.TotalSeconds:F1}s " +
                          $"({(sw.Elapsed.TotalSeconds > 0 ? lines / sw.Elapsed.TotalSeconds : 0):N0} lines/s).");
        store.Dispose();
        Console.WriteLine($"Case database: {new FileInfo(Path.Combine(casePath, CaseStore.DatabaseFileName)).Length / 1048576.0:N1} MB");
        return results.Any(r => r.Outcome == FileOutcome.Failed) ? 1 : 0;
    }

    private static IProgress<ImportProgress> ConsoleProgress() =>
        new SyncProgress<ImportProgress>(p => Console.Error.Write($"\r[{p.FileNumber}/{p.FileCount}] {Path.GetFileName(p.Path),-24} {p.EventsSoFar,12:N0} events"));

    private static int RunStats(string casePath, bool includeHidden)
    {
        using var store = CaseStore.Open(casePath);
        var stats = new CaseStats(store);

        Console.WriteLine($"{"Server",-16} {"Files",5} {"Events",11} {"Nested",11}  First (UTC)              Last (UTC)");
        foreach (var s in stats.Servers())
            Console.WriteLine($"{s.Server,-16} {s.Files,5} {s.Events,11:N0} {s.NestedLines,11:N0}  {s.FirstUtc:yyyy-MM-dd HH:mm:ss.fff}  {s.LastUtc:yyyy-MM-dd HH:mm:ss.fff}");
        Console.WriteLine($"Pending orphan blocks: {stats.PendingOrphans()}");
        var (keptRows, keptChars, dropped) = stats.NestedVolume();
        Console.WriteLine($"Nested detail: {keptRows:N0} events kept ({keptChars / 1048576.0:N1} M chars), {dropped:N0} events dropped");

        PrintCategories(stats.CategoryCounts(), includeHidden);

        PrintMatrix("Signatures", stats.CountBy(CaseStats.Signatures));
        PrintMatrix("Field coverage (events with the field)", stats.CountBy(CaseStats.FieldCoverage));

        Console.WriteLine("\nTop 25 module + Class::Method");
        foreach (var r in stats.TopClassMethods(25))
            Console.WriteLine($"  {r.Count,9:N0}  {r.Key}");
        return 0;
    }

    private static void PrintCategories(List<CaseStats.CategoryCount> rows, bool includeHidden)
    {
        var servers = rows.Select(r => r.Server).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"\nCategories{(includeHidden ? " (all; * = hidden by default)" : " (visible; --all for hidden)")}");
        Console.WriteLine($"  {"",-46}" + string.Concat(servers.Select(s => $"{s,14}")));
        foreach (var group in rows.GroupBy(r => r.Group))
        {
            var cats = group.GroupBy(r => (r.Category, r.Hidden)).Where(c => includeHidden || !c.Key.Hidden).ToList();
            var hiddenTotal = group.Where(r => r.Hidden).Sum(r => r.Count);
            if (cats.Count == 0 && hiddenTotal == 0) continue;
            Console.WriteLine($"  [{group.Key}]");
            foreach (var c in cats)
            {
                var name = (c.Key.Hidden ? "* " : "  ") + c.Key.Category;
                Console.WriteLine($"  {name,-46}" + string.Concat(servers.Select(s => $"{c.Where(r => r.Server == s).Sum(r => r.Count),14:N0}")));
            }
            if (!includeHidden && hiddenTotal > 0)
                Console.WriteLine($"  {"  (hidden: " + group.Where(r => r.Hidden).Select(r => r.Category).Distinct().Count() + " categories)",-46}" +
                                  string.Concat(servers.Select(s => $"{group.Where(r => r.Hidden && r.Server == s).Sum(r => r.Count),14:N0}")));
        }
    }

    private static int RunNames(string casePath)
    {
        using var store = CaseStore.Open(casePath);
        var sw = Stopwatch.StartNew();
        var summary = new Core.Names.NameResolver(store).Resolve();
        Console.WriteLine($"Resolved in {sw.Elapsed.TotalSeconds:F2}s: {summary}");

        void Section(string title, string sql)
        {
            Console.WriteLine($"\n{title}");
            foreach (var row in store.Query(sql))
                Console.WriteLine("  " + string.Join("  |  ", row.Select(v => v switch { null => "-", Guid g => "{" + g.ToString().ToUpperInvariant() + "}", _ => v.ToString() })));
        }

        Section("Replication groups", "SELECT name, guid, source FROM name_rg ORDER BY name");
        Section("Replicated folders", "SELECT f.name, g.name AS rg, f.guid, f.source FROM name_rf f LEFT JOIN name_rg g ON g.guid = f.rg_guid ORDER BY f.name");
        Section("Members", "SELECT name, guid FROM name_member ORDER BY name");
        Section("Connections (as seen by each server)", """
            SELECT s.name AS server, CASE WHEN c.inbound THEN 'from ' ELSE 'to ' END || c.partner AS connection,
                   c.enabled, c.rdc_enabled, g.name AS rg, c.conn_id
            FROM name_conn c JOIN server s ON s.server_id = c.server_id LEFT JOIN name_rg g ON g.guid = c.rg_guid
            ORDER BY server, connection
            """);
        Section("DB GUID -> server (inferred from local changes)", "SELECT server, guid, evidence, ambiguous FROM name_db ORDER BY server");
        Section("Replicated folder configuration", """
            SELECT s.name AS server, f.name AS rf, c.root_path, c.staging_size_mb, c.conflict_size_mb, c.is_primary, c.file_filter
            FROM rf_config c JOIN server s ON s.server_id = c.server_id LEFT JOIN name_rf f ON f.guid = c.rf_guid
            ORDER BY server, rf
            """);
        return 0;
    }

    private static int RunRules(string? casePath, List<string> inputs)
    {
        switch (inputs.FirstOrDefault())
        {
            case "export-default":
                if (inputs.Count < 2) return Fail("export-default needs a path.");
                File.WriteAllText(inputs[1], Core.Taxonomy.RuleSet.DefaultJson());
                Console.WriteLine($"Wrote {inputs[1]}");
                return 0;

            case "apply":
                using (var store = CaseStore.Open(casePath!))
                {
                    var sw = Stopwatch.StartNew();
                    if (inputs.Count >= 2) store.SaveAndApplyRules(Core.Taxonomy.RuleSet.Load(inputs[1]));
                    else store.ApplyRules();
                    Console.WriteLine($"Re-categorised in {sw.Elapsed.TotalSeconds:F1}s using {store.Rules.Rules.Count} rules, {store.Rules.Categories.Count} categories.");
                }
                return 0;

            default:
                return Fail("rules: expected 'apply' or 'export-default'.");
        }
    }

    private static void PrintMatrix(string title, List<CountRow> rows)
    {
        var servers = rows.Select(r => r.Server).Distinct().ToList();
        Console.WriteLine($"\n{title}");
        Console.WriteLine($"  {"",-44}" + string.Concat(servers.Select(s => $"{s,14}")));
        foreach (var g in rows.GroupBy(r => r.Key))
            Console.WriteLine($"  {g.Key,-44}" + string.Concat(servers.Select(s => $"{g.FirstOrDefault(r => r.Server == s)?.Count ?? 0,14:N0}")));
    }

    private static int RunFiles(string casePath)
    {
        using var store = CaseStore.Open(casePath);
        Console.WriteLine($"{"Id",3} {"Srv",3} {"Idx",4} {"Seq",4} {"Lvl",3} {"Lines",8} {"Events",8} {"Orph",4} {"Done",5}  {"First (UTC)",-23} File");
        foreach (var f in store.ListFiles())
            Console.WriteLine($"{f.FileId,3} {f.ServerId,3} {f.Index,4} {f.Sequence,4} {f.LogLevel,3} {f.LineCount,8:N0} {f.EventCount,8:N0} {f.OrphanLines,4} {f.IsComplete,5}  {f.FirstTsUtc:yyyy-MM-dd HH:mm:ss.fff} {f.FileName}  [{f.TzBasis}]");
        return 0;
    }

    private static int RunSql(string casePath, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Fail("No query given.");
        using var store = CaseStore.Open(casePath);
        foreach (var row in store.Query(query))
            Console.WriteLine(string.Join('\t', row.Select(v => v switch
            {
                null => "NULL",
                DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                _ => v.ToString(),
            })));
        return 0;
    }

    private static int RunBench(List<string> inputs)
    {
        var files = Importer.FindLogFiles(inputs);
        long lines = 0, events = 0;

        var sw = Stopwatch.StartNew();
        foreach (var f in files)
        {
            using var r = new StreamReader(Core.Parsing.LogFileReader.OpenStream(f));
            while (r.ReadLine() is not null) lines++;
        }
        var tRead = sw.Elapsed;

        sw.Restart();
        foreach (var f in files)
        {
            using var r = new Core.Parsing.LogFileReader(f);
            foreach (var _ in r.ReadEvents()) events++;
        }
        var tParse = sw.Elapsed;

        sw.Restart();
        foreach (var f in files)
        {
            using var r = new Core.Parsing.LogFileReader(f);
            foreach (var e in r.ReadEvents()) Core.Parsing.FieldExtractor.Extract(e);
        }
        var tExtract = sw.Elapsed;

        var tmp = Path.Combine(Path.GetTempPath(), "dfsra-bench-" + Guid.NewGuid().ToString("N"));

        sw.Restart();
        long csvBytes = 0;
        foreach (var f in files)
        {
            var csv = new CsvBulkWriter(tmp);
            using (csv)
            {
                using var r = new Core.Parsing.LogFileReader(f);
                foreach (var e in r.ReadEvents()) csv.Write(e.LineNo, 1, 1, e, e.LocalTime, Core.Parsing.FieldExtractor.Extract(e));
            }
            csvBytes += new FileInfo(csv.EventPath).Length + new FileInfo(csv.NestedPath).Length;
            csv.DeleteFiles();
        }
        var tCsv = sw.Elapsed;

        var tImports = new List<(int Workers, TimeSpan Time)>();
        foreach (var workers in new[] { 1, 2, 3, 4, 7 })
        {
            sw.Restart();
            using (var store = CaseStore.Open(tmp))
                new Importer(store) { MaxParallelism = workers }.Import(files);
            tImports.Add((workers, sw.Elapsed));
            Directory.Delete(tmp, true);
        }
        var tImport = tImports.Min(t => t.Time);
        foreach (var (w, t) in tImports) Console.WriteLine($"  import with {w} parse worker(s): {t.TotalSeconds:F2}s");

        Console.WriteLine($"{files.Count} files, {lines:N0} lines, {events:N0} events (single-threaded stages, then full import)");
        Console.WriteLine($"  read lines              {tRead.TotalSeconds,6:F2}s  {lines / tRead.TotalSeconds,12:N0} lines/s");
        Console.WriteLine($"  read + parse            {tParse.TotalSeconds,6:F2}s  {lines / tParse.TotalSeconds,12:N0} lines/s");
        Console.WriteLine($"  read + parse + extract  {tExtract.TotalSeconds,6:F2}s  {lines / tExtract.TotalSeconds,12:N0} lines/s");
        Console.WriteLine($"  ... + write CSV         {tCsv.TotalSeconds,6:F2}s  {lines / tCsv.TotalSeconds,12:N0} lines/s  ({csvBytes / 1048576.0:N0} MB)");
        Console.WriteLine($"  full import (pipeline)  {tImport.TotalSeconds,6:F2}s  {lines / tImport.TotalSeconds,12:N0} lines/s");
        return 0;
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; for a console we want synchronous writes.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
