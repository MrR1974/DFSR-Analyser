using System.Diagnostics;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Cli;

internal static class Program
{
    private const string Usage = """
        dfsra — DFSR Analyser console harness

        Usage:
          dfsra import --case <folder> [--server <name>] [--per-folder] <file-or-folder>...
              Import Dfsr*.log / Dfsr*.log.gz into a case (created if missing).
              --server      file every log under this server name (default: header Computer name)
              --per-folder  each immediate sub-folder is one server, named after the folder
          dfsra stats --case <folder>
              Print per-server totals, signature counts, field coverage and top methods.
          dfsra files --case <folder>
              List imported files.
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
        var inputs = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--case": casePath = args[++i]; break;
                case "--server": server = args[++i]; break;
                case "--per-folder": perFolder = true; break;
                default: inputs.Add(args[i]); break;
            }
        }

        if (casePath is null && cmd != "bench")
        {
            Console.Error.WriteLine("--case is required.\n\n" + Usage);
            return 2;
        }

        try
        {
            return cmd switch
            {
                "import" => RunImport(casePath!, server, perFolder, inputs),
                "stats" => RunStats(casePath!),
                "files" => RunFiles(casePath!),
                "bench" => RunBench(inputs),
                _ => Fail($"Unknown command '{cmd}'."),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
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

    private static int RunImport(string casePath, string? server, bool perFolder, List<string> inputs)
    {
        if (inputs.Count == 0) return Fail("No input files or folders given.");

        using var store = CaseStore.Open(casePath);
        var importer = new Importer(store);
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

    private static int RunStats(string casePath)
    {
        using var store = CaseStore.Open(casePath);
        var stats = new CaseStats(store);

        Console.WriteLine($"{"Server",-16} {"Files",5} {"Events",11} {"Nested",11}  First (UTC)              Last (UTC)");
        foreach (var s in stats.Servers())
            Console.WriteLine($"{s.Server,-16} {s.Files,5} {s.Events,11:N0} {s.NestedLines,11:N0}  {s.FirstUtc:yyyy-MM-dd HH:mm:ss.fff}  {s.LastUtc:yyyy-MM-dd HH:mm:ss.fff}");
        Console.WriteLine($"Pending orphan blocks: {stats.PendingOrphans()}");

        PrintMatrix("Signatures", stats.CountBy(CaseStats.Signatures));
        PrintMatrix("Field coverage (events with the field)", stats.CountBy(CaseStats.FieldCoverage));

        Console.WriteLine("\nTop 25 module + Class::Method");
        foreach (var r in stats.TopClassMethods(25))
            Console.WriteLine($"  {r.Count,9:N0}  {r.Key}");
        return 0;
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
