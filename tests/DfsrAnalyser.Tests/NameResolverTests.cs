using System.Text;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Query;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Tests;

/// <summary>
/// Member01's log with a configuration dump (lines taken from the lab samples), one local create (which
/// reveals Member01's DB GUID), one update offered on the outbound connection, and one RAWGET received
/// on the inbound connection for a file created on DC01 (whose DB GUID no local change reveals).
/// </summary>
public sealed class NameResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dfsra-names-" + Guid.NewGuid().ToString("N"));
    private readonly CaseStore _store;
    private readonly CaseQuery _q;

    private const string Member01Db = "BFF1148F-18FE-47A1-9C5C-617CAEB61A1E";
    private const string Dc01Db = "2314971A-C581-4D65-A9C1-9C297D1261E2";
    private const string Inbound = "24CB324F-D1A5-463A-8D72-A108E69D8C9F";
    private const string Outbound = "DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5";
    private const string Rf = "B2683E40-9D0D-4BF7-A465-298E6B684DA8";

    private static string Cpar(int sec, string method, string text) => $"20261111 12:51:{sec:00}.266 7112 CPAR   205 Config::{method}::Print {text}";

    private static readonly string[] Lines =
    [
        Cpar(12, "ParamBlock", "[[ DfsrReplicationGroup ]]"),
        Cpar(12, "GuidParam", "ReplicationGroupGuid: BDF5101B-0CBA-4360-80B7-868BDC513EA1 [Flags:0x1]"),
        Cpar(12, "StringParam", @"ReplicationGroupName: dfsrtest.com\testnamespace\dfsfolder [Flags:0x1]"),
        // an unrelated line from another module in the middle of the dump
        "20261111 12:51:12.267 4000 TASK   581 Task::Schedule task:0000026F0CF2D0D0 state:INACTIVE",
        Cpar(12, "ParamBlock", "[[ DfsrMember ]]"),
        Cpar(12, "GuidParam", "MemberGuid: D49A24EF-17DE-42C0-8BB3-7CBB89B28DD4 [Flags:0x1]"),
        Cpar(12, "StringParam", "MemberName: DFSRMEMBER01 [Flags:0x1]"),
        Cpar(12, "ParamBlock", "[[ DfsrConnection ]]"),
        Cpar(12, "GuidParam", $"ConnectionGuid: {Inbound} [Flags:0x1]"),
        Cpar(12, "GuidParam", "PartnerGuid: FACB0C1D-A767-45E9-B7D4-2B053EC53EF5 [Flags:0x1]"),
        Cpar(12, "StringParam", "PartnerName: DFSRDC01 [Flags:0x1]"),
        Cpar(12, "BoolParam", "Enabled: TRUE [Flags:0x1]"),
        Cpar(12, "BoolParam", "Inbound: TRUE [Flags:0x1]"),
        Cpar(12, "BoolParam", "RdcEnabled: TRUE [Flags:0x1]"),
        Cpar(12, "ParamBlock", "[[ DfsrConnection ]]"),
        Cpar(12, "GuidParam", $"ConnectionGuid: {Outbound} [Flags:0x1]"),
        Cpar(12, "StringParam", "PartnerName: DFSRDC01 [Flags:0x1]"),
        Cpar(12, "BoolParam", "Inbound: FALSE [Flags:0x1]"),
        Cpar(12, "ParamBlock", "[[ DfsrReplicatedFolder ]]"),
        Cpar(12, "GuidParam", $"ReplicatedFolderGuid: {Rf} [Flags:0x1]"),
        Cpar(12, "StringParam", "ReplicatedFolderName: DFSFolder [Flags:0x1]"),
        Cpar(12, "GuidParam", "ReplicationGroupGuid: BDF5101B-0CBA-4360-80B7-868BDC513EA1 [Flags:0x1]"),
        Cpar(12, "PathParam", @"RootPath: C:\Data [Flags:0x1]"),
        Cpar(12, "DWordParam", "StagingSizeInMb: 4096 [Flags:0x1]"),
        Cpar(12, "BoolParam", "IsPrimary: FALSE [Flags:0x1]"),
        Cpar(12, "StringParam", "Description: <null> [Flags:0x0]"),
        // local create on Member01: its DB GUID
        "20261111 13:13:00.100 7124 USNC  2871 UsnConsumer::CreateNewRecord LDB Inserting ID Record:",
        $"+\tgvsn                            {{{Member01Db}}}-v12",
        $"+\tuid                             {{{Member01Db}}}-v12",
        $"+\tcsId                            {{{Rf}}}",
        "+\tname                            file_1002.txt",
        // a USN-only update carries a remote GVSN and must NOT count as evidence for Member01
        "20261111 13:13:00.200 7124 USNC  2234 UsnConsumer::UpdateUsnOnly LDB Updating ID Record:",
        $"+\tgvsn                            {{{Dc01Db}}}-v2038",
        $"20261111 13:13:00.300 4092 JOIN  1253 Join::SubmitUpdate Sent: uid:{{{Member01Db}}}-v12 gvsn:{{{Member01Db}}}-v12 name:file_1002.txt connId:{{{Outbound}}} csId:{{{Rf}}}",
        $"20261111 13:13:00.400 3244 INCO  6535 InConnection::LogTransferActivity Received RAWGET updateName:file_0001.txt uid:{{{Dc01Db}}}-v1 gvsn:{{{Dc01Db}}}-v1 connId:{{{Inbound}}} csId:{{{Rf}}} stagedSize:10",
    ];

    public NameResolverTests()
    {
        Directory.CreateDirectory(_root);
        var log = Path.Combine(_root, "Dfsr00001.log");
        File.WriteAllText(log,
            "* DFSR Log Sequence:1 Index:1 Computer:DFSRMEMBER01 TimeZone:AUS Eastern Daylight Time (GMT+11:00) (TzId>AUS Eastern Standard Time<dIzT) Build:[x]\n" +
            string.Join("\n", Lines) + "\n", Encoding.UTF8);
        _store = CaseStore.Open(Path.Combine(_root, "case"));
        new Importer(_store).Import([log], ct: TestContext.Current.CancellationToken);
        _q = new CaseQuery(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static readonly EventFilter All = new() { ShowHidden = true };

    [Fact]
    public void Config_dump_names_groups_folders_members_and_connections()
    {
        Assert.Equal(@"dfsrtest.com\testnamespace\dfsfolder", _store.Scalar<string>("SELECT name FROM name_rg"));
        Assert.Equal("DFSFolder", _store.Scalar<string>("SELECT name FROM name_rf"));
        Assert.Equal(2, _store.Scalar<long>("SELECT count(*) FROM name_member")); // self + partner
        Assert.True(_store.Scalar<bool>($"SELECT inbound FROM name_conn WHERE conn_id = '{Inbound}'"));
        Assert.False(_store.Scalar<bool>($"SELECT inbound FROM name_conn WHERE conn_id = '{Outbound}'"));
        Assert.Equal(4096, _store.Scalar<int>("SELECT staging_size_mb FROM rf_config"));
        Assert.Null(_store.Scalar<string?>("SELECT file_filter FROM rf_config"));
    }

    [Fact]
    public void Db_guid_is_inferred_from_local_database_writes_only()
    {
        Assert.Equal("DFSRMEMBER01", _store.Scalar<string>($"SELECT server FROM name_db WHERE guid = '{Member01Db}'"));
        Assert.Equal(1, _store.Scalar<long>("SELECT evidence FROM name_db"));
        Assert.Equal(0, _store.Scalar<long>($"SELECT count(*) FROM name_db WHERE guid = '{Dc01Db}'"));
    }

    [Fact]
    public void Dimensions_use_the_resolved_names()
    {
        var conns = _q.Facet(Dimensions.Connection, All).ToDictionary(v => v.Value, v => v.Count);
        Assert.Equal(1, conns["to DFSRDC01"]);
        Assert.Equal(1, conns["from DFSRDC01"]);

        var origins = _q.Facet(Dimensions.OriginServer, All).Select(v => v.Value).ToList();
        Assert.Contains("DFSRMEMBER01", origins);
        Assert.Contains("unknown {2314971A…}", origins); // DC01's GUID: no local change in these logs

        var folders = _q.Facet(Dimensions.ReplicatedFolder, All);
        Assert.Equal("DFSFolder", folders.First(v => v.Value != Dimension.None).Value);
        Assert.Contains(_q.Facet(Dimensions.ReplicationGroup, All), v => v.Value == @"dfsrtest.com\testnamespace\dfsfolder");

        // filter + group through a name join
        var f = All.Toggle(Dimensions.Direction.Key, "Inbound");
        Assert.Equal(1, _q.CountEvents(f));
        var groups = _q.Groups(All, [(Dimensions.Partner, "DFSRDC01")], Dimensions.Direction, TimeSpan.FromMinutes(1));
        Assert.Equal(["Inbound", "Outbound"], groups.Select(g => g.Value).Order());
    }

    [Fact]
    public void Detail_shows_names_beside_guids()
    {
        var id = _store.Scalar<long>("SELECT event_id FROM event WHERE class_method = 'InConnection::LogTransferActivity'");
        var d = _q.Detail(id)!;
        Assert.Contains(d.Fields, x => x.Name == "Connection" && x.Value.EndsWith("(from DFSRDC01)"));
        Assert.Contains(d.Fields, x => x.Name == "Replicated folder" && x.Value.EndsWith("(DFSFolder)"));

        var sent = _q.Detail(_store.Scalar<long>("SELECT event_id FROM event WHERE class_method = 'Join::SubmitUpdate'"))!;
        Assert.Contains(sent.Fields, x => x.Name == "GVSN" && x.Value.EndsWith("(last changed on DFSRMEMBER01)"));
    }

    [Fact]
    public void Resolution_is_repeatable()
    {
        var first = new Core.Names.NameResolver(_store).Resolve();
        var second = new Core.Names.NameResolver(_store).Resolve();
        Assert.Equal(first, second);
    }
}
