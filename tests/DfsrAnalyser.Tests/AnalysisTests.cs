using System.Text;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Query;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Tests;

/// <summary>
/// Top-N, file trace and CSV export over real lines cut from the lab samples (Fixtures/Trace):
/// file_0005.txt created on DFSRDC01 (UID {2314…}-v2038; Member01 receives it, install not in the fixture),
/// then — after a delete — re-created on DFSRMEMBER01 under a new UID {BFF1…}-v1019, offered, served, and
/// received, transferred (RAWGET) and installed on DFSRDC01.
/// </summary>
public sealed class AnalysisTests : IDisposable
{
    private const string OldUid = "{2314971A-C581-4D65-A9C1-9C297D1261E2}-v2038";
    private const string NewUid = "{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v1019";
    private static DateTime Utc(string s) => DateTime.SpecifyKind(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dfsra-an-" + Guid.NewGuid().ToString("N"));
    private readonly CaseStore _store;
    private readonly CaseQuery _q;

    public AnalysisTests()
    {
        var logs = Path.Combine(_root, "logs");
        foreach (var fixture in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Trace"), "*.txt"))
        {
            var parts = Path.GetFileNameWithoutExtension(fixture).Split('-'); // DFSRDC01-Dfsr00004
            var dir = Directory.CreateDirectory(Path.Combine(logs, parts[0])).FullName;
            File.Copy(fixture, Path.Combine(dir, parts[1] + ".log"));
        }
        _store = CaseStore.Open(Path.Combine(_root, "case"));
        var job = ImportJob.Run(_store, [logs], new ImportOptions(ServerNaming.FolderName), writeLog: false, ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, job.FailedCount);
        _q = new CaseQuery(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ trace

    [Theory]
    [InlineData("file_0005.txt", 2)]
    [InlineData("FILE_00*.TXT", 2)]
    [InlineData(NewUid, 1)]
    [InlineData("{bff1148f-18fe-47a1-9c5c-617caeb61a1e}-v1019", 1)]
    [InlineData("nosuchfile.txt", 0)]
    public void FindUids_by_name_pattern_or_uid(string text, int expected) =>
        Assert.Equal(expected, _q.FindUids(text).Count);

    [Fact]
    public void FindUids_lists_both_lives_of_a_reused_name_in_order()
    {
        var m = _q.FindUids("file_0005.txt");
        Assert.Equal([OldUid, NewUid], m.Select(x => x.Uid));
        Assert.Equal(["DFSRDC01", "DFSRMEMBER01"], m.Select(x => x.CreatedOn));
        Assert.All(m, x => Assert.Equal(2, x.Servers));
        Assert.Empty(_q.FindUids("file_0005.txt", fromUtc: Utc("2026-11-11 03:00")));
    }

    [Fact]
    public void Trace_gives_milestones_and_latency_per_server()
    {
        Assert.True(CaseQuery.TryParseUid(NewUid, out var db, out var ver));
        var t = _q.Trace(db, ver);

        Assert.Equal(NewUid, t.Uid);
        Assert.Equal("DFSRMEMBER01", t.CreatedOn);
        Assert.Equal(["file_0005.txt"], t.Names);
        var v = Assert.Single(t.Versions);
        Assert.Equal(NewUid, v.Gvsn); // a new file: GVSN == UID
        Assert.Equal("DFSRMEMBER01", v.Origin);
        Assert.Equal(Utc("2026-11-11 02:13:27.807"), v.ChangedAt);
        Assert.Equal("Local create", v.ChangeKind);
        Assert.Equal(Utc("2026-11-11 02:13:27.826"), v.OfferedAt);

        var d = Assert.Single(v.Deliveries);
        Assert.Equal("DFSRDC01", d.Server);
        Assert.Equal(Utc("2026-11-11 02:13:27.827"), d.ReceivedAt);
        Assert.Equal(Utc("2026-11-11 02:13:28.053"), d.TransferredAt);
        Assert.Equal("RAWGET", d.TransferType);
        Assert.Equal(Utc("2026-11-11 02:13:28.094"), d.InstalledAt);
        Assert.Equal("Installed: file content", d.Outcome);
        Assert.Equal(287, (d.InstalledAt!.Value - v.BaseTime).TotalMilliseconds, 3);
    }

    [Fact]
    public void Trace_links_the_uid_less_usn_line_by_fid_and_keeps_utc_order()
    {
        CaseQuery.TryParseUid(NewUid, out var db, out var ver);
        var t = _q.Trace(db, ver);

        var usn = Assert.Single(t.Events, e => e.ViaFid);
        Assert.Equal("Local create", usn.Category);
        Assert.Equal("DFSRMEMBER01", usn.Server);
        Assert.Equal(NewUid, usn.Gvsn); // version taken from the LDB line 1 ms earlier
        Assert.Equal(t.Events.OrderBy(e => e.TsUtc).ThenBy(e => e.EventId).Select(e => e.EventId), t.Events.Select(e => e.EventId));
        Assert.Contains(t.Events, e => e.Hidden && e.Category == "Local change: database update");
        Assert.Contains(t.Events, e => e.Server == "DFSRDC01" && e.Stage == "Install");
        Assert.DoesNotContain(t.Events, e => e.Gvsn == OldUid); // the earlier file of the same name is another UID
    }

    [Fact]
    public void Trace_of_a_version_not_installed_in_the_logs()
    {
        CaseQuery.TryParseUid(OldUid, out var db, out var ver);
        var v = Assert.Single(_q.Trace(db, ver).Versions);
        Assert.Equal("DFSRDC01", v.Origin);
        var d = Assert.Single(v.Deliveries);
        Assert.Equal(("DFSRMEMBER01", Utc("2026-11-11 01:52:25.244")), (d.Server, d.ReceivedAt!.Value));
        Assert.Null(d.InstalledAt);
        Assert.Null(d.TransferredAt);
    }

    [Fact]
    public void Trace_can_be_limited_to_a_time_range()
    {
        CaseQuery.TryParseUid(NewUid, out var db, out var ver);
        var t = _q.Trace(db, ver, fromUtc: Utc("2026-11-11 02:13:28"));
        Assert.All(t.Events, e => Assert.True(e.TsUtc >= Utc("2026-11-11 02:13:28")));
        Assert.Null(Assert.Single(t.Versions).ChangedAt); // the change is outside the range: base = first seen
    }

    [Theory]
    [InlineData("{2314971A-C581-4D65-A9C1-9C297D1261E2}-v2038", true)]
    [InlineData("2314971A-C581-4D65-A9C1-9C297D1261E2-v2038", true)]
    [InlineData(" {2314971a-c581-4d65-a9c1-9c297d1261e2}-v7 ", true)]
    [InlineData("{2314971A-C581-4D65-A9C1-9C297D1261E2}", false)]
    [InlineData("file_0005.txt", false)]
    public void Uid_parsing(string text, bool ok) => Assert.Equal(ok, CaseQuery.TryParseUid(text, out _, out _));

    // ------------------------------------------------------------------ Top-N and grid

    [Fact]
    public void TopN_files_ranks_with_share_files_and_servers()
    {
        var f = new EventFilter();
        var r = _q.TopN(f, CaseQuery.GetTopNList("files"), 10, TimeSpan.FromMinutes(1));
        var top = Assert.Single(r.Rows);
        Assert.Equal(("file_0005.txt", 1, 2L, 2), (top.Group.Value, top.Rank, top.Files, top.Servers));
        Assert.Equal(r.Total, top.Group.Count);
        Assert.Equal(1.0, top.Share);
        Assert.Empty(_q.TopN(f, CaseQuery.GetTopNList("folders"), 10, TimeSpan.FromMinutes(1)).Rows);
    }

    [Fact]
    public void TopN_honours_the_filter_and_orders_by_count()
    {
        var all = new EventFilter { ShowHidden = true };
        var cats = _q.TopN(all, CaseQuery.GetTopNList("category"), 3, TimeSpan.FromMinutes(1));
        Assert.Equal(3, cats.Rows.Count);
        Assert.Equal(cats.Rows.Select(x => x.Group.Count).OrderDescending(), cats.Rows.Select(x => x.Group.Count));
        Assert.Equal(_q.CountEvents(all), cats.Total);

        var dcOnly = all.Toggle("server", "DFSRDC01");
        var servers = _q.TopN(dcOnly, CaseQuery.GetTopNList("server"), 10, TimeSpan.FromMinutes(1));
        Assert.Equal(["DFSRDC01"], servers.Rows.Select(x => x.Group.Value));
        Assert.Empty(_q.TopN(all with { FileNamePattern = "*.docx" }, CaseQuery.GetTopNList("files"), 10, TimeSpan.FromMinutes(1)).Rows);
        Assert.Throws<ArgumentException>(() => CaseQuery.GetTopNList("nope"));
    }

    [Fact]
    public void Grid_leaves_sum_to_the_filtered_count_and_follow_level_totals()
    {
        foreach (var f in new[] { new EventFilter(), new EventFilter { ShowHidden = true }, new EventFilter { FileNamePattern = "file_*" } })
        {
            var leaves = _q.GridLeaves(f, [Dimensions.Server, Dimensions.Category]);
            Assert.Equal(_q.CountEvents(f), leaves.Sum(l => l.Count));
            // Servers appear in blocks, the larger total first.
            var serverOrder = leaves.Select(l => l.Values[0]).Distinct().ToList();
            var totals = serverOrder.Select(s => leaves.Where(l => l.Values[0] == s).Sum(l => l.Count)).ToList();
            Assert.Equal(totals.OrderDescending(), totals);
            var runs = leaves.Where((l, i) => i == 0 || leaves[i - 1].Values[0] != l.Values[0]).Count();
            Assert.Equal(serverOrder.Count, runs);
        }
    }

    [Fact]
    public void Older_cases_get_run_on_file_names_repaired_once_on_open()
    {
        // Simulate a case imported before the extractor fix.
        _store.Execute("UPDATE event SET file_name = file_name || ' fileSize(approx):1556480', extension = NULL WHERE file_name = 'file_0005.txt' AND class_method = 'Meet::Install'");
        _store.Execute("UPDATE event SET file_name = 'My File.docx fileSize(approx):12' WHERE class_method = 'Meet::Download'");
        _store.Execute("DELETE FROM meta WHERE key = 'repair_qualified_key_names'");
        var folder = _store.Folder;
        _store.Dispose();

        using var reopened = CaseStore.Open(folder);
        var names = reopened.Query("SELECT DISTINCT file_name, extension FROM event WHERE class_method IN ('Meet::Install', 'Meet::Download') ORDER BY 1")
            .Skip(1).Select(r => ((string?)r[0], (string?)r[1])).ToList(); // first row = column names
        Assert.Equal([("My File.docx", "docx"), ("file_0005.txt", "txt")], names);
        Assert.Equal(1L, Convert.ToInt64(reopened.Query("SELECT count(*) FROM meta WHERE key = 'repair_qualified_key_names'").Skip(1).Single()[0]));
    }

    // ------------------------------------------------------------------ CSV

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("-> DONE Install-rename completed", "'-> DONE Install-rename completed")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("@x", "'@x")]
    [InlineData("", "")]
    public void Csv_quotes_and_guards_text(string value, string expected)
    {
        var sw = new StringWriter();
        using (var csv = new CsvWriter(sw)) { csv.Text(value); csv.EndRow(); }
        Assert.Equal(expected + "\r\n", sw.ToString());
    }

    [Fact]
    public void Csv_numbers_and_times_are_invariant()
    {
        var sw = new StringWriter();
        using (var csv = new CsvWriter(sw)) { csv.Value(1234567L).Value(-2.5).Time(Utc("2026-11-11 02:13:27.807")).Value(null); csv.EndRow(); }
        Assert.Equal("1234567,-2.5,2026-11-11 02:13:27.807,\r\n", sw.ToString());
    }

    [Fact]
    public void Event_export_has_one_row_per_event_and_a_local_column_on_request()
    {
        var f = new EventFilter { ShowHidden = true };
        var path = Path.Combine(_root, "events.csv");
        long n;
        using (var csv = CsvWriter.Create(path)) n = _q.ExportEvents(f, null, csv, TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time"), TestContext.Current.CancellationToken);
        Assert.Equal(_q.CountEvents(f), n);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // BOM for Excel
        var rows = ParseCsv(Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal(n + 1, rows.Count);
        var header = rows[0];
        Assert.Equal(["Time (UTC)", "Time (local)", "Server local time", "Server"], header[..4]);
        Assert.All(rows, r => Assert.Equal(header.Length, r.Length));

        var done = rows.Single(r => r[Array.IndexOf(header, "Message")].StartsWith("'-> DONE", StringComparison.Ordinal));
        Assert.Equal("2026-11-11 02:13:28.094", done[0]);
        Assert.Equal("2026-11-11 13:13:28.094", done[1]); // AEDT = UTC+11
        Assert.Equal(done[1], done[2]);                    // the server's own clock, from the log
        Assert.Equal(NewUid, done[Array.IndexOf(header, "UID")]);
        Assert.Equal("Dfsr00004.log", done[Array.IndexOf(header, "Log file")]);
    }

    [Fact]
    public void Grid_topn_and_trace_exports_match_their_queries()
    {
        var f = new EventFilter();
        var sw = new StringWriter();
        using (var csv = new CsvWriter(sw)) _q.ExportGrid(f, [Dimensions.Category, Dimensions.Server], csv);
        var grid = ParseCsv(sw.ToString());
        Assert.Equal(["Category", "Server", "Events", "Per minute", "First (UTC)", "Last (UTC)"], grid[0]);
        Assert.Equal(_q.CountEvents(f), grid.Skip(1).Sum(r => long.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture)));

        sw = new StringWriter();
        var top = _q.TopN(f, CaseQuery.GetTopNList("category"), 5, TimeSpan.FromMinutes(1));
        using (var csv = new CsvWriter(sw)) CaseQuery.ExportTopN(top, _q.RangeMinutes(f), csv);
        var topRows = ParseCsv(sw.ToString());
        Assert.Equal(top.Rows.Count + 1, topRows.Count);
        Assert.Equal(top.Rows[0].Group.Value, topRows[1][1]);

        CaseQuery.TryParseUid(NewUid, out var db, out var ver);
        var trace = _q.Trace(db, ver);
        sw = new StringWriter();
        using (var csv = new CsvWriter(sw)) CaseQuery.ExportTrace(trace, csv, includeHidden: false);
        var traceRows = ParseCsv(sw.ToString());
        Assert.Equal(trace.Events.Count(e => !e.Hidden) + 1, traceRows.Count);
        var installed = traceRows.Single(r => r[4] == "Installed: file content");
        Assert.Equal("0.287", installed[1]);
        Assert.Equal("FID", traceRows.Single(r => r[4] == "Local create")[9]);
    }

    /// <summary>Minimal RFC 4180 reader for the tests.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add([.. row]); row.Clear();
                i++;
            }
            else field.Append(c);
        }
        return rows;
    }
}
