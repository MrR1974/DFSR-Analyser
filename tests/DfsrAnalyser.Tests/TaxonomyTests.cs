using System.Text;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Storage;
using DfsrAnalyser.Core.Taxonomy;

namespace DfsrAnalyser.Tests;

public class RuleSetTests
{
    [Fact]
    public void Default_rules_load_and_validate()
    {
        var rules = RuleSet.Default();
        rules.Validate();
        Assert.True(rules.Rules.Count > 50);
        Assert.Contains(rules.Categories, c => c.Group == RuleSet.PlumbingGroup);
    }

    [Fact]
    public void Validation_reports_unknown_categories_and_empty_rules()
    {
        var rules = RuleSet.Parse("""
            { "categories": [ { "name": "A", "group": "G" } ],
              "rules": [ { "category": "B", "module": "MEET" }, { "category": "A" } ] }
            """);
        var ex = Assert.Throws<RuleSetException>(rules.Validate);
        Assert.Contains("unknown category 'B'", ex.Message);
        Assert.Contains("has no conditions", ex.Message);
    }

    [Fact]
    public void Single_values_and_lists_are_both_accepted()
    {
        var rules = RuleSet.Parse("""
            { "categories": [ { "name": "A", "group": "G" } ],
              "rules": [ { "category": "A", "module": "MEET", "classMethod": ["X::Y", "Z::*"], "errorCode": 32 } ] }
            """);
        Assert.Equal(["MEET"], rules.Rules[0].Module!);
        Assert.Equal(["X::Y", "Z::*"], rules.Rules[0].ClassMethod!);
        Assert.Equal([32], rules.Rules[0].ErrorCode!);
    }

    [Fact]
    public void Conditions_compile_to_sql()
    {
        var sql = RuleSql.Condition(new Rule
        {
            Category = "A",
            Module = ["MEET"],
            ClassMethod = ["Meet::Install", "Meet::*_x"],
            Message = "^it's",
            Severity = "warn",
            InnerErrorCode = [1722],
        });
        Assert.Equal(
            "(module = 'MEET' AND (class_method = 'Meet::Install' OR class_method LIKE 'Meet::%\\_x' ESCAPE '\\') " +
            "AND regexp_matches(message, '^it''s') AND severity = 1 AND error_code_inner IN (1722))",
            sql);
    }
}

