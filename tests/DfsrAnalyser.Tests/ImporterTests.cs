using System.IO.Compression;
using System.Text;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Tests;

public sealed class ImporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dfsra-tests-" + Guid.NewGuid().ToString("N"));
    private string Logs => Path.Combine(_root, "logs");
    private string CaseDir => Path.Combine(_root, "case");

    public ImporterTests() => Directory.CreateDirectory(Logs);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Header(int seq, int idx, string computer = "DFSRDC01") =>
        $"* DFSR Log Sequence:{seq} Index:{idx} Computer:{computer} TimeZone:AUS Eastern Daylight Time (GMT+11:00) (TzId>AUS Eastern Standard Time<dIzT) Build:[Jan 01 2016 08:00:00 built by: WinBuild (FRE)]\r\n" +
        @"* Configuration logLevel:4 maxEntryCount:200000 maxFileCount:1000 logPath:\\.\C:\Windows\debug\" + "\r\n";

    private string WriteLog(string name, string content, bool gzip)
    {
        var path = Path.Combine(Logs, name);
        var bytes = Encoding.UTF8.GetBytes(content);
        if (gzip)
        {
            using var fs = File.Create(path);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            gz.Write(bytes);
        }
        else
        {
            File.WriteAllBytes(path, bytes);
        }
        return path;
    }

    /// <summary>A roll that splits the CreateNewRecord ID record: the file name only appears in the second file.</summary>
    private (string A, string B) WriteSplitRoll()
    {
        var firstHalf = Samples.IdRecord[..7];
        var secondHalf = Samples.IdRecord[7..];
        var a = WriteLog("Dfsr00004.log.gz",
            Header(1, 4) + Samples.SubmitUpdateSent + "\r\n" + Samples.CreateNewRecord + "\r\n" + string.Join("\r\n", firstHalf) + "\r\n", gzip: true);
        var b = WriteLog("Dfsr00005.log.gz",
            Header(2, 5) + string.Join("\r\n", secondHalf) + "\r\n" + Samples.RawGet + "\r\n" + Samples.Exit + "\r\n", gzip: true);
        return (a, b);
    }

    private static (long Nested, string? Name) SplitEvent(CaseStore store) =>
        (store.Scalar<long>("SELECT nested_count FROM event WHERE class_method = 'UsnConsumer::CreateNewRecord'"),
         store.Scalar<string?>("SELECT file_name FROM event WHERE class_method = 'UsnConsumer::CreateNewRecord'"));

    [Fact]
    public void Stitches_orphan_lines_onto_the_previous_file_in_one_import()
    {
        var (a, b) = WriteSplitRoll();
        using var store = CaseStore.Open(CaseDir);
        var results = new Importer(store).Import([b, a]);

        Assert.All(results, r => Assert.Equal(FileOutcome.Imported, r.Outcome));
        Assert.Equal((Samples.IdRecord.Length, "file_0001.txt"), SplitEvent(store));
        Assert.Equal(0, new CaseStats(store).PendingOrphans());
        Assert.Equal(4, store.Scalar<long>("SELECT count(*) FROM event"));
    }

    [Fact]
    public void Holds_orphans_until_the_previous_file_arrives()
    {
        var (a, b) = WriteSplitRoll();
        using var store = CaseStore.Open(CaseDir);
        var importer = new Importer(store);

        importer.Import([b]);
        Assert.Equal(1, new CaseStats(store).PendingOrphans());

        importer.Import([a]);
        Assert.Equal(0, new CaseStats(store).PendingOrphans());
        Assert.Equal((Samples.IdRecord.Length, "file_0001.txt"), SplitEvent(store));
    }

    [Fact]
    public void Resumes_a_growing_active_log_and_recognises_it_once_gzipped()
    {
        var head = Header(1, 1, "DFSRMEMBER01");
        var lines = new List<string> { Samples.SubmitUpdateSent, Samples.CreateNewRecord };
        lines.AddRange(Samples.IdRecord[..3]);
        var log = WriteLog("Dfsr00001.log", head + string.Join("\r\n", lines) + "\r\n", gzip: false);

        using var store = CaseStore.Open(CaseDir);
        var importer = new Importer(store);

        var r1 = Assert.Single(importer.Import([log]));
        Assert.Equal(FileOutcome.Imported, r1.Outcome);
        Assert.False(Assert.Single(store.ListFiles()).IsComplete);
        Assert.Equal(2, store.Scalar<long>("SELECT count(*) FROM event"));

        // The service keeps writing: the last event gains nested lines and new events follow.
        lines.AddRange(Samples.IdRecord[3..]);
        lines.Add(Samples.RawGet);
        File.WriteAllText(log, head + string.Join("\r\n", lines) + "\r\n");

        var r2 = Assert.Single(importer.Import([log]));
        Assert.Equal(FileOutcome.Resumed, r2.Outcome);
        Assert.Equal(3, store.Scalar<long>("SELECT count(*) FROM event"));
        Assert.Equal(3, store.Scalar<long>("SELECT count(DISTINCT event_id) FROM event"));
        Assert.Equal((Samples.IdRecord.Length, "file_0001.txt"), SplitEvent(store));

        // The file is rolled and gzipped under the same Index, with one more event.
        lines.Add(Samples.Exit);
        File.Delete(log);
        var gz = WriteLog("Dfsr00001.log.gz", head + string.Join("\r\n", lines) + "\r\n", gzip: true);

        var r3 = Assert.Single(importer.Import([gz]));
        Assert.Equal(FileOutcome.Resumed, r3.Outcome);
        Assert.Equal(4, store.Scalar<long>("SELECT count(*) FROM event"));
        var file = Assert.Single(store.ListFiles());
        Assert.True(file.IsComplete);
        Assert.Equal(4, file.EventCount);

        var r4 = Assert.Single(importer.Import([gz]));
        Assert.Equal(FileOutcome.Unchanged, r4.Outcome);
        Assert.Equal(4, store.Scalar<long>("SELECT count(*) FROM event"));
    }

    [Fact]
    public void A_later_index_marks_earlier_files_complete()
    {
        var first = WriteLog("Dfsr00001.log", Header(1, 1) + Samples.SubmitUpdateSent + "\r\n", gzip: false);
        using var store = CaseStore.Open(CaseDir);
        var importer = new Importer(store);
        importer.Import([first]);
        Assert.False(store.ListFiles()[0].IsComplete);

        var second = WriteLog("Dfsr00002.log", Header(1, 2) + Samples.RawGet + "\r\n", gzip: false);
        importer.Import([second]);
        var files = store.ListFiles();
        Assert.True(files[0].IsComplete);
        Assert.False(files[1].IsComplete);
    }

    [Fact]
    public void Stores_utc_and_local_time()
    {
        var log = WriteLog("Dfsr00001.log.gz", Header(1, 1) + Samples.CreateNewRecord + "\r\n" + string.Join("\r\n", Samples.IdRecord) + "\r\n", gzip: true);
        using var store = CaseStore.Open(CaseDir);
        new Importer(store).Import([log]);
        Assert.Equal(new DateTime(2026, 11, 11, 0, 47, 5, 743), store.Scalar<DateTime>("SELECT ts_utc FROM event"));
        Assert.Equal(new DateTime(2026, 11, 11, 11, 47, 5, 743), store.Scalar<DateTime>("SELECT ts_local FROM event"));
    }

    [Fact]
    public void Server_name_override_files_logs_under_the_given_name()
    {
        var log = WriteLog("Dfsr00001.log.gz", Header(1, 1, "WIN-OLDNAME") + Samples.SubmitUpdateSent + "\r\n", gzip: true);
        using var store = CaseStore.Open(CaseDir);
        new Importer(store).Import([log], serverName: "DFSRDC01");
        Assert.Equal("DFSRDC01", store.Scalar<string>("SELECT name FROM server"));
        Assert.Equal("WIN-OLDNAME", store.Scalar<string>("SELECT alias FROM server_alias"));
    }

    [Fact]
    public void Event_ids_encode_file_and_line()
    {
        Assert.Equal((3L << 32) | 17, CaseStore.EventId(3, 17));
    }
}
