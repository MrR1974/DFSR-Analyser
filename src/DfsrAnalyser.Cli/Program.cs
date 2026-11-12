using System.Diagnostics;
using System.Globalization;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Cli;

internal static class Program
{
    private const string Usage = """
        dfsra — DFSR Analyser command line

        Usage:
          dfsra import --case <folder> [--server <name>] [--per-folder] [--drop-plumbing-detail] <file-or-folder>...
              Import Dfsr*.log / Dfsr*.log.gz into a case (created if missing).
              --server               file every log under this server name (default: header Computer name)
              --per-folder           each immediate sub-folder is one server, named after the folder
              --drop-plumbing-detail do not store nested detail of Plumbing-category events
              Each run writes <case>\logs\import-*.log.
          dfsra stats --case <folder> [--all] [--from <time>] [--to <time>] [--utc] [--file <pattern>]
              Print per-server totals, category counts (--all includes hidden categories),
              signature counts, field coverage and top methods.
              With --from/--to/--file, print category x server counts for that view only, as the app shows it:
              --from / --to   "yyyy-MM-dd HH:mm[:ss]", local time (--utc: UTC); --from inclusive, --to exclusive
              --file          file name wildcards: * and ?, ';' between alternatives, '!' to exclude,
                              e.g. "file_00*.txt"  or  "*.txt;!file_1*"
          dfsra top --case <folder> [--by <list>] [--top <n>] [filters]
              Top-N table for the view: --by files (default), folders, ext, origin, partner, server,
              category, error, method; --top <n> (default 25).
          dfsra trace --case <folder> (--uid "{GUID}-vN" | --name <file name or pattern>) [--all] [--from/--to] [--utc]
              Every event of one file (UID) on every server, with per-version milestones and latency.
              --name lists the matching UIDs and traces the only one, or asks for --uid.
              --all includes hidden categories in the event list.
          dfsra export --case <folder> --what events|grid|topn|trace --out <file.csv> [filters]
                       [--levels category,server,file] [--by <list>] [--top <n>] [--uid <uid>]
              CSV of the events in view, the grid (one row per combination of --levels; default
              category,server,file), a Top-N table, or a trace (with --uid).
              Times are UTC, plus a local-time column unless --utc is given.
          Filters for stats, top and export: --from, --to, --utc, --file as for stats; --all includes
              hidden categories.
          dfsra verify --case <folder>
              Check the case for duplicate events or files, miscounted files, missing Indexes and waiting
              roll fragments. Exit code 1 when a defect is found.
          dfsra compare --case <a> --case <b>
              Compare two cases file by file (server, Index, first line): events, categories and detail over
              the lines both hold. Exit code 1 when any shared file differs.
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

        Remote collection (dfsra discover / test / collect / topup / import-evtx):
        """ + "\r\n" + CollectCommands.Usage;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var cmd = args[0];
        if (CollectCommands.Handles(cmd)) return CollectCommands.Run(args);

