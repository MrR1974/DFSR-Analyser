using System.Globalization;
using System.Text;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Query;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Tests;

/// <summary>
/// Two servers, one minute of synthetic but well-formed log each:
/// DC01 offers 6 updates (seconds 0..5) and Member01 receives 4 RAWGETs (seconds 10..13), plus one
/// hidden dominance check on Member01. Local times are UTC+11 (AUS Eastern, DST).
/// </summary>
public sealed class QueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dfsra-q-" + Guid.NewGuid().ToString("N"));
    private readonly CaseStore _store;
    private readonly CaseQuery _q;
    private static readonly DateTime T0 = new(2026, 11, 11, 1, 0, 0, DateTimeKind.Utc); // 12:00 local

    public QueryTests()
    {
        Directory.CreateDirectory(_root);
        var dc = Write("dc", "DFSRDC01", Enumerable.Range(0, 6).Select(i =>
            $"{Local(i)} 4444 JOIN  1253 Join::SubmitUpdate Sent: uid:{{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}}-v{i + 1} gvsn:{{2314971A-C581-4D65-A9C1-9C297D1261E2}}-v{i + 1} name:file_{i}.{(i < 4 ? "txt" : "docx")} connId:{{DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5}} csId:{{B2683E40-9D0D-4BF7-A465-298E6B684DA8}} csName:DFSFolder"));
        var member = Write("member", "DFSRMEMBER01", Enumerable.Range(10, 4).Select(i =>
            $"{Local(i)} 3244 INCO  6535 InConnection::LogTransferActivity Received RAWGET updateName:file_{i}.txt uid:{{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}}-v{i} gvsn:{{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}}-v{i} connId:{{24CB324F-D1A5-463A-8D72-A108E69D8C9F}} csId:{{B2683E40-9D0D-4BF7-A465-298E6B684DA8}} stagedSize:100")
            .Append($"{Local(20)} 3868 MEET  6357 Meet::LocalDominates Remote version dominates localgvsn:{{2314971A-C581-4D65-A9C1-9C297D1261E2}}-v1 updateName:file_10.txt"));
        _store = CaseStore.Open(Path.Combine(_root, "case"));
        new Importer(_store).Import([dc, member], ct: TestContext.Current.CancellationToken);
        _q = new CaseQuery(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Local(int second) =>
        T0.AddHours(11).AddSeconds(second).ToString("yyyyMMdd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private string Write(string folder, string computer, IEnumerable<string> lines)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Dfsr00001.log");
        File.WriteAllText(path,
            $"* DFSR Log Sequence:1 Index:1 Computer:{computer} TimeZone:AUS Eastern Daylight Time (GMT+11:00) (TzId>AUS Eastern Standard Time<dIzT) Build:[x]\n" +
            string.Join("\n", lines) + "\n", Encoding.UTF8);
        return path;
    }

    private static readonly EventFilter All = new();

    [Fact]
    public void Extent_covers_visible_events()
    {
        var (min, max) = _q.Extent(All)!.Value;
        Assert.Equal(T0, min);
        Assert.Equal(T0.AddSeconds(13), max); // the dominance check at +20 s is hidden
        Assert.Equal(T0.AddSeconds(20), _q.Extent(All with { ShowHidden = true })!.Value.Max);
    }

    [Fact]
    public void Facets_ignore_their_own_selection_but_respect_others()
    {
        var f = All.Toggle("server", "DFSRDC01");

        var servers = _q.Facet(Dimensions.Server, f);
        Assert.Equal([("DFSRDC01", 6L, true), ("DFSRMEMBER01", 4L, false)], servers.Select(v => (v.Value, v.Count, v.Selected)));

        var ext = _q.Facet(Dimensions.Extension, f);
        Assert.Equal([("txt", 4L), ("docx", 2L)], ext.Select(v => (v.Value, v.Count)));
    }

    [Fact]
    public void Facet_search_and_selected_zero_count_values()
    {
        var f = All.Toggle("file", "no-such-file");
        var files = _q.Facet(Dimensions.FileName, f, search: "FILE_1");
        Assert.Equal("no-such-file", files[0].Value);
        Assert.Equal(0, files[0].Count);
        Assert.Equal(["file_1.txt", "file_10.txt", "file_11.txt", "file_12.txt", "file_13.txt"],
            files.Skip(1).Select(v => v.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Series_stacks_by_dimension_with_zero_filled_buckets()
    {
        var s = _q.Series(All, TimeSpan.FromSeconds(5), Dimensions.Server);
        Assert.Equal(T0, s.Buckets[0]);
        Assert.Equal(3, s.Buckets.Length); // 0-5, 5-10, 10-15
        var dc = s.Series.Single(x => x.Key == "DFSRDC01").Counts;
        var member = s.Series.Single(x => x.Key == "DFSRMEMBER01").Counts;
        Assert.Equal([5L, 1, 0], dc);
        Assert.Equal([0L, 0, 4], member);
    }

    [Fact]
    public void Series_folds_the_tail_into_other()
    {
        var s = _q.Series(All, TimeSpan.FromMinutes(1), Dimensions.FileName, topN: 3);
        Assert.Equal(4, s.Series.Count);
        Assert.Equal("Other", s.Series[^1].Key);
        Assert.Equal(10, s.Series.Sum(x => x.Counts.Sum()));
    }

    [Fact]
    public void Groups_give_counts_peaks_and_trends_under_a_parent_path()
    {
        var top = _q.Groups(All, [], Dimensions.Category, TimeSpan.FromSeconds(5));
        Assert.Equal(["Update offered to partner", "Transfer: RAWGET (full)"], top.Select(g => g.Value));
        var offered = top[0];
        Assert.Equal(6, offered.Count);
        Assert.Equal(5, offered.PeakCount);
        Assert.Equal(T0, offered.PeakAt);
        Assert.Equal(6, offered.Trend.Sum());

        var byExt = _q.Groups(All, [(Dimensions.Category, "Update offered to partner")], Dimensions.Extension, TimeSpan.FromSeconds(5));
        Assert.Equal([("txt", 4L), ("docx", 2L)], byExt.Select(g => (g.Value, g.Count)));
    }

    [Fact]
    public void Tied_peaks_resolve_to_the_earliest_window()
    {
        // Member01's four RAWGETs are one per second: every 1 s window ties at 1.
        var member = _q.Groups(All, [(Dimensions.Server, "DFSRMEMBER01")], Dimensions.Category, TimeSpan.FromSeconds(1)).Single();
        Assert.Equal(1, member.PeakCount);
        for (var i = 0; i < 5; i++)
            Assert.Equal(T0.AddSeconds(10), _q.Groups(All, [(Dimensions.Server, "DFSRMEMBER01")], Dimensions.Category, TimeSpan.FromSeconds(1)).Single().PeakAt);
    }

    [Fact]
    public void Events_page_in_time_order_and_detail_decodes_fields()
    {
        var path = new List<(Dimension, string)> { (Dimensions.Server, "DFSRMEMBER01") };
        Assert.Equal(4, _q.CountEvents(All, path));
        var page = _q.Events(All, path, offset: 1, limit: 2);
        Assert.Equal([T0.AddSeconds(11), T0.AddSeconds(12)], page.Select(e => e.TsUtc));

        var d = _q.Detail(page[0].EventId)!;
        Assert.Equal("Inbound", d.Group);
        Assert.Contains("Full copy", d.Note);
        Assert.Equal(T0.AddHours(11).AddSeconds(11), d.TsLocal);
        Assert.Contains(d.Fields, f => f.Name == "Transfer" && f.Value == "RAWGET");
        Assert.Contains(d.Fields, f => f.Name == "GVSN" && f.Value == "{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v11");
    }

    [Fact]
    public void Message_text_and_time_bounds_filter()
    {
        Assert.Equal(1, _q.CountEvents(All with { MessageText = "FILE_5.docx" }));
        // [3 s, 11 s): DC01 at 3, 4, 5 and Member01 at 10; the upper bound is exclusive.
        Assert.Equal(4, _q.CountEvents(All with { FromUtc = T0.AddSeconds(3), ToUtc = T0.AddSeconds(11) }));
        Assert.Equal(3, _q.CountEvents(All with { FromUtc = T0.AddSeconds(3), ToUtc = T0.AddSeconds(10) }));
    }

    [Theory]
    [InlineData("file_?.txt", 4)]          // file_0..3 on DC01; file_10.txt has two characters
    [InlineData("FILE_1?.TXT", 4)]         // case-insensitive; the hidden dominance check stays hidden
    [InlineData("*.docx", 2)]
    [InlineData("*.txt;*.docx", 10)]
    [InlineData("*.txt;!file_1*", 3)]       // file_0, 2, 3 (file_1.txt and file_1x.txt excluded)
    [InlineData("!*.docx", 8)]
    [InlineData("file_1", 0)]               // no wildcard = whole name
    [InlineData("file_10.txt", 1)]
    [InlineData(" file_10.txt ; ", 1)]
    [InlineData("file%", 0)]                // LIKE characters are literal
    [InlineData("file_%.txt", 0)]
    [InlineData("", 10)]
    public void File_name_pattern_filters_events(string pattern, long expected) =>
        Assert.Equal(expected, _q.CountEvents(All with { FileNamePattern = pattern }));

    [Fact]
    public void File_name_pattern_combines_with_hidden_categories_time_and_facets()
    {
        Assert.Equal(5, _q.CountEvents(All with { FileNamePattern = "file_1?.txt", ShowHidden = true }));
        Assert.Equal(2, _q.CountEvents(All with { FileNamePattern = "file_1*", FromUtc = T0.AddSeconds(11), ToUtc = T0.AddSeconds(13) }));

        var f = All with { FileNamePattern = "*.txt" };
        Assert.Equal([("DFSRDC01", 4L), ("DFSRMEMBER01", 4L)], _q.Facet(Dimensions.Server, f).Select(v => (v.Value, v.Count)));
        Assert.Equal([("txt", 8L)], _q.Facet(Dimensions.Extension, f).Select(v => (v.Value, v.Count)));
        Assert.Equal(
            [("Transfer: RAWGET (full)", "DFSRMEMBER01", 4L), ("Update offered to partner", "DFSRDC01", 4L)],
            _q.Matrix(f, Dimensions.Category, Dimensions.Server));
        Assert.Null(f.ClearAll().FileNamePattern);
    }

    [Fact]
    public void Facet_search_accepts_wildcards()
    {
        Assert.Equal(["file_4.docx", "file_5.docx"], _q.Facet(Dimensions.FileName, All, search: "*.DOCX").Select(v => v.Value).Order(StringComparer.Ordinal));
        Assert.Equal(["file_4.docx", "file_5.docx"], _q.Facet(Dimensions.FileName, All, search: "!*.txt").Select(v => v.Value).Order(StringComparer.Ordinal));
        Assert.Equal(["file_1.txt"], _q.Facet(Dimensions.FileName, All, search: "file_?.txt;!file_0*;!file_2*;!file_3*").Select(v => v.Value));
    }

    [Theory]
    [InlineData("*.txt", "%.txt")]
    [InlineData("file_?", @"file\__")]
    [InlineData(@"100%\a", @"100\%\\a")]
    [InlineData("~$*", "~$%")]
    public void Wildcards_translate_to_escaped_like_patterns(string pattern, string like) =>
        Assert.Equal(like, FileNamePattern.ToLike(pattern));

    [Fact]
    public void Pattern_lists_split_into_includes_and_excludes()
    {
        var p = FileNamePattern.Parse(" *.docx ;*.xlsx; ! ~$* ;;!");
        Assert.Equal(["*.docx", "*.xlsx"], p.Includes);
        Assert.Equal(["~$*"], p.Excludes);
        Assert.Equal("*.docx;*.xlsx;!~$*", p.ToString());
        Assert.True(FileNamePattern.Parse(" ; ").IsEmpty);
    }

    [Fact]
    public void Auto_bucket_picks_the_smallest_preset_that_fits()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), CaseQuery.AutoBucket(TimeSpan.FromMinutes(2)));
        Assert.Equal(TimeSpan.FromMinutes(1), CaseQuery.AutoBucket(TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.FromHours(1), CaseQuery.AutoBucket(TimeSpan.FromDays(7)));
    }
}
