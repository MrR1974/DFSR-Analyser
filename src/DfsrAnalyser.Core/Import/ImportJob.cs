using System.Globalization;
using DfsrAnalyser.Core.Collection;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Core.Import;

/// <summary>How imported files are assigned to servers.</summary>
public enum ServerNaming
{
    /// <summary>The <c>Computer:</c> name in each file's header.</summary>
    Header,
    /// <summary>The first folder under the chosen folder (or a picked file's own folder): <c>&lt;root&gt;\&lt;server&gt;\…\Dfsr*.log</c>.</summary>
    FolderName,
    /// <summary>One name for every file.</summary>
    Fixed,
}

public sealed record ImportOptions(ServerNaming Naming = ServerNaming.Header, string? FixedName = null, bool DropPlumbingDetail = false);

public sealed record ServerImportSummary(string Server, int Imported, int Resumed, int Unchanged, int Failed, long Events, int MinIndex, int MaxIndex, int Duplicates = 0);

public sealed record ImportJobResult(IReadOnlyList<FileImportResult> Files, IReadOnlyList<ServerImportSummary> Servers,
    IReadOnlyList<string> Warnings, string? LogPath, TimeSpan Elapsed)
{
    public int FailedCount => Files.Count(f => f.Outcome == FileOutcome.Failed);
    public long Events => Files.Sum(f => (long)f.Events);
}

