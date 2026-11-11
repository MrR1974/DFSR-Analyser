using System.Diagnostics;
using DfsrAnalyser.Core.Parsing;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Core.Import;

public enum FileOutcome { Imported, Resumed, Unchanged, Failed }

public sealed record FileImportResult(
    string Path, string Server, int Index, int Sequence, FileOutcome Outcome,
    int Events, int Lines, TimeSpan Elapsed, string? Error = null, TimeSpan ParseTime = default, TimeSpan CommitTime = default, TimeSpan LoadTime = default);

public sealed record ImportProgress(string Path, int FileNumber, int FileCount, long EventsSoFar);

/// <summary>
/// Imports DFSR debug log files into a case in three stages:
/// <list type="number">
/// <item><b>Plan</b> (in order, cheap): read each header, match it against files already in the
/// case, register new files, and trim a partially imported active log back to its last event.</item>
/// <item><b>Parse</b> (parallel): parse, extract fields and write each file to CSV.</item>
/// <item><b>Commit</b> (in order, per server by Index): COPY into DuckDB, stitch orphan '+' lines
/// across rolls, and record per-file statistics.</item>
/// </list>
/// Re-importing is safe: complete files are skipped, an active log resumes from its last event,
/// and a log that has since been gzipped is recognised by its fingerprint.
/// </summary>
public sealed class Importer(CaseStore store)
{
    /// <summary>Upper bound on files parsed ahead of the commit stage (bounds temp disk use).</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    /// <summary>Finds Dfsr*.log and Dfsr*.log.gz under the given files or folders (recursively).</summary>
    public static List<string> FindLogFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) { files.Add(Path.GetFullPath(p)); continue; }
            if (!Directory.Exists(p)) throw new FileNotFoundException($"Not found: {p}");
            files.AddRange(Directory.EnumerateFiles(p, "Dfsr*.log", SearchOption.AllDirectories));
            files.AddRange(Directory.EnumerateFiles(p, "Dfsr*.log.gz", SearchOption.AllDirectories));
        }
        return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <param name="serverName">Server to file the logs under; when null, each file's header Computer name is used.</param>
    public List<FileImportResult> Import(IEnumerable<string> files, string? serverName = null,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<FileImportResult>();

        // ---- probe ----
        var probes = new List<Probe>();
        foreach (var path in files)
        {
            try { probes.Add(Probe.Read(path, serverName)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                results.Add(new FileImportResult(path, serverName ?? "?", 0, 0, FileOutcome.Failed, 0, 0, TimeSpan.Zero, ex.Message));
            }
        }
        probes = probes
            .OrderBy(p => p.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Header.Index)
            .ToList();

        // ---- plan ----
        var plans = new List<FilePlan>();
        foreach (var probe in probes)
        {
            var higherInBatch = probes.Any(o => o.Server.Equals(probe.Server, StringComparison.OrdinalIgnoreCase) && o.Header.Index > probe.Header.Index);
            var plan = Plan(probe, higherInBatch);
            if (plan.Outcome == FileOutcome.Unchanged)
                results.Add(new FileImportResult(probe.Path, probe.Server, probe.Header.Index, probe.Header.Sequence,
                    FileOutcome.Unchanged, 0, plan.Record.LineCount ?? 0, TimeSpan.Zero));
            else
                plans.Add(plan);
        }

        // ---- parse (parallel, at most MaxParallelism files ahead) + commit (in order) ----
        // Tasks are launched in plan order from this loop, so the file being committed is
        // always already running and temp CSVs never pile up.
        var tasks = new Task<ParsedFile>?[plans.Count];
        var launched = 0;
        void LaunchUpTo(int limit)
        {
            for (; launched < plans.Count && launched < limit; launched++)
            {
                var p = plans[launched];
                tasks[launched] = Task.Run(() => Parse(p, store.TempFolder, ct), ct);
            }
        }

        long events = 0;
        var committed = 0;
        try
        {
            LaunchUpTo(MaxParallelism);
            for (var i = 0; i < plans.Count; i++)
            {
                progress?.Report(new ImportProgress(plans[i].Probe.Path, i + 1, plans.Count, events));
                var parsed = tasks[i]!.GetAwaiter().GetResult();
                LaunchUpTo(i + 1 + MaxParallelism);
                var result = Commit(parsed);
                committed = i + 1;
                events += result.Events;
                results.Add(result);
            }
        }
        finally
        {
            // On cancellation or failure, let in-flight parses finish and remove their CSVs;
            // drop the registrations of new files that never got committed.
            for (var i = committed; i < plans.Count; i++)
            {
                if (tasks[i] is { } t)
                {
                    try { t.Wait(CancellationToken.None); } catch (AggregateException) { }
                    if (t.IsCompletedSuccessfully) t.Result.Csv?.DeleteFiles();
                }
                if (plans[i].Outcome == FileOutcome.Imported) store.DeleteFile(plans[i].Record.FileId);
            }
        }
        return results;
    }

    // ---------------------------------------------------------------- plan

    private sealed record FilePlan(Probe Probe, int ServerId, LogFileRecord Record, FileOutcome Outcome, int FromLine, bool Complete);

    private FilePlan Plan(Probe probe, bool higherIndexInBatch)
    {
        var h = probe.Header;
        var serverId = store.GetOrAddServer(probe.Server);
        store.AddAlias(serverId, h.Computer);

        var existing = store.FindFile(serverId, h.Index, probe.FirstLineHash);
        if (existing is { IsComplete: true })
            return new FilePlan(probe, serverId, existing, FileOutcome.Unchanged, 0, true);

        var isGz = probe.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        var complete = isGz || higherIndexInBatch || store.HasHigherIndex(serverId, h.Index);

        if (existing is not null)
        {
            var fromLine = existing.LastEventLine ?? 0;
            store.DeleteEventsFrom(existing.FileId, fromLine);
            return new FilePlan(probe, serverId, existing, FileOutcome.Resumed, fromLine, complete);
        }

        var rec = new LogFileRecord { ServerId = serverId, Index = h.Index, FirstLineHash = probe.FirstLineHash, FileName = Path.GetFileName(probe.Path) };
        store.AddFile(rec);
        return new FilePlan(probe, serverId, rec, FileOutcome.Imported, 0, complete);
    }

    // ---------------------------------------------------------------- parse

    private sealed class ParsedFile
    {
        public required FilePlan Plan { get; init; }
        public CsvBulkWriter? Csv { get; set; }
        public int Events { get; set; }
        public DateTime? FirstTs { get; set; }
        public DateTime? LastTs { get; set; }
        public int? LastLine { get; set; }
        public int Lines { get; set; }
        public int Unrecognised { get; set; }
        public List<string> LeadingOrphans { get; set; } = [];
        public string? TzBasis { get; set; }
        public TimeSpan Elapsed { get; set; }
        public Exception? Error { get; set; }
    }

    private static ParsedFile Parse(FilePlan plan, string tempFolder, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var parsed = new ParsedFile { Plan = plan };
        try
        {
            var converter = new LocalTimeConverter(plan.Probe.Header);
            parsed.TzBasis = converter.ConversionBasis.ToString();
            var fileId = plan.Record.FileId;
            var serverId = plan.ServerId;

            using var reader = new LogFileReader(plan.Probe.Path);
            reader.ReadPreamble();
            parsed.LeadingOrphans = [.. reader.LeadingOrphans];

            parsed.Csv = new CsvBulkWriter(tempFolder);
            using (parsed.Csv)
            {
                var n = 0;
                foreach (var ev in reader.ReadEvents(plan.FromLine))
                {
                    if ((++n & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                    var ts = converter.ToUtc(ev.LocalTime);
                    parsed.Csv.Write(CaseStore.EventId(fileId, ev.LineNo), fileId, serverId, ev, ts, FieldExtractor.Extract(ev));
                    parsed.FirstTs ??= ts;
                    parsed.LastTs = ts;
                    parsed.LastLine = ev.LineNo;
                    parsed.Events++;
                }
            }
            parsed.Lines = reader.LinesRead;
            parsed.Unrecognised = reader.UnrecognisedLines;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            parsed.Error = ex;
        }
        parsed.Elapsed = sw.Elapsed;
        return parsed;
    }

    // ---------------------------------------------------------------- commit

    private FileImportResult Commit(ParsedFile parsed)
    {
        var sw = Stopwatch.StartNew();
        var plan = parsed.Plan;
        var probe = plan.Probe;
        var h = probe.Header;
        var rec = plan.Record;

        if (parsed.Error is not null)
        {
            parsed.Csv?.DeleteFiles();
            if (plan.Outcome == FileOutcome.Imported) store.DeleteFile(rec.FileId);
            return new FileImportResult(probe.Path, probe.Server, h.Index, h.Sequence, FileOutcome.Failed, 0, 0, parsed.Elapsed, parsed.Error.Message);
        }

        var loadSw = Stopwatch.StartNew();
        try { store.LoadCsv(parsed.Csv!); }
        finally { parsed.Csv!.DeleteFiles(); }
        var loadTime = loadSw.Elapsed;

        long? lastId = parsed.LastLine is { } ll ? CaseStore.EventId(rec.FileId, ll) : rec.LastEventId;

        // Orphans at the start of a rolled file (Sequence > 1) belong to the last event of Index-1.
        // A file started by a service start (Sequence 1) has nothing to continue; its orphans are only counted.
        if (plan.Outcome == FileOutcome.Imported && parsed.LeadingOrphans.Count > 0 && h.Sequence > 1)
        {
            if (store.FindLatestByIndex(plan.ServerId, h.Index - 1)?.LastEventId is { } prevLast) AppendNested(prevLast, parsed.LeadingOrphans);
            else store.AddPendingOrphan(plan.ServerId, h.Index, parsed.LeadingOrphans);
        }

        // Orphans that arrived earlier and were waiting for this file's last event.
        if (lastId is { } myLast && store.TakePendingOrphan(plan.ServerId, h.Index + 1) is { } waiting)
            AppendNested(myLast, waiting);

        rec.SourcePath = probe.Path;
        rec.FileName = Path.GetFileName(probe.Path);
        rec.SizeBytes = new FileInfo(probe.Path).Length;
        rec.HeaderComputer = h.Computer;
        rec.TzId = h.TzId;
        rec.TzName = h.TimeZoneName;
        rec.TzBasis = parsed.TzBasis;
        rec.LogLevel = h.LogLevel;
        rec.MaxEntryCount = h.MaxEntryCount;
        rec.MaxFileCount = h.MaxFileCount;
        rec.Sequence = h.Sequence;
        rec.FirstTsUtc ??= parsed.FirstTs;
        rec.LastTsUtc = parsed.LastTs ?? rec.LastTsUtc;
        rec.LineCount = parsed.Lines;
        rec.EventCount = store.CountEvents(rec.FileId);
        if (plan.Outcome == FileOutcome.Imported) rec.OrphanLines = parsed.LeadingOrphans.Count;
        rec.Unrecognised = parsed.Unrecognised;
        rec.LastEventLine = parsed.LastLine ?? rec.LastEventLine;
        rec.LastEventId = lastId;
        rec.IsComplete = plan.Complete;
        rec.ImportedAt = DateTime.UtcNow;
        store.UpdateFile(rec);

        // A later Index proves every earlier file of this server has stopped growing.
        store.MarkEarlierComplete(plan.ServerId, h.Index);

        return new FileImportResult(probe.Path, probe.Server, h.Index, h.Sequence, plan.Outcome,
            parsed.Events, parsed.Lines, parsed.Elapsed + sw.Elapsed, null, parsed.Elapsed, sw.Elapsed, loadTime);
    }

    /// <summary>Appends nested lines to a stored event and re-extracts its fields (same event id).</summary>
    private void AppendNested(long eventId, List<string> lines)
    {
        var stored = store.LoadEvent(eventId) ?? throw new InvalidOperationException($"Event {eventId} not found.");
        stored.Raw.Nested.AddRange(lines);
        store.DeleteEvent(eventId);
        using var writer = store.OpenSmallWriter();
        writer.Write(eventId, stored.FileId, stored.ServerId, stored.Raw, stored.TsUtc, FieldExtractor.Extract(stored.Raw));
    }

    // ---------------------------------------------------------------- probe

    /// <summary>Header and first dated line of a file, read without parsing the body.</summary>
    private sealed record Probe(string Path, string Server, LogHeader Header, long FirstLineHash)
    {
        public static Probe Read(string path, string? serverName)
        {
            using var reader = new LogFileReader(path);
            reader.ReadPreamble();
            var h = reader.Header;
            if (h.Index == 0 && string.IsNullOrEmpty(h.Computer))
                throw new InvalidDataException("No DFSR log header found.");
            var server = !string.IsNullOrWhiteSpace(serverName) ? serverName : h.Computer;
            return new Probe(path, server, h, Fnv1a64(reader.FirstDatedLine ?? ""));
        }
    }

    internal static long Fnv1a64(string s)
    {
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return (long)hash;
        }
    }
}
