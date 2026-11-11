using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Core.Collection;

public sealed record CollectionRequest(
    IReadOnlyList<ServerTarget> Servers,
    DateTime? FromUtc,
    DateTime? ToUtc,
    bool IncludeEventLog = false,
    bool DryRun = false,
    NetworkCredential? Credential = null);

public enum ServerStatus { Ok, Partial, Failed, Skipped }

public sealed class ServerReport(ServerTarget server)
{
    public ServerTarget Server { get; } = server;
    public ServerStatus Status { get; set; } = ServerStatus.Ok;
    public string? Error { get; set; }
    public List<PlannedFile> Plan { get; set; } = [];
    public int FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public long EventsImported { get; set; }
    public int EventLogRecords { get; set; }
    public TimeSpan Elapsed { get; set; }

    public int FilesToCopy => Plan.Count(p => p.Decision == FileDecision.Copy);

    public string Summary => Status == ServerStatus.Failed
        ? $"FAILED: {Error}"
        : $"{Plan.Count} files listed, {FilesToCopy} selected, {Plan.Count(p => p.Decision == FileDecision.AlreadyImported)} already in case, " +
          $"{Plan.Count(p => p.Decision is FileDecision.BeforeRange or FileDecision.AfterRange)} outside range, " +
          (Plan.Any(p => p.Decision == FileDecision.Unreadable) ? $"{Plan.Count(p => p.Decision == FileDecision.Unreadable)} unreadable, " : "") +
          $"{FilesCopied} copied ({BytesCopied / 1048576.0:N1} MB), {EventsImported:N0} events" +
          (EventLogRecords > 0 ? $", {EventLogRecords:N0} event-log records" : "") + (Error is null ? "" : $"; {Error}");
}

public sealed record CollectionReport(IReadOnlyList<ServerReport> Servers, string? LogPath, TimeSpan Elapsed, bool DryRun);

public sealed record ConnectionTest(ServerTarget Server, bool ShareOk, string ShareDetail, bool? EventLogOk, string? EventLogDetail)
{
    public bool Ok => ShareOk && EventLogOk != false;
}

/// <summary>
/// Collects DFSR debug logs (and optionally the DFS Replication event log) from servers into a case.
/// Servers are processed in parallel for listing, probing, copying and event-log reading; the case
/// is written afterwards on one thread. A failing server is reported and does not stop the others.
/// </summary>
public sealed class Collector(CaseStore store, IShareConnector connector, IEventLogSource? eventLog, CollectionLog log)
{
    public int MaxParallelServers { get; init; } = 4;

    // ------------------------------------------------------------------ test connection