/// <summary>
/// A manual import as the app and CLI run it: find the files under the chosen paths, assign servers,
/// warn about naming surprises, import, and write everything to <c>&lt;case&gt;\logs\import-*.log</c>
/// so a run can be reviewed (or pasted back) afterwards.
/// </summary>
public static class ImportJob
{
    /// <summary>Finds the log files under <paramref name="paths"/> and assigns each a server per <paramref name="options"/>.</summary>
    public static List<ImportItem> ResolveItems(IReadOnlyList<string> paths, ImportOptions options)
    {
        var items = new List<ImportItem>();
        foreach (var p in paths)
        {
            var isFolder = Directory.Exists(p);
            var root = isFolder ? Path.GetFullPath(p) : null;
            foreach (var file in Importer.FindLogFiles([p]))
                items.Add(new ImportItem(file, options.Naming switch
                {
                    ServerNaming.Fixed => string.IsNullOrWhiteSpace(options.FixedName) ? throw new ArgumentException("A server name is required.") : options.FixedName.Trim(),
                    ServerNaming.FolderName => FolderServer(root, file),
                    _ => null,
                }));
        }
        return items.DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// For a folder import, the first path segment under the root (<c>root\DC01\debug\Dfsr1.log</c> → DC01);
    /// a file directly in the root, or a picked file, uses its own folder's name.
    /// </summary>
    public static string FolderServer(string? root, string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (root is not null)
        {
            var rel = Path.GetRelativePath(root, dir);
            if (rel != "." && !rel.StartsWith("..", StringComparison.Ordinal))
                return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        }
        return Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : dir;
    }

    /// <summary>Naming surprises worth telling the analyst about before (and after) importing.</summary>
    public static List<string> Warnings(IReadOnlyList<ImportPreviewItem> preview)
    {
        var warnings = new List<string>();
        foreach (var g in preview.Where(p => p.Status != PreviewStatus.Unreadable && p.HeaderComputer is { Length: > 0 })
                     .GroupBy(p => p.Server, StringComparer.OrdinalIgnoreCase))
        {
            var computers = g.Select(p => p.HeaderComputer!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var others = computers.Where(c => !SameComputer(c, g.Key)).ToList();
            if (computers.Count > 1)
                warnings.Add($"Server '{g.Key}' gets logs from {computers.Count} computers ({string.Join(", ", computers)}). " +
                             "Fine after a rename; otherwise import those logs under separate names.");
            else if (others.Count > 0)
                warnings.Add($"Server '{g.Key}': the log headers say Computer:{others[0]}.");
            foreach (var dup in g.GroupBy(p => p.Index).Where(d => d.Count() > 1))
                warnings.Add($"Server '{g.Key}': Index {dup.Key} appears in {dup.Count()} files ({string.Join(", ", dup.Select(d => Path.GetFileName(d.Path)))}); " +
                             "the case keeps one copy of each log.");
        }
        foreach (var bad in preview.Where(p => p.Status == PreviewStatus.Unreadable))
            warnings.Add($"{bad.Path}: {bad.Error}");
        return warnings;
    }

    /// <summary>Header names are NetBIOS names; folder or chosen names may be FQDNs or differ in case.</summary>
    private static bool SameComputer(string header, string server) =>
        string.Equals(header, server, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(header, server.Split('.')[0], StringComparison.OrdinalIgnoreCase);

    public static ImportJobResult Run(CaseStore store, IReadOnlyList<string> paths, ImportOptions options,
        IProgress<ImportProgress>? progress = null, CancellationToken ct = default, bool writeLog = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var log = new CollectionLog(writeLog ? Path.Combine(store.Folder, "logs") : null, "import");
        log.Info($"Case {store.Folder}.");
        log.Info($"Paths: {string.Join(" | ", paths)}.");
        log.Info($"Server names: {options.Naming switch { ServerNaming.FolderName => "from sub-folder names", ServerNaming.Fixed => $"'{options.FixedName}' for every file", _ => "from log headers" }}" +
                 $"; drop Plumbing detail: {(options.DropPlumbingDetail ? "yes" : "no")}.");

        List<ImportItem> items;
        try { items = ResolveItems(paths, options); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            log.Error("Could not list the files", null, ex);
            throw;
        }
        log.Info($"{items.Count} log file(s) found.");

        var importer = new Importer(store) { DropPlumbingDetail = options.DropPlumbingDetail };
        var preview = importer.Preview(items);
        foreach (var p in preview)
            log.Info(p.Status == PreviewStatus.Unreadable
                    ? $"{p.Path}: unreadable ({p.Error})"
                    : $"{Path.GetFileName(p.Path)}: Index {p.Index}, Sequence {p.Sequence}, header {p.HeaderComputer}, first event {Utc(p.FirstEventUtc)}, " +
                      $"last written {Utc(p.LastWriteUtc)}, {p.SizeBytes:N0} bytes, {Describe(p.Status)}. {p.Path}",
                p.Server);
        var warnings = Warnings(preview);
        foreach (var w in warnings) log.Warn(w);

        List<FileImportResult> results;
        try
        {
            results = importer.Import(items, progress, ct);
        }
        catch (OperationCanceledException)
        {
            log.Warn("Import cancelled. Files already committed are kept; importing again resumes the rest.");
            throw;
        }
        catch (Exception ex)
        {
            log.Error("Import failed", null, ex);
            throw;
        }

        foreach (var r in results)
        {
            var msg = $"{Path.GetFileName(r.Path)}: {r.Outcome}, Index {r.Index}, {r.Events:N0} events, {r.Lines:N0} lines, {r.Elapsed.TotalSeconds:F1} s";
            if (r.Outcome == FileOutcome.Failed) log.Error($"{msg}: {r.Error}", r.Server);
            else if (r.Outcome == FileOutcome.Duplicate) log.Warn($"{msg}: skipped, {r.Error}", r.Server);
            else log.Info(msg, r.Server);
        }

        var summaries = results.GroupBy(r => r.Server, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ok = g.Where(r => r.Outcome is not (FileOutcome.Failed or FileOutcome.Duplicate)).ToList();
                return new ServerImportSummary(g.Key,
                    g.Count(r => r.Outcome == FileOutcome.Imported), g.Count(r => r.Outcome == FileOutcome.Resumed),
                    g.Count(r => r.Outcome == FileOutcome.Unchanged), g.Count(r => r.Outcome == FileOutcome.Failed),
                    g.Sum(r => (long)r.Events), ok.Count > 0 ? ok.Min(r => r.Index) : 0, ok.Count > 0 ? ok.Max(r => r.Index) : 0,
                    g.Count(r => r.Outcome == FileOutcome.Duplicate));
            }).ToList();
        foreach (var s in summaries) log.Info(SummaryLine(s), s.Server);
        log.Info($"Done in {sw.Elapsed.TotalSeconds:F1} s: {results.Sum(r => (long)r.Events):N0} events, {results.Count(r => r.Outcome == FileOutcome.Failed)} failed file(s).");
        return new ImportJobResult(results, summaries, warnings, log.Path, sw.Elapsed);
    }

    public static string SummaryLine(ServerImportSummary s) =>
        $"{s.Imported} imported, {s.Resumed} resumed, {s.Unchanged} unchanged, {s.Failed} failed" +
        (s.Duplicates > 0 ? $", {s.Duplicates} duplicate" : "") + $"; {s.Events:N0} events" +
        (s.MaxIndex > 0 ? $"; Index {s.MinIndex}–{s.MaxIndex}" : "");

    public static string Describe(PreviewStatus s) => s switch
    {
        PreviewStatus.New => "new",
        PreviewStatus.Resume => "in case, will resume",
        PreviewStatus.InCase => "already in case",
        _ => "unreadable",
    };

    private static string Utc(DateTime? t) =>
        t is { } v ? v.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "?";
}
