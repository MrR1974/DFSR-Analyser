using System.IO.Compression;
using System.Text;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Tests;

/// <summary>
/// Phase 6a: import naming and diagnostics, import order independence, and the verify / compare checks
/// used to prove that importing and collecting the same logs give the same case.
/// </summary>
public sealed class ImportVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dfsra-iv-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ImportVerificationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Header(int seq, int idx, string computer) =>
        $"* DFSR Log Sequence:{seq} Index:{idx} Computer:{computer} TimeZone:AUS Eastern Daylight Time (GMT+11:00) (TzId>AUS Eastern Standard Time<dIzT) Build:[Jan 01 2016 08:00:00 built by: WinBuild (FRE)]\r\n" +
        @"* Configuration logLevel:4 maxEntryCount:200000 maxFileCount:1000 logPath:\\.\C:\Windows\debug\" + "\r\n";

    private static string Write(string path, string content, bool gzip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(content);
        if (!gzip) File.WriteAllBytes(path, bytes);
        else
        {
            using var fs = File.Create(path);
            using var gz = new GZipStream(fs, CompressionLevel.Fastest);
            gz.Write(bytes);
        }
        return path;
    }

    /// <summary>
    /// Four logs for one server: Index 1 (service start), 2–3 rolled (3 opens with the tail of 2's last ID record),
    /// and 4, the active log. Returns the paths in Index order.
    /// </summary>
    private List<string> WriteServer(string folder, string computer = "DFSRDC01")
    {
        var dir = Path.Combine(_root, folder);
        // Sample lines come from different moments; restamp dated lines one second apart so time runs forward.
        var clock = 0;
        var lines = (params string[] l) => string.Join("\r\n", l.Select(x => x.Length > 21 && char.IsDigit(x[0])
            ? new DateTime(2026, 11, 11, 13, 0, 0).AddSeconds(clock++).ToString("yyyyMMdd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + x[21..]
            : x)) + "\r\n";
        return
        [
            Write(Path.Combine(dir, "Dfsr00001.log.gz"), Header(1, 1, computer) + lines(Samples.AddContentSet, Samples.SubmitUpdateSent, Samples.WarnLine), true),
            Write(Path.Combine(dir, "Dfsr00002.log.gz"), Header(1, 2, computer) +
                lines([Samples.RawGet, Samples.CreateNewRecord, .. Samples.IdRecord[..7]]), true),
            Write(Path.Combine(dir, "Dfsr00003.log.gz"), Header(2, 3, computer) +
                lines([.. Samples.IdRecord[7..], Samples.MeetInstall, Samples.TombstoneOrDelete, .. Samples.UsnRecord]), true),
            Write(Path.Combine(dir, "Dfsr00004.log"), Header(3, 4, computer) +
                lines(Samples.EventLogReport, Samples.SubmitUpdateSent, Samples.ErrorLine, Samples.ErrorStack[0], Samples.ErrorStack[1], Samples.Exit), false),
        ];
    }

    private CaseStore NewCase(string name) => CaseStore.Open(Path.Combine(_root, "cases", name));

    // ------------------------------------------------------------------ server naming

    [Fact]
    public void Folder_naming_uses_the_first_folder_under_the_root()
    {
        var root = Path.Combine(_root, "copied");
        Assert.Equal("DC01", ImportJob.FolderServer(root, Path.Combine(root, "DC01", "Dfsr00001.log")));
        Assert.Equal("DC01", ImportJob.FolderServer(root, Path.Combine(root, "DC01", "debug", "Dfsr00001.log")));
        // A file directly in the root, or a picked file, uses its own folder.
        Assert.Equal("copied", ImportJob.FolderServer(root, Path.Combine(root, "Dfsr00001.log")));
        Assert.Equal("MEMBER01", ImportJob.FolderServer(null, Path.Combine(_root, "MEMBER01", "Dfsr00001.log")));
    }

    [Fact]
    public void Import_job_files_logs_by_folder_and_warns_about_header_names()
    {
        WriteServer(Path.Combine("copied", "DC01-renamed"));
        WriteServer(Path.Combine("copied", "MEMBER01", "debug"), "DFSRMEMBER01");
        using var store = NewCase("naming");

        var job = ImportJob.Run(store, [Path.Combine(_root, "copied")], new ImportOptions(ServerNaming.FolderName), ct: Ct);

        Assert.Equal(["DC01-renamed", "MEMBER01"], job.Servers.Select(s => s.Server));
        Assert.All(job.Servers, s => Assert.Equal((4, 0, 1, 4), (s.Imported, s.Failed, s.MinIndex, s.MaxIndex)));
        Assert.Contains(job.Warnings, w => w.Contains("DC01-renamed") && w.Contains("Computer:DFSRDC01"));
        Assert.Contains(job.Warnings, w => w.Contains("MEMBER01") && w.Contains("Computer:DFSRMEMBER01"));
        Assert.NotNull(job.LogPath);
        var log = File.ReadAllText(job.LogPath!);
        Assert.Contains("from sub-folder names", log);
        Assert.Contains("[DC01-renamed] Dfsr00003.log.gz: Imported, Index 3", log);
        Assert.Contains("[MEMBER01] 4 imported, 0 resumed, 0 unchanged, 0 failed", log);
    }

    [Fact]
    public void Header_naming_matches_fqdn_folder_names_without_warning()
    {
        WriteServer(Path.Combine("fq", "dfsrdc01.dfsrtest.com"));
        using var store = NewCase("fq");
        var preview = new Importer(store).Preview(ImportJob.ResolveItems([Path.Combine(_root, "fq")], new ImportOptions(ServerNaming.FolderName)));
        Assert.Empty(ImportJob.Warnings(preview));
        Assert.All(preview, p => Assert.Equal(PreviewStatus.New, p.Status));
        Assert.Equal(new DateTime(2026, 11, 11, 2, 0, 0, DateTimeKind.Utc), preview[0].FirstEventUtc); // 13:00:00 AEDT
    }

    [Fact]
    public void Preview_reports_case_status_without_writing()
    {
        var files = WriteServer("prev");
        using var store = NewCase("prev");
        new Importer(store).Import(files[..2], ct: Ct);
        var before = store.Scalar<long>("SELECT count(*) FROM log_file");

        var preview = new Importer(store).Preview(files.Select(f => new ImportItem(f)));

        Assert.Equal([PreviewStatus.InCase, PreviewStatus.InCase, PreviewStatus.New, PreviewStatus.New], preview.Select(p => p.Status));
        Assert.Equal(before, store.Scalar<long>("SELECT count(*) FROM log_file"));
    }

    [Fact]
    public void Bad_files_fail_alone_and_are_logged()
    {
        var files = WriteServer(Path.Combine("bad", "DC01"));
        var dir = Path.GetDirectoryName(files[0])!;
        File.WriteAllText(Path.Combine(dir, "Dfsr09999.log"), "this is not a DFSR log\r\n");
        File.WriteAllBytes(Path.Combine(dir, "Dfsr09998.log"), []);
        // A later log whose copy was cut off half way (an interrupted transfer).
        var big = Write(Path.Combine(_root, "whole", "Dfsr00005.log.gz"), Header(1, 5, "DFSRDC01") +
            string.Concat(Enumerable.Range(0, 2000).Select(i => $"20261111 14:{i / 60 % 60:00}:{i % 60:00}.000 4444 JOIN  1253 Join::SubmitUpdate Sent: name:f{i}.txt\r\n")), true);
        var gz = File.ReadAllBytes(big);
        File.WriteAllBytes(Path.Combine(dir, "Dfsr00005.log.gz"), gz[..(gz.Length / 2)]);
        // The same log twice (as when a folder was copied twice): one copy is used.
        File.Copy(files[1], Path.Combine(dir, "Dfsr00002 - Copy.log.gz"));
        using var store = NewCase("bad");

        var job = ImportJob.Run(store, [Path.Combine(_root, "bad")], new ImportOptions(ServerNaming.FolderName), ct: Ct);

        var outcomes = string.Join(" | ", job.Files.Select(f => $"{Path.GetFileName(f.Path)} {f.Outcome} {f.Error}"));
        Assert.True(job.FailedCount == 3, outcomes);
        Assert.Equal(4, job.Files.Count(f => f.Outcome == FileOutcome.Imported));
        Assert.Equal(1, job.Servers.Single().Duplicates);
        Assert.Contains(job.Files, f => f.Outcome == FileOutcome.Failed && f.Error!.Contains("truncated or damaged"));
        var log = File.ReadAllText(job.LogPath!);
        Assert.Contains("Dfsr09999.log: unreadable", log);
        Assert.Contains("Dfsr00005.log.gz: Failed", log);
        Assert.Contains("skipped, same log as", log);
        Assert.DoesNotContain(CaseIntegrity.Verify(store), f => f.Level == FindingLevel.Defect);
    }

    // ------------------------------------------------------------------ order independence

    [Fact]
    public void Import_order_does_not_change_the_case()
    {
        var files = WriteServer("order");
        using (var inOrder = NewCase("in-order")) new Importer(inOrder).Import(files, ct: Ct);

        // Newest first, one file per import: Index 3's opening lines wait for Index 2.
        using (var reversed = NewCase("reversed"))
        {
            foreach (var f in Enumerable.Reverse(files)) new Importer(reversed).Import([f], ct: Ct);
            AssertSame(reversed, "in-order");
        }

        using (var shuffled = NewCase("shuffled"))
        {
            foreach (var f in new[] { files[2], files[0], files[3], files[1] }) new Importer(shuffled).Import([f], ct: Ct);
            AssertSame(shuffled, "in-order");
        }
    }

    private void AssertSame(CaseStore store, string otherCase)
    {
        var findings = CaseIntegrity.Verify(store);
        Assert.True(findings.All(f => f.Level == FindingLevel.Info), string.Join(" | ", findings));
        var rows = CaseIntegrity.Compare(store, Path.Combine(_root, "cases", otherCase));
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.True(r.Match == FileMatch.Identical && r.DetailSame, $"Index {r.Index}: {r.Match} {r.Note}"));
    }

    // ------------------------------------------------------------------ verify and compare

    [Fact]
    public void Verify_reports_missing_index_and_waiting_fragment_as_warnings()
    {
        var files = WriteServer("gap");
        using var store = NewCase("gap");
        new Importer(store).Import([files[0], files[2], files[3]], ct: Ct);

        var findings = CaseIntegrity.Verify(store);

        Assert.True(findings.All(f => f.Level != FindingLevel.Defect), string.Join(" | ", findings));
        Assert.Contains(findings, f => f.Level == FindingLevel.Warning && f.Message.Contains("Index 2 not in the case"));
        Assert.Contains(findings, f => f.Level == FindingLevel.Warning && f.Message.Contains("opening lines of Index 3"));
    }

    [Fact]
    public void Verify_finds_duplicated_events()
    {
        var files = WriteServer("dup");
        using var store = NewCase("dup");
        new Importer(store).Import(files, ct: Ct);
        store.Execute("INSERT INTO event SELECT * FROM event WHERE file_id = (SELECT min(file_id) FROM event) LIMIT 1");

        var defects = CaseIntegrity.Verify(store).Where(f => f.Level == FindingLevel.Defect).Select(f => f.Message).ToList();

        Assert.Contains(defects, m => m.Contains("1 event id(s) occur more than once"));
        Assert.Contains(defects, m => m.Contains("records 3 events but holds 4"));
    }

    [Fact]
    public void Compare_accepts_an_active_log_copied_later_and_flags_real_differences()
    {
        var files = WriteServer("prefix");
        var active = files[3];
        var full = File.ReadAllText(active);
        using (var later = NewCase("later")) new Importer(later).Import(files, ct: Ct);

        // An earlier copy of the active log, cut after its second event.
        var cut = full[..(full.LastIndexOf('\n', full.IndexOf("[ERROR]", StringComparison.Ordinal)) + 1)];
        var earlyDir = Directory.CreateDirectory(Path.Combine(_root, "early")).FullName;
        var earlyFiles = new List<string>();
        foreach (var f in files[..3])
        {
            var copy = Path.Combine(earlyDir, Path.GetFileName(f));
            File.Copy(f, copy);
            earlyFiles.Add(copy);
        }
        earlyFiles.Add(Write(Path.Combine(earlyDir, "Dfsr00004.log"), cut, false));

        using var early = NewCase("early");
        new Importer(early).Import(earlyFiles, ct: Ct);
        var rows = CaseIntegrity.Compare(early, Path.Combine(_root, "cases", "later"));
        Assert.Equal([FileMatch.Identical, FileMatch.Identical, FileMatch.Identical, FileMatch.SamePrefix], rows.Select(r => r.Match));
        Assert.Equal(2, rows[3].CommonEvents);
        Assert.Contains("B was copied later", rows[3].Note);

        early.Execute("UPDATE event SET category = 'Changed' WHERE class_method = 'Join::SubmitUpdate'");
        rows = CaseIntegrity.Compare(early, Path.Combine(_root, "cases", "later"));
        Assert.Contains(rows, r => r.Match == FileMatch.Differs && r.ContentSame && !r.CategoriesSame);
    }
}