    public List<ConnectionTest> Test(IReadOnlyList<ServerTarget> servers, NetworkCredential? credential, bool includeEventLog, CancellationToken ct = default)
    {
        log.Info($"Testing {servers.Count} server(s){(credential is null ? " as the current user" : $" as {Who(credential)}")}.");
        var results = new ConcurrentBag<ConnectionTest>();
        Parallel.ForEach(servers, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelServers, CancellationToken = ct }, s =>
        {
            bool shareOk;
            string shareDetail;
            bool? elOk = null;
            string? elDetail = null;
            try
            {
                using var _ = connector.Connect(s.Host, credential);
                var files = CollectionPlanner.ListLogs(s.EffectiveLogFolder);
                shareOk = true;
                var newest = files.MaxBy(f => f.LastWriteUtc);
                shareDetail = $"{s.EffectiveLogFolder}: {files.Count} log file(s)" +
                              (newest is null ? "" : $", newest {newest.Name} written {newest.LastWriteUtc:yyyy-MM-dd HH:mm:ss} UTC");
                log.Info($"Share OK. {shareDetail}", s.Name);

                if (includeEventLog && eventLog is not null)
                {
                    try
                    {
                        var n = eventLog.ReadRemote(s.Host, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, credential, ct).Take(1000).Count();
                        elOk = true;
                        elDetail = $"DFS Replication log readable, {n}{(n == 1000 ? "+" : "")} record(s) in the last 24 h";
                        log.Info($"Event log OK. {elDetail}", s.Name);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        elOk = false;
                        elDetail = CollectionLog.Describe(ex);
                        log.Error("Event log test failed", s.Name, ex);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                shareOk = false;
                shareDetail = $"{s.EffectiveLogFolder}: {CollectionLog.Describe(ex)}";
                log.Error($"Share test failed for {s.EffectiveLogFolder}", s.Name, ex);
            }
            results.Add(new ConnectionTest(s, shareOk, shareDetail, elOk, elDetail));
        });
        return [.. results.OrderBy(r => r.Server.Name, StringComparer.OrdinalIgnoreCase)];
    }

    // ------------------------------------------------------------------ collect

    public CollectionReport Collect(CollectionRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        log.Info($"{(req.DryRun ? "Dry run" : "Collection")} from {req.Servers.Count} server(s); range " +
                 $"{req.FromUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "start"} .. {req.ToUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "now"} UTC; " +
                 $"event log {(req.IncludeEventLog ? "yes" : "no")}; {(req.Credential is null ? "current user" : Who(req.Credential))}.");

        // Snapshot of complete files per server, so parallel planning never touches the database.
        var complete = CompleteFiles();
        var tempRoot = Path.Combine(store.TempFolder, "collect-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        var reports = req.Servers.Select(s => new ServerReport(s)).ToList();
        var copied = new ConcurrentDictionary<ServerReport, List<(string Local, string Remote)>>();
        var eventRecords = new ConcurrentDictionary<ServerReport, List<EventLogRecordData>>();

        try
        {
            // ---- per server, in parallel: connect, list, plan, copy, read event log ----
            Parallel.ForEach(reports, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelServers, CancellationToken = ct }, rep =>
            {
                var s = rep.Server;
                var serverSw = Stopwatch.StartNew();
                try
                {
                    progress?.Report($"{s.Name}: connecting…");
                    using var _ = connector.Connect(s.Host, req.Credential);

                    progress?.Report($"{s.Name}: listing {s.EffectiveLogFolder}…");
                    var files = CollectionPlanner.ListLogs(s.EffectiveLogFolder);
                    log.Info($"Listed {files.Count} file(s) in {s.EffectiveLogFolder}.", s.Name);

                    var serverComplete = complete.GetValueOrDefault(s.Name.ToUpperInvariant()) ?? [];
                    rep.Plan = CollectionPlanner.Plan(files, req.FromUtc, req.ToUtc, (idx, hash) => serverComplete.Contains((idx, hash)));
                    foreach (var p in rep.Plan)
                        log.Info($"{p.File.Name,-18} {p.File.Size,12:N0} B  idx {p.Index?.ToString() ?? "?",4}  " +
                                 $"{p.FirstEventUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",19} .. {p.EndUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-",19}  " +
                                 $"{p.Decision}{(p.Note is null ? "" : " (" + p.Note + ")")}", s.Name);
                    if (rep.Plan.Any(p => p.Decision == FileDecision.Unreadable)) rep.Status = ServerStatus.Partial;

                    if (!req.DryRun)
                    {
                        var dest = Path.Combine(tempRoot, Sanitise(s.Name));
                        Directory.CreateDirectory(dest);
                        var list = new List<(string, string)>();
                        foreach (var p in rep.Plan.Where(p => p.Decision == FileDecision.Copy))
                        {
                            ct.ThrowIfCancellationRequested();
                            progress?.Report($"{s.Name}: copying {p.File.Name} ({p.File.Size / 1048576.0:N1} MB)…");
                            var local = Path.Combine(dest, p.File.Name);
                            CopyFile(p.File.Path, local, ct);
                            rep.FilesCopied++;
                            rep.BytesCopied += new FileInfo(local).Length;
                            list.Add((local, p.File.Path));
                        }
                        copied[rep] = list;
                        log.Info($"Copied {rep.FilesCopied} file(s), {rep.BytesCopied:N0} bytes.", s.Name);
                    }

                    if (req.IncludeEventLog && eventLog is not null && !req.DryRun)
                    {
                        progress?.Report($"{s.Name}: reading the DFS Replication event log…");
                        try
                        {
                            var from = req.FromUtc ?? DateTime.UtcNow.AddDays(-30);
                            var to = req.ToUtc ?? DateTime.UtcNow.AddMinutes(1);
                            var records = eventLog.ReadRemote(s.Host, from, to, req.Credential, ct).ToList();
                            eventRecords[rep] = records;
                            log.Info($"Read {records.Count} event-log record(s).", s.Name);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            rep.Status = ServerStatus.Partial;
                            rep.Error = "event log: " + ex.Message;
                            log.Error("Reading the event log failed", s.Name, ex);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    rep.Status = ServerStatus.Failed;
                    rep.Error = ex.Message;
                    log.Error("Collection failed", s.Name, ex);
                }
                rep.Elapsed = serverSw.Elapsed;
            });

            // ---- write the case, one server at a time ----
            foreach (var rep in reports.Where(r => copied.ContainsKey(r)))
            {
                var files = copied[rep];
                if (files.Count == 0) continue;
                progress?.Report($"{rep.Server.Name}: importing {files.Count} file(s)…");
                var remoteOf = files.ToDictionary(f => Path.GetFullPath(f.Local), f => f.Remote, StringComparer.OrdinalIgnoreCase);
                var importer = new Importer(store) { SourcePathResolver = local => remoteOf.GetValueOrDefault(local, local) };
                var results = importer.Import(files.Select(f => f.Local), rep.Server.Name, null, ct);
                rep.EventsImported = results.Sum(r => (long)r.Events);
                foreach (var r in results)
                    log.Info($"Imported {Path.GetFileName(r.Path)}: {r.Outcome}, {r.Events:N0} events{(r.Error is null ? "" : ", " + r.Error)}", rep.Server.Name);
                if (results.Any(r => r.Outcome == FileOutcome.Failed)) rep.Status = ServerStatus.Partial;
            }
            foreach (var (rep, records) in eventRecords)
            {
                progress?.Report($"{rep.Server.Name}: storing {records.Count} event-log record(s)…");
                rep.EventLogRecords = store.ImportEventLog(rep.Server.Name, records);
            }
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); }
            catch (IOException ex) { log.Warn($"Could not delete temporary folder {tempRoot}: {ex.Message}"); }
        }

        foreach (var r in reports) log.Info($"Summary: {r.Summary} in {r.Elapsed.TotalSeconds:N1} s.", r.Server.Name);
        log.Info($"Finished in {sw.Elapsed.TotalSeconds:N1} s.");
        return new CollectionReport(reports, log.Path, sw.Elapsed, req.DryRun);
    }

    private Dictionary<string, HashSet<(int, long)>> CompleteFiles()
    {
        var map = new Dictionary<string, HashSet<(int, long)>>();
        foreach (var row in store.Query("SELECT upper(s.name), f.idx, f.first_line_hash FROM log_file f JOIN server s USING (server_id) WHERE f.is_complete AND f.idx >= 0").Skip(1))
        {
            var key = (string)row[0]!;
            if (!map.TryGetValue(key, out var set)) map[key] = set = [];
            set.Add((Convert.ToInt32(row[1]), Convert.ToInt64(row[2])));
        }
        return map;
    }

    private static void CopyFile(string source, string dest, CancellationToken ct)
    {
        // The active log is open for writing by DFSR: share read/write so the copy is allowed.
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = src.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            dst.Write(buffer, 0, n);
        }
    }

    private static string Sanitise(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string Who(NetworkCredential c) => string.IsNullOrEmpty(c.Domain) ? c.UserName : $@"{c.Domain}\{c.UserName}";
}
