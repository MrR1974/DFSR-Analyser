using System.Globalization;
using System.Net;
using System.Text;
using DfsrAnalyser.Core.Collection;
using DfsrAnalyser.Core.Query;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Tests;

/// <summary>
/// Collection against local folders standing in for \\server\admin$\debug: the planner, copy, import,
/// top-up skipping, per-server failure isolation and event-log storage all run through the real code;
/// only AD, WNet credentials and the remote event-log RPC are faked.
/// </summary>
public sealed class CollectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dfsra-col-" + Guid.NewGuid().ToString("N"));
    private string Share(string server) => Path.Combine(_root, "shares", server);
    private string CaseDir => Path.Combine(_root, "case");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // UTC hour h of 2026-11-11 is local h+11 (AUS Eastern, DST).
    private static DateTime Utc(int h, int m = 0) => new(2026, 11, 11, h, m, 0, DateTimeKind.Utc);

    public CollectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Writes a log whose events run every minute from <paramref name="startUtc"/>; last write time = last event.</summary>
    private string WriteLog(string server, int index, int sequence, DateTime startUtc, int minutes, bool gzip)
    {
        Directory.CreateDirectory(Share(server));
        var sb = new StringBuilder();
        sb.Append($"* DFSR Log Sequence:{sequence} Index:{index} Computer:{server} TimeZone:AUS Eastern Daylight Time (GMT+11:00) (TzId>AUS Eastern Standard Time<dIzT) Build:[x]\n");
        for (var i = 0; i < minutes; i++)
        {
            var local = startUtc.AddHours(11).AddMinutes(i).ToString("yyyyMMdd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            sb.Append($"{local} 4444 JOIN  1253 Join::SubmitUpdate Sent: uid:{{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}}-v{index * 100 + i} gvsn:{{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}}-v{index * 100 + i} name:f{i}.txt connId:{{DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5}}\n");
        }
        var path = Path.Combine(Share(server), $"Dfsr{index:00000}.log{(gzip ? ".gz" : "")}");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        if (gzip)
        {
            using var fs = File.Create(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
            gz.Write(bytes);
        }
        else File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, startUtc.AddMinutes(minutes - 1));
        return path;
    }

    /// <summary>Three hourly files on DC01 (01:00, 02:00, 03:00 UTC, 60 events each; the last still active).</summary>
    private void ThreeHours()
    {
        WriteLog("DC01", 1, 1, Utc(1), 60, gzip: true);
        WriteLog("DC01", 2, 2, Utc(2), 60, gzip: true);
        WriteLog("DC01", 3, 3, Utc(3), 60, gzip: false);
    }

    private Collector NewCollector(CaseStore store, CollectionLog log, IEventLogSource? el = null) =>
        new(store, new CurrentUserAccess(), el, log);

    [Fact]
    public void Planner_selects_files_overlapping_the_range()
    {
        ThreeHours();
        var plan = CollectionPlanner.Plan(CollectionPlanner.ListLogs(Share("DC01")), Utc(2, 30), Utc(2, 45));
        Assert.Equal(
            [("Dfsr00001.log.gz", FileDecision.BeforeRange), ("Dfsr00002.log.gz", FileDecision.Copy), ("Dfsr00003.log", FileDecision.AfterRange)],
            plan.Select(p => (p.File.Name, p.Decision)));
        // the first file was ruled out by its last-write time without being opened
        Assert.Null(plan[0].Index);
        Assert.Equal(Utc(2), plan[1].FirstEventUtc);
        Assert.Equal(Utc(3), plan[1].EndUtc);
    }

    [Fact]
    public void Planner_with_no_range_takes_everything_and_marks_unreadable_files()
    {
        ThreeHours();
        File.WriteAllText(Path.Combine(Share("DC01"), "Dfsr00009.log"), "not a dfsr log\n");
        var plan = CollectionPlanner.Plan(CollectionPlanner.ListLogs(Share("DC01")), null, null);
        Assert.Equal(3, plan.Count(p => p.Decision == FileDecision.Copy));
        Assert.Single(plan, p => p.Decision == FileDecision.Unreadable);
    }

    [Fact]
    public void Collect_imports_skips_on_top_up_and_isolates_a_failing_server()
    {
        ThreeHours();
        WriteLog("MEMBER01", 1, 1, Utc(2), 30, gzip: false);
        using var store = CaseStore.Open(CaseDir);
        using var log = new CollectionLog(Path.Combine(CaseDir, "logs"));
        var servers = new List<ServerTarget>
        {
            new("DC01", "dc01", Share("DC01")),
            new("MEMBER01", "member01", Share("MEMBER01")),
            new("GONE01", "gone01", Path.Combine(_root, "shares", "missing")),
        };

        var first = NewCollector(store, log).Collect(new CollectionRequest(servers, null, null), ct: Ct);
        var dc = first.Servers.Single(s => s.Server.Name == "DC01");
        Assert.Equal(ServerStatus.Ok, dc.Status);
        Assert.Equal(3, dc.FilesCopied);
        Assert.Equal(180, dc.EventsImported);
        Assert.Equal(30, first.Servers.Single(s => s.Server.Name == "MEMBER01").EventsImported);
        var gone = first.Servers.Single(s => s.Server.Name == "GONE01");
        Assert.Equal(ServerStatus.Failed, gone.Status);
        Assert.NotNull(gone.Error);
        Assert.Equal(210, store.Scalar<long>("SELECT count(*) FROM event"));
        Assert.Equal(Share("DC01"), Path.GetDirectoryName(store.Scalar<string>("SELECT source_path FROM log_file WHERE file_name = 'Dfsr00002.log.gz'")));
        Assert.False(Directory.EnumerateFiles(store.TempFolder, "*", SearchOption.AllDirectories).Any());

        // Top-up: the complete .gz files are skipped; the active log is copied again and resumed.
        var second = NewCollector(store, log).Collect(new CollectionRequest(servers[..1], null, null), ct: Ct);
        var dc2 = second.Servers.Single();
        Assert.Equal(2, dc2.Plan.Count(p => p.Decision == FileDecision.AlreadyImported));
        Assert.Equal(1, dc2.FilesCopied);
        Assert.Equal(210, store.Scalar<long>("SELECT count(*) FROM event"));

        Assert.True(File.Exists(log.Path));
        // read while the log is still open, as an editor would during a run
        using var fs = new FileStream(log.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var text = new StreamReader(fs).ReadToEnd();
        Assert.Contains("[GONE01]", text);
        Assert.Contains("AlreadyImported", text);
    }

    [Fact]
    public void Dry_run_plans_but_copies_and_imports_nothing()
    {
        ThreeHours();
        using var store = CaseStore.Open(CaseDir);
        using var log = new CollectionLog(null);
        var report = NewCollector(store, log).Collect(new CollectionRequest([new("DC01", "dc01", Share("DC01"))], Utc(1, 30), null, DryRun: true), ct: Ct);
        Assert.True(report.DryRun);
        Assert.Equal(3, report.Servers[0].FilesToCopy);
        Assert.Equal(0, report.Servers[0].FilesCopied);
        Assert.Equal(0, store.Scalar<long>("SELECT count(*) FROM event"));
    }

    [Fact]
    public void Connection_test_reports_each_server()
    {
        ThreeHours();
        using var store = CaseStore.Open(CaseDir);
        using var log = new CollectionLog(null);
        var tests = NewCollector(store, log, new FakeEventLog()).Test(
            [new("DC01", "dc01", Share("DC01")), new("GONE01", "gone01", Path.Combine(_root, "nope"))], null, includeEventLog: true, Ct);
        Assert.True(tests.Single(t => t.Server.Name == "DC01").Ok);
        Assert.Contains("3 log file(s)", tests.Single(t => t.Server.Name == "DC01").ShareDetail);
        Assert.False(tests.Single(t => t.Server.Name == "GONE01").Ok);
    }

    [Fact]
    public void Event_log_records_join_the_timeline_and_replace_on_recollect()
    {
        ThreeHours();
        using var store = CaseStore.Open(CaseDir);
        using var log = new CollectionLog(null);
        var el = new FakeEventLog();
        var req = new CollectionRequest([new("DC01", "dc01", Share("DC01"))], Utc(2), Utc(4), IncludeEventLog: true);
        var report = NewCollector(store, log, el).Collect(req, ct: Ct);
        Assert.Equal(2, report.Servers[0].EventLogRecords);

        var q = new CaseQuery(store);
        var all = new EventFilter();
        var sources = q.Facet(Dimensions.Source, all).ToDictionary(v => v.Value, v => v.Count);
        Assert.Equal(2, sources["Event log"]);
        Assert.Equal("Event log: warning", store.Scalar<string>("SELECT category FROM event WHERE event_log_id = 4202"));
        Assert.Equal("Event 4412: The DFS Replication service detected that a file was changed on multiple servers.",
            store.Scalar<string>("SELECT message FROM event WHERE event_log_id = 4412"));
        Assert.Equal(2, store.Scalar<long>("SELECT nested_count FROM event WHERE event_log_id = 4412"));

        // the same records again: replaced, not duplicated
        NewCollector(store, log, el).Collect(req, ct: Ct);
        Assert.Equal(2, store.Scalar<long>("SELECT count(*) FROM event WHERE module = 'EVTLOG'"));
    }

    [Fact]
    public void Rejected_credentials_stop_the_run_before_any_server_is_touched()
    {
        ThreeHours();
        using var store = CaseStore.Open(CaseDir);
        using var log = new CollectionLog(null);
        var access = new RecordingAccess { Reject = true };
        var cred = new NetworkCredential("someone", "wrong", "LAB");
        var report = new Collector(store, access, null, log).Collect(
            new CollectionRequest([new("DC01", "dc01", Share("DC01")), new("DC02", "dc02", Share("DC01"))], null, null, Credential: cred), ct: Ct);

        Assert.All(report.Servers, s =>
        {
            Assert.Equal(ServerStatus.Failed, s.Status);
            Assert.Contains("incorrect", s.Error);
            Assert.Empty(s.Plan);
        });
        Assert.Equal(0, access.Runs);
        Assert.Contains("incorrect", report.FatalError);
        Assert.Equal(0, store.Scalar<long>("SELECT count(*) FROM event"));

        Assert.Throws<CredentialRejectedException>(() =>
            new Collector(store, access, null, log).Test([new("DC01", "dc01", Share("DC01"))], cred, false, Ct));
    }

    [Fact]
    public void Remote_work_for_every_server_runs_under_the_requested_credentials()
    {
        ThreeHours();
        WriteLog("MEMBER01", 1, 1, Utc(2), 30, gzip: false);
        using var store = CaseStore.Open(CaseDir);
        using var log = new CollectionLog(null);
        var access = new RecordingAccess();
        var cred = new NetworkCredential("someone", "right", "LAB");
        var report = new Collector(store, access, null, log).Collect(
            new CollectionRequest([new("DC01", "dc01", Share("DC01")), new("MEMBER01", "member01", Share("MEMBER01"))], null, null, Credential: cred), ct: Ct);

        Assert.All(report.Servers, s => Assert.Equal(ServerStatus.Ok, s.Status));
        Assert.Equal(1, access.Validations);
        Assert.Equal(2, access.Runs);
        Assert.All(access.Credentials, c => Assert.Same(cred, c));
        Assert.Equal(210, store.Scalar<long>("SELECT count(*) FROM event"));
    }

    [Fact]
    public void Blocked_remote_event_log_gets_a_firewall_hint()
    {
        var blocked = new InvalidOperationException("The RPC server is unavailable.") { HResult = unchecked((int)0x800706BA) };
        Assert.Contains("Remote Event Log Management", Collector.EventLogHint(blocked));
        Assert.Equal("", Collector.EventLogHint(new IOException("other")));
    }

    private sealed class RecordingAccess : IRemoteAccess
    {
        public bool Reject { get; init; }
        public int Validations;
        public int Runs;
        public readonly System.Collections.Concurrent.ConcurrentBag<NetworkCredential?> Credentials = [];

        public void Validate(NetworkCredential credential, string? domain)
        {
            Interlocked.Increment(ref Validations);
            if (Reject) throw new CredentialRejectedException($"The user name or password for {credential.Domain}\\{credential.UserName} is incorrect.");
        }

        public void Run(NetworkCredential? credential, Action work)
        {
            Interlocked.Increment(ref Runs);
            Credentials.Add(credential);
            work();
        }
    }

    [Fact]
    public void Profiles_round_trip_in_case_json_without_credentials()
    {
        var s = new CaseSettings();
        s.Upsert(new CollectionProfile { Name = "lab", Servers = [new("DC01", "dc01.lab", null)], IncludeEventLog = true, LastCollectedUtc = Utc(3) });
        s.Upsert(new CollectionProfile { Name = "LAB", Servers = [new("DC01", "dc01.lab", @"\\dc01\d$\logs")] });
        Directory.CreateDirectory(CaseDir);
        s.Save(CaseDir);

        var json = File.ReadAllText(Path.Combine(CaseDir, CaseSettings.FileName));
        Assert.DoesNotContain("effectiveLogFolder", json);
        Assert.DoesNotContain("assword", json);
        var loaded = CaseSettings.Load(CaseDir);
        var p = Assert.Single(loaded.Profiles);
        Assert.Equal(@"\\dc01\d$\logs", p.Servers[0].EffectiveLogFolder);
        Assert.Equal(@"\\dc01.lab\admin$\debug", new ServerTarget("DC01", "dc01.lab").EffectiveLogFolder);
    }

    private sealed class FakeEventLog : IEventLogSource
    {
        public IEnumerable<EventLogRecordData> ReadRemote(string host, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        [
            new(101, Utc(2, 10), 4412, 4, "DFSR",
                "The DFS Replication service detected that a file was changed on multiple servers.\r\n\r\nOriginal File Path: C:\\Data\\a.txt\r\nNew Name in Conflict Folder: a-{GUID}-v1.txt"),
            new(102, Utc(2, 20), 4202, 3, "DFSR", "The DFS Replication service has detected that the staging space in use is above the high watermark."),
        ];

        public IEnumerable<EventLogRecordData> ReadFile(string evtxPath, CancellationToken ct = default) => [];
    }
}