public sealed class CategorisationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dfsra-tax-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CategorisationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private const string Head =
        "* DFSR Log Sequence:1 Index:1 Computer:DFSRDC01 TimeZone:AUS Eastern Daylight Time (GMT+11:00) (TzId>AUS Eastern Standard Time<dIzT) Build:[x]\n" +
        @"* Configuration logLevel:4 maxEntryCount:200000 maxFileCount:1000 logPath:\\.\C:\Windows\debug\" + "\n";

    private const string Uid = "uid:{2314971A-C581-4D65-A9C1-9C297D1261E2}-v2042 gvsn:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v15 connId:{DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5}";

    private static readonly string[] Lines =
    [
        // local create: the USN-record line is the counted one, the LDB line is a hidden companion
        Samples.CreateNewRecord, .. Samples.IdRecord,
        "20261111 11:47:05.743 3988 USNC  2874 UsnConsumer::CreateNewRecord ID record created from USN_RECORD:",
        "+\tReason:              Close Data Extend File Create ",
        "+\tFileName:            file_0001.txt",
        Samples.SubmitUpdateSent,
        Samples.RawGet,
        // remote delete: move-out inside a tombstone install on the same thread
        $"20261111 13:12:44.657 4244 MEET  5384 Meet::InstallTombstone Moving out existing content updateName:file_0009.txt {Uid}",
        $"20261111 13:12:44.660 4244 MEET  5667 Meet::MoveOut Moving to conflict/deleted:0x200000001291C updateName:file_0009.txt {Uid}",
        $"20261111 13:12:44.682 4244 MEET  5418 Meet::InstallTombstone -> DONE Install Tombstone complete updateName:file_0009.txt {Uid}",
        // conflict: the loser's move-out is also inside a tombstone install, but a conflict for the same file wins
        "20261111 12:55:38.362 1372 MEET  6324 Meet::LocalDominates Conflicting gvsn:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v11 updateName:file_1001.txt uid:{2314971A-C581-4D65-A9C1-9C297D1261E2}-v3035",
        "20261111 12:55:38.505 4164 MEET  5338 Meet::InstallTombstone Moving out existing content updateName:file_1001.txt uid:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v11",
        "20261111 12:55:38.505 4164 MEET  5667 Meet::MoveOut Moving to conflict/deleted:0x2000000012952 updateName:file_1001.txt uid:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v11",
        // benign error, routine dominance check, plumbing with nested detail, unknown module
        "20261111 13:00:00.100 3888 DOWN  1234 [ERROR] DownstreamTransport::RdcGet Failed on connId:{DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5} Error:",
        "+\t[Error:9047(0x2357) Rdc::SeedFile::Initialize seedfile.cpp:100 3888 C The download is redundant]",
        "20261111 13:00:00.200 3888 MEET  6357 Meet::LocalDominates Remote version dominates localgvsn:{2314971A-C581-4D65-A9C1-9C297D1261E2}-v2049 updateName:file_0016.txt",
        "20261111 13:00:00.300 3808 ASYN   522 AsyncUnbufferedFileWriter::Close Async WRITE Statistics:",
        "+\tTotal I/O Bytes             1756497 (2 buffers)",
        "+\tNumber of Async Buffers     2",
        "20261111 13:00:00.400 1 ZZZZ 1 Foo::Bar something new",
        Samples.Exit,
    ];

    private CaseStore ImportSample(bool dropPlumbing = false)
    {
        var log = Path.Combine(_root, "Dfsr00001.log.gz");
        using (var fs = File.Create(log))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
            gz.Write(Encoding.UTF8.GetBytes(Head + string.Join("\n", Lines) + "\n"));
        var store = CaseStore.Open(Path.Combine(_root, "case"));
        new Importer(store) { DropPlumbingDetail = dropPlumbing }.Import([log], ct: Ct);
        return store;
    }

    private static string? CategoryOf(CaseStore store, string classMethod, string messagePrefix) =>
        store.Scalar<string?>("SELECT category FROM event WHERE class_method = ? AND starts_with(message, ?)", classMethod, messagePrefix);

    [Fact]
    public void Lines_are_categorised_as_they_load()
    {
        using var store = ImportSample();
        Assert.Equal("Local create", CategoryOf(store, "UsnConsumer::CreateNewRecord", "ID record created"));
        Assert.Equal("Local change: database update", CategoryOf(store, "UsnConsumer::CreateNewRecord", "LDB Inserting"));
        Assert.Equal("Update offered to partner", CategoryOf(store, "Join::SubmitUpdate", "Sent:"));
        Assert.Equal("Transfer: RAWGET (full)", CategoryOf(store, "InConnection::LogTransferActivity", "Received RAWGET"));
        Assert.Equal("Installed: delete (tombstone)", CategoryOf(store, "Meet::InstallTombstone", "-> DONE"));
        Assert.Equal("Conflict detected", CategoryOf(store, "Meet::LocalDominates", "Conflicting"));
        Assert.Equal("Dominance check", CategoryOf(store, "Meet::LocalDominates", "Remote version"));
        Assert.Equal("Redundant download (benign)", CategoryOf(store, "DownstreamTransport::RdcGet", "Failed"));
        Assert.Equal("Async I/O statistics", CategoryOf(store, "AsyncUnbufferedFileWriter::Close", "Async"));
        Assert.Equal("Other (ZZZZ)", CategoryOf(store, "Foo::Bar", "something"));
        Assert.Equal(0, store.Scalar<long>("SELECT count(*) FROM event WHERE category IS NULL"));
    }

    [Fact]
    public void Move_outs_are_classified_from_their_context()
    {
        using var store = ImportSample();
        Assert.Equal("Moved to ConflictAndDeleted: remote delete",
            store.Scalar<string>("SELECT category FROM event WHERE class_method = 'Meet::MoveOut' AND file_name = 'file_0009.txt'"));
        Assert.Equal("Moved to ConflictAndDeleted: conflict loser",
            store.Scalar<string>("SELECT category FROM event WHERE class_method = 'Meet::MoveOut' AND file_name = 'file_1001.txt'"));
    }

    [Fact]
    public void Rules_can_be_changed_and_reapplied_without_reimporting()
    {
        using var store = ImportSample();
        var rules = RuleSet.Default();
        rules.Categories.Add(new CategoryDef { Name = "Novel thing", Group = "Experiments" });
        rules.Rules.Insert(0, new Rule { Category = "Novel thing", Module = ["ZZZZ"] });

        store.SaveAndApplyRules(rules);

        Assert.Equal("Novel thing", CategoryOf(store, "Foo::Bar", "something"));
        Assert.Equal("Experiments", store.Scalar<string>("SELECT grp FROM category WHERE name = 'Novel thing'"));
        Assert.Contains("Novel thing", File.ReadAllText(store.RulesPath));
        // correlation still applied after a full re-categorisation
        Assert.Equal("Moved to ConflictAndDeleted: conflict loser",
            store.Scalar<string>("SELECT category FROM event WHERE class_method = 'Meet::MoveOut' AND file_name = 'file_1001.txt'"));
    }

    [Fact]
    public void Invalid_regex_is_rejected_with_the_rule_number()
    {
        using var store = ImportSample();
        var rules = RuleSet.Default();
        rules.Rules.Insert(0, new Rule { Category = rules.Categories[0].Name, Message = "([unclosed" });
        var ex = Assert.Throws<RuleSetException>(() => store.UseRules(rules));
        Assert.StartsWith("Rule 1 ", ex.Message);
    }

    [Fact]
    public void Plumbing_detail_can_be_dropped_at_import()
    {
        using var store = ImportSample(dropPlumbing: true);
        var asyncId = store.Scalar<long>("SELECT event_id FROM event WHERE class_method = 'AsyncUnbufferedFileWriter::Close'");
        Assert.Equal(0, store.Scalar<long>("SELECT count(*) FROM event_nested WHERE event_id = ?", asyncId));
        Assert.Equal(2, store.Scalar<int>("SELECT nested_count FROM event WHERE event_id = ?", asyncId));
        Assert.True(store.ListFiles()[0].NestedDropped);
        // non-plumbing detail is kept
        Assert.Equal(1, store.Scalar<long>("SELECT count(*) FROM event_nested n JOIN event e USING (event_id) WHERE e.category = 'Local create'"));
    }

    [Fact]
    public void A_new_case_gets_the_default_rules_file()
    {
        using var store = CaseStore.Open(Path.Combine(_root, "fresh"));
        Assert.True(File.Exists(store.RulesPath));
        Assert.Equal(RuleSet.DefaultJson(), File.ReadAllText(store.RulesPath));
    }
}