        string? casePath = null, server = null, from = null, to = null, filePattern = null;
        string? by = null, uid = null, name = null, what = null, outPath = null, levels = null;
        var top = 25;
        var cases = new List<string>();
        var utc = false;
        var perFolder = false;
        var dropPlumbing = false;
        var all = false;
        var inputs = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--case": casePath ??= args[i + 1]; cases.Add(args[++i]); break;
                case "--from": from = args[++i]; break;
                case "--to": to = args[++i]; break;
                case "--utc": utc = true; break;
                case "--file": filePattern = args[++i]; break;
                case "--server": server = args[++i]; break;
                case "--per-folder": perFolder = true; break;
                case "--drop-plumbing-detail": dropPlumbing = true; break;
                case "--all": all = true; break;
                case "--by": by = args[++i]; break;
                case "--top": top = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--uid": uid = args[++i]; break;
                case "--name": name = args[++i]; break;
                case "--what": what = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--levels": levels = args[++i]; break;
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
                "stats" => from is null && to is null && filePattern is null
                    ? RunStats(casePath!, all)
                    : RunFilteredStats(casePath!, all, ParseTime(from, utc), ParseTime(to, utc), filePattern),
                "top" => RunTop(casePath!, Filter(all, ParseTime(from, utc), ParseTime(to, utc), filePattern), by ?? "files", top),
                "trace" => RunTrace(casePath!, uid, name, all, ParseTime(from, utc), ParseTime(to, utc), utc),
                "export" => RunExport(casePath!, what, outPath, Filter(all, ParseTime(from, utc), ParseTime(to, utc), filePattern),
                    levels, by ?? "files", top, uid, utc),
                "verify" => RunVerify(casePath!),
                "compare" => cases.Count == 2 ? RunCompare(cases[0], cases[1]) : Fail("compare needs two --case options."),
                "rules" => RunRules(casePath, inputs),
                "names" => RunNames(casePath!),
                "files" => RunFiles(casePath!),
                "bench" => RunBench(inputs),
                "sql" => RunSql(casePath!, string.Join(' ', inputs)),
                _ => Fail($"Unknown command '{cmd}'."),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or FormatException or Core.Taxonomy.RuleSetException)
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
        var sw = Stopwatch.StartNew();
        var options = new ImportOptions(
            perFolder ? ServerNaming.FolderName : server is not null ? ServerNaming.Fixed : ServerNaming.Header, server, dropPlumbing);
        // --per-folder: each immediate sub-folder of an input is one server, named after the folder.
        var job = ImportJob.Run(store, inputs, options, ConsoleProgress());
        foreach (var w in job.Warnings) Console.Error.WriteLine("WARNING: " + w);
        var results = job.Files;
        sw.Stop();
        Console.Error.WriteLine();

        Console.WriteLine($"{"Server",-16} {"Idx",4} {"Seq",4} {"Outcome",-10} {"Events",9} {"Lines",9} {"Secs",7} {"Parse",6} {"Commit",6} {"Load",6}  File");
        foreach (var r in results)
            Console.WriteLine($"{r.Server,-16} {r.Index,4} {r.Sequence,4} {r.Outcome,-10} {r.Events,9:N0} {r.Lines,9:N0} {r.Elapsed.TotalSeconds,7:F2} {r.ParseTime.TotalSeconds,6:F2} {r.CommitTime.TotalSeconds,6:F2} {r.LoadTime.TotalSeconds,6:F2}  {Path.GetFileName(r.Path)}{(r.Error is null ? "" : (r.Outcome == FileOutcome.Failed ? "  ERROR: " : "  NOTE: ") + r.Error)}");

        var lines = results.Sum(r => (long)r.Lines);
        var events = results.Sum(r => (long)r.Events);
        Console.WriteLine();
        Console.WriteLine($"Imported {events:N0} events from {lines:N0} lines in {sw.Elapsed.TotalSeconds:F1}s " +
                          $"({(sw.Elapsed.TotalSeconds > 0 ? lines / sw.Elapsed.TotalSeconds : 0):N0} lines/s).");
        Console.WriteLine();
        foreach (var srv in job.Servers) Console.WriteLine($"{srv.Server,-16} {ImportJob.SummaryLine(srv)}");
        if (job.LogPath is not null) Console.WriteLine($"Import log: {job.LogPath}");
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

    /// <summary>Local time by default (as typed in the app with local display), UTC with --utc.</summary>
    internal static DateTime? ParseTime(string? text, bool utc)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd"];
        var style = utc ? DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal : DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal;
        if (!DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, style, out var t) &&
            !DateTime.TryParse(text, CultureInfo.CurrentCulture, style, out t))
            throw new FormatException($"Cannot read the time '{text}'. Use \"yyyy-MM-dd HH:mm[:ss]\".");
        return DateTime.SpecifyKind(t, DateTimeKind.Utc);
    }

    private static int RunFilteredStats(string casePath, bool includeHidden, DateTime? fromUtc, DateTime? toUtc, string? pattern)
    {
        using var store = CaseStore.Open(casePath);
        var q = new Core.Query.CaseQuery(store);
        var filter = new Core.Query.EventFilter { FromUtc = fromUtc, ToUtc = toUtc, ShowHidden = includeHidden, FileNamePattern = pattern };
        var span = q.Extent(new Core.Query.EventFilter { ShowHidden = true });
        Console.WriteLine($"Case span (UTC): {(span is { } sp ? $"{sp.Min:yyyy-MM-dd HH:mm:ss.fff} - {sp.Max:yyyy-MM-dd HH:mm:ss.fff}" : "no events")}");
        Console.WriteLine($"Filter: from {fromUtc?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "start"} UTC (incl.)" +
                          $" to {toUtc?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "end"} UTC (excl.)" +
                          $", file name {(string.IsNullOrWhiteSpace(pattern) ? "any" : $"'{Core.Query.FileNamePattern.Parse(pattern)}'")}" +
                          $", {(includeHidden ? "all categories" : "visible categories")}");
        Console.WriteLine($"Events in view: {q.CountEvents(filter):N0}");

        var rows = q.Matrix(filter, Core.Query.Dimensions.Category, Core.Query.Dimensions.Server);
        var servers = rows.Select(r => r.Column).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"\n  {"Category",-46}" + string.Concat(servers.Select(x => $"{x,14}")));
        foreach (var g in rows.GroupBy(r => r.Row))
            Console.WriteLine($"  {g.Key,-46}" + string.Concat(servers.Select(x => $"{g.Where(r => r.Column == x).Sum(r => r.Count),14:N0}")));
        Console.WriteLine($"  {"Total",-46}" + string.Concat(servers.Select(x => $"{rows.Where(r => r.Column == x).Sum(r => r.Count),14:N0}")));
        return 0;
    }

    private static Core.Query.EventFilter Filter(bool all, DateTime? fromUtc, DateTime? toUtc, string? pattern) =>
        new() { FromUtc = fromUtc, ToUtc = toUtc, ShowHidden = all, FileNamePattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern };

    private static TimeSpan AutoBucket(Core.Query.CaseQuery q, Core.Query.EventFilter f) =>
        Core.Query.CaseQuery.AutoBucket(TimeSpan.FromMinutes(q.RangeMinutes(f)));

    private static string T(DateTime? utc, bool asUtc) => utc is not { } t ? "-"
        : (asUtc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime()).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Trim(string s, int width) => s.Length <= width ? s : s[..(width - 3)] + "...";

    private static int RunTop(string casePath, Core.Query.EventFilter filter, string by, int n)
    {
        using var store = CaseStore.Open(casePath);
        var q = new Core.Query.CaseQuery(store);
        var list = Core.Query.CaseQuery.GetTopNList(by);
        var bucket = AutoBucket(q, filter);
        var result = q.TopN(filter, list, n, bucket);
        var minutes = q.RangeMinutes(filter);
        Console.WriteLine($"{list.Label}: top {n} of {result.Total:N0} events with a value (peak window {bucket}, times UTC)");
        Console.WriteLine($"{"#",3}  {list.Dimension.Label,-40} {"Events",9} {"Share",6} {"Files",6} {"Srv",3} {"Per min",8} {"Peak",6}  {"Peak at",-23} {"First",-23} Last");
        foreach (var r in result.Rows)
        {
            var g = r.Group;
            Console.WriteLine($"{r.Rank,3}  {Trim(g.Value, 40),-40} {g.Count,9:N0} {r.Share,6:P1} {r.Files,6:N0} {r.Servers,3} {g.Count / minutes,8:N1} {g.PeakCount,6:N0}  " +
                              $"{T(g.PeakAt, true),-23} {T(g.First, true),-23} {T(g.Last, true)}");
        }
        return 0;
    }

    private static int RunTrace(string casePath, string? uid, string? name, bool all, DateTime? fromUtc, DateTime? toUtc, bool asUtc)
    {
        if (uid is null && name is null) return Fail("trace needs --uid or --name.");
        using var store = CaseStore.Open(casePath);
        var q = new Core.Query.CaseQuery(store);
        if (uid is null)
        {
            var matches = q.FindUids(name!, fromUtc, toUtc);
            if (matches.Count == 0)
            {
                Console.WriteLine($"No file (UID) with a name matching '{name}'.");
                return 1;
            }
            if (matches.Count > 1)
            {
                Console.WriteLine($"{matches.Count} files (UIDs) match '{name}'. Trace one with --uid:");
                Console.WriteLine($"  {"UID",-48} {"Created on",-14} {"Ver",3} {"Srv",3} {"Events",7}  {"First",-23} {"Last",-23} Names");
                foreach (var m in matches)
                    Console.WriteLine($"  {m.Uid,-48} {m.CreatedOn ?? "?",-14} {m.Versions,3} {m.Servers,3} {m.Events,7:N0}  {T(m.First, asUtc),-23} {T(m.Last, asUtc),-23} {m.Names}");
                return 0;
            }
            uid = matches[0].Uid;
        }
        if (!Core.Query.CaseQuery.TryParseUid(uid, out var db, out var ver)) return Fail($"'{uid}' is not a UID ({{GUID}}-vN).");
        var trace = q.Trace(db, ver, fromUtc, toUtc);
        PrintTrace(trace, all, asUtc);
        return trace.Events.Count == 0 ? 1 : 0;
    }

    private static void PrintTrace(Core.Query.FileTraceResult trace, bool all, bool asUtc)
    {
        static string Since(DateTime? t, DateTime b) => t is { } x ? $"+{(x - b).TotalSeconds:0.000}s" : "-";
        var zone = asUtc ? "UTC" : "local";
        Console.WriteLine($"UID {trace.Uid}   names: {string.Join(", ", trace.Names)}   created on {trace.CreatedOn ?? "unknown"}   times {zone}");
        Console.WriteLine($"{trace.Events.Count:N0} event(s){(trace.Truncated ? $" (first {Core.Query.CaseQuery.MaxTraceEvents:N0} only)" : "")}, {trace.Versions.Count} version(s)");
        foreach (var v in trace.Versions)
        {
            Console.WriteLine();
            Console.WriteLine($"Version {v.Gvsn}  origin {v.Origin ?? "unknown"}  {v.ChangeKind ?? "change not in the logs"} at {T(v.BaseTime, asUtc)}" +
                              $"{(v.ChangedAt is null ? " (first seen)" : "")}  offered {Since(v.OfferedAt, v.BaseTime)}");
            foreach (var d in v.Deliveries)
                Console.WriteLine($"    {d.Server,-16} received {Since(d.ReceivedAt, v.BaseTime),-10} transferred {Since(d.TransferredAt, v.BaseTime),-10} {d.TransferType ?? "",-6}" +
                                  $" installed {Since(d.InstalledAt, v.BaseTime),-10} {d.Outcome ?? "(not installed in these logs)"}");
        }
        Console.WriteLine();
        var bases = trace.Versions.ToDictionary(v => v.Gvsn, v => v.BaseTime);
        Console.WriteLine($"{"Time (" + zone + ")",-23} {"Since",10}  {"Server",-14} {"Stage",-14} {"Category",-34} Class::Method  Message");
        foreach (var e in trace.Events.Where(e => all || !e.Hidden))
            Console.WriteLine($"{T(e.TsUtc, asUtc),-23} {(e.Gvsn is not null && bases.TryGetValue(e.Gvsn, out var b) ? Since(e.TsUtc, b) : ""),10}  {Trim(e.Server, 14),-14} {Trim(e.Stage, 14),-14} " +
                              $"{Trim(e.Category, 34),-34} {e.ClassMethod}{(e.ViaFid ? " [FID]" : "")}  {Trim(e.Message, 90)}");
    }

    private static int RunExport(string casePath, string? what, string? outPath, Core.Query.EventFilter filter, string? levels, string by, int n,
        string? uid, bool asUtc)
    {
        if (what is null || outPath is null) return Fail("export needs --what and --out.");
        using var store = CaseStore.Open(casePath);
        var q = new Core.Query.CaseQuery(store);
        var local = asUtc ? null : TimeZoneInfo.Local;
        long rows;
        using (var csv = Core.Query.CsvWriter.Create(outPath))
        {
            switch (what)
            {
                case "events":
                    rows = q.ExportEvents(filter, null, csv, local);
                    break;
                case "grid":
                    var dims = (levels ?? "category,server,file").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(Core.Query.Dimensions.Get).ToList();
                    rows = q.ExportGrid(filter, dims, csv, local);
                    break;
                case "topn":
                    var result = q.TopN(filter, Core.Query.CaseQuery.GetTopNList(by), n, AutoBucket(q, filter));
                    Core.Query.CaseQuery.ExportTopN(result, q.RangeMinutes(filter), csv, local);
                    rows = result.Rows.Count;
                    break;
                case "trace":
                    if (!Core.Query.CaseQuery.TryParseUid(uid, out var db, out var ver)) return Fail("export --what trace needs --uid {GUID}-vN.");
                    var trace = q.Trace(db, ver, filter.FromUtc, filter.ToUtc);
                    Core.Query.CaseQuery.ExportTrace(trace, csv, local, filter.ShowHidden);
                    rows = trace.Events.Count(e => filter.ShowHidden || !e.Hidden);
                    break;
                default:
                    return Fail($"Unknown --what '{what}' (events, grid, topn, trace).");
            }
        }
        Console.WriteLine($"Wrote {rows:N0} row(s) to {Path.GetFullPath(outPath)}");
        return 0;
    }

    private static int RunVerify(string casePath)
    {
        using var store = CaseStore.Open(casePath);
        var findings = CaseIntegrity.Verify(store);
        foreach (var f in findings)
            Console.WriteLine($"{f.Level switch { FindingLevel.Defect => "DEFECT ", FindingLevel.Warning => "WARNING", _ => "INFO   " }} {f.Message}");
        var defects = findings.Count(f => f.Level == FindingLevel.Defect);
        Console.WriteLine(defects == 0 ? "Result: OK" : $"Result: {defects} defect(s)");
        return defects == 0 ? 0 : 1;
    }

    private static int RunCompare(string caseA, string caseB)
    {
        // Opening B once applies any one-time repairs (e.g. file names fixed in 6b) before it is attached read-only.
        using (CaseStore.Open(caseB)) { }
        using var store = CaseStore.Open(caseA);
        var rows = CaseIntegrity.Compare(store, caseB);
        Console.WriteLine($"A = {Path.GetFullPath(caseA)}\nB = {Path.GetFullPath(caseB)}\n");
        Console.WriteLine($"{"Server",-16} {"Idx",4} {"Events A",10} {"Events B",10} {"Compared",10}  {"Result",-11} File / note");
        foreach (var r in rows)
            Console.WriteLine($"{r.Server,-16} {r.Index,4} {r.EventsA?.ToString("N0", CultureInfo.CurrentCulture) ?? "-",10} {r.EventsB?.ToString("N0", CultureInfo.CurrentCulture) ?? "-",10} " +
                              $"{(r.Match is FileMatch.OnlyInA or FileMatch.OnlyInB ? "-" : r.CommonEvents.ToString("N0", CultureInfo.CurrentCulture)),10}  {r.Match,-11} " +
                              $"{r.FileA ?? r.FileB}{(r.Note is null ? "" : "  (" + r.Note + ")")}");
        var shared = rows.Where(r => r.Match is not (FileMatch.OnlyInA or FileMatch.OnlyInB)).ToList();
        var differ = shared.Count(r => r.Match == FileMatch.Differs);
        Console.WriteLine($"\n{shared.Count} shared file(s): {shared.Count(r => r.Match == FileMatch.Identical)} identical, " +
                          $"{shared.Count(r => r.Match == FileMatch.SamePrefix)} identical over the common lines, {differ} different; " +
                          $"{rows.Count(r => r.Match == FileMatch.OnlyInA)} only in A, {rows.Count(r => r.Match == FileMatch.OnlyInB)} only in B.");
        Console.WriteLine($"Events in shared files, over the common lines: {shared.Sum(r => r.CommonEvents):N0}");
        Console.WriteLine(differ == 0 ? "Result: OK" : "Result: DIFFERENT");
        return differ == 0 ? 0 : 1;
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
