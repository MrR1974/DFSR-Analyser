using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DfsrAnalyser.App.Services;
using DfsrAnalyser.Core.Import;

namespace DfsrAnalyser.App.ViewModels;

/// <summary>One file in the import preview.</summary>
public sealed record ImportPreviewRow(string Server, string File, int Index, int Sequence, string Computer, string FirstEvent,
    string LastWritten, string Size, string Status, string Folder, bool IsProblem);

/// <summary>
/// The Import dialog: choose how files map to servers and whether to keep Plumbing detail, and see what will
/// happen to each file (new / resume / already in case / unreadable) before anything is written.
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly CaseSession _session;
    private int _scan;

    public ImportViewModel(CaseSession session, IReadOnlyList<string> paths)
    {
        _session = session;
        Paths = paths;
        PathsText = paths.Count == 1 ? paths[0] : $"{paths.Count} files in {string.Join(", ", paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))}";
        // Suggested single name: the chosen folder's name, or the picked files' folder.
        var first = paths.Count > 0 ? Path.GetFullPath(paths[0]).TrimEnd(Path.DirectorySeparatorChar) : "";
        _fixedName = Path.GetFileName(Directory.Exists(first) ? first : Path.GetDirectoryName(first)) ?? "";
    }

    public IReadOnlyList<string> Paths { get; }
    public string PathsText { get; }
    public ObservableCollection<ImportPreviewRow> Rows { get; } = [];
    public ObservableCollection<string> ServerSummaries { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] private bool _useHeaderNames = true;
    [ObservableProperty] private bool _useFolderNames;
    [ObservableProperty] private bool _useFixedName;
    [ObservableProperty] private string _fixedName;
    [ObservableProperty] private bool _dropPlumbingDetail;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _summaryText = "Scanning…";
    [ObservableProperty] private bool _canImport;

    public ImportOptions Options => new(
        UseFolderNames ? ServerNaming.FolderName : UseFixedName ? ServerNaming.Fixed : ServerNaming.Header,
        FixedName, DropPlumbingDetail);

    partial void OnUseHeaderNamesChanged(bool value) { if (value) RescanAsync().Forget(); }
    partial void OnUseFolderNamesChanged(bool value) { if (value) RescanAsync().Forget(); }
    partial void OnUseFixedNameChanged(bool value) { if (value) RescanAsync().Forget(); }
    partial void OnFixedNameChanged(string value) { if (UseFixedName) RescanAsync(debounce: true).Forget(); }

    /// <summary>Re-reads every file's header with the current naming; stale scans are dropped.</summary>
    public async Task RescanAsync(bool debounce = false)
    {
        var scan = ++_scan;
        if (debounce) await Task.Delay(300);
        if (scan != _scan) return;
        if (UseFixedName && string.IsNullOrWhiteSpace(FixedName))
        {
            Rows.Clear();
            ServerSummaries.Clear();
            Warnings.Clear();
            SummaryText = "Type the server name to file these logs under.";
            CanImport = false;
            return;
        }

        IsScanning = true;
        SummaryText = "Scanning…";
        var options = Options;
        try
        {
            var preview = await _session.RunAsync((store, _) => new Importer(store).Preview(ImportJob.ResolveItems(Paths, options)));
            if (scan != _scan) return;

            Rows.Clear();
            foreach (var p in preview)
                Rows.Add(new ImportPreviewRow(p.Server, Path.GetFileName(p.Path), p.Index, p.Sequence, p.HeaderComputer ?? "",
                    p.FirstEventUtc is { } first ? TimeDisplay.Full(first)[..19] : "",
                    p.LastWriteUtc == default ? "" : TimeDisplay.Full(p.LastWriteUtc)[..19],
                    p.SizeBytes >= 1 << 20 ? $"{p.SizeBytes / 1048576.0:N1} MB" : $"{p.SizeBytes / 1024.0:N0} KB",
                    p.Status == PreviewStatus.Unreadable ? "unreadable: " + p.Error : ImportJob.Describe(p.Status),
                    Path.GetDirectoryName(p.Path) ?? "", p.Status == PreviewStatus.Unreadable));

            ServerSummaries.Clear();
            foreach (var g in preview.Where(p => p.Status != PreviewStatus.Unreadable).GroupBy(p => p.Server, StringComparer.OrdinalIgnoreCase))
            {
                var firsts = g.Where(p => p.FirstEventUtc is not null).Select(p => p.FirstEventUtc!.Value).ToList();
                var span = firsts.Count == 0 ? "" : $", first event {TimeDisplay.Full(firsts.Min())[..16]}, last written {TimeDisplay.Full(g.Max(p => p.LastWriteUtc))[..16]} {TimeDisplay.ZoneLabel}";
                ServerSummaries.Add($"{g.Key}: {g.Count()} file(s), Index {g.Min(p => p.Index)}–{g.Max(p => p.Index)}{span} — " +
                                    $"{g.Count(p => p.Status == PreviewStatus.New)} new, {g.Count(p => p.Status == PreviewStatus.Resume)} to resume, " +
                                    $"{g.Count(p => p.Status == PreviewStatus.InCase)} already in the case");
            }

            Warnings.Clear();
            foreach (var w in ImportJob.Warnings(preview)) Warnings.Add(w);

            var importable = preview.Count(p => p.Status is PreviewStatus.New or PreviewStatus.Resume);
            SummaryText = preview.Count == 0
                ? "No Dfsr*.log or Dfsr*.log.gz files found."
                : $"{preview.Count} file(s) for {ServerSummaries.Count} server(s); {importable} to import.";
            CanImport = importable > 0;
        }
        catch (Exception ex)
        {
            if (scan != _scan) return;
            Rows.Clear();
            SummaryText = "Could not read the files: " + ex.Message;
            CanImport = false;
        }
        finally
        {
            if (scan == _scan) IsScanning = false;
        }
    }
}
