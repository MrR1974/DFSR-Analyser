using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DfsrAnalyser.App.Services;
using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Query;
using DfsrAnalyser.Core.Storage;
using Microsoft.Win32;

namespace DfsrAnalyser.App.ViewModels;

public sealed record BucketChoice(string Label, TimeSpan? Size)
{
    public override string ToString() => Label;
}

public sealed record LevelChoice(string Label, Dimension? Dimension)
{
    public override string ToString() => Label;
}

/// <summary>
/// The main screen. Every filter change bumps a version number; query results from an older version
/// are discarded, so fast clicking never shows stale data.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const int EventPageSize = 500;

    private readonly AppSettings _settings = AppSettings.Load();
    private CaseSession? _session;
    private int _version;
    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _importCts;
    private TimeSpan _bucket = TimeSpan.FromMinutes(1);
    private double _rangeMinutes;
    private readonly Stack<(DateTime? From, DateTime? To)> _zoomHistory = new();

    public MainViewModel()
    {
        TimeDisplay.UseLocal = _settings.UseLocalTime;
        _useLocalTime = _settings.UseLocalTime;
        BucketChoices = [new BucketChoice("Auto", null), .. CaseQuery.BucketPresets.Select(b => new BucketChoice(TimeDisplay.Span(b), b))];
        _selectedBucket = BucketChoices[0];
        LevelChoices = [.. Dimensions.All.Select(d => new LevelChoice(d.Label, d))];
        OptionalLevelChoices = [new LevelChoice("(none)", null), .. LevelChoices];
        _level1 = LevelChoices.First(l => l.Dimension == Dimensions.Category);
        _level2 = OptionalLevelChoices.First(l => l.Dimension == Dimensions.Server);
        _level3 = OptionalLevelChoices.First(l => l.Dimension == Dimensions.FileName);
        _stackBy = Dimensions.Category;

        var expanded = new HashSet<Dimension> { Dimensions.Group, Dimensions.Category, Dimensions.Server, Dimensions.OriginServer, Dimensions.Connection };
        foreach (var d in Dimensions.All)
            Facets.Add(new FacetViewModel(d, this, expanded.Contains(d)));
        foreach (var r in _settings.RecentCases) RecentCases.Add(r);
    }

    // ------------------------------------------------------------------ bindable state

    public ObservableCollection<FacetViewModel> Facets { get; } = [];
    public ObservableCollection<TreeRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> RecentCases { get; } = [];
    public IReadOnlyList<Dimension> AllDimensions => Dimensions.All;
    public IReadOnlyList<BucketChoice> BucketChoices { get; }
    public IReadOnlyList<LevelChoice> LevelChoices { get; }
    public IReadOnlyList<LevelChoice> OptionalLevelChoices { get; }
    public Palette Palette { get; } = new();

    /// <summary>Latest chart data; <see cref="SeriesChanged"/> fires when it is replaced.</summary>
    public TimeSeries? Series { get; private set; }
    public event EventHandler? SeriesChanged;

    [ObservableProperty] private string _title = "DFSR Analyser";
    [ObservableProperty] private bool _isCaseOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private bool _isQuerying;
    [ObservableProperty] private EventFilter _filter = new();
    [ObservableProperty] private BucketChoice _selectedBucket;
    [ObservableProperty] private Dimension _stackBy;
    [ObservableProperty] private LevelChoice _level1;
    [ObservableProperty] private LevelChoice _level2;
    [ObservableProperty] private LevelChoice _level3;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _useLocalTime;
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private string _fileNamePatternText = "";
    [ObservableProperty] private string _timeRangeText = "";

    // Typed time range (display zone). Re-filled from the effective range after every refresh.
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private string _fromTime = "";
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _toTime = "";
    [ObservableProperty] private string _rangeError = "";
    [ObservableProperty] private string _caseSpanText = "";

    /// <summary>First and last event of the whole case (hidden categories included), UTC.</summary>
    private (DateTime Min, DateTime Max)? _caseExtent;
    [ObservableProperty] private string _bucketText = "";
    [ObservableProperty] private string _statusText = "Open or create a case to begin (File menu).";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private TreeRowViewModel? _selectedRow;

    public bool CanZoomOut => _zoomHistory.Count > 0;
    public bool HasTimeFilter => Filter.FromUtc is not null || Filter.ToUtc is not null;

    private IReadOnlyList<Dimension> Levels =>
        new[] { Level1.Dimension, Level2.Dimension, Level3.Dimension }.TakeWhile(d => d is not null).Select(d => d!).Distinct().ToList();

    // ------------------------------------------------------------------ change handlers

    partial void OnFilterChanged(EventFilter value)
    {
        OnPropertyChanged(nameof(HasTimeFilter));
        RequestRefresh();
    }

    partial void OnSelectedBucketChanged(BucketChoice value) => RequestRefresh();
    partial void OnStackByChanged(Dimension value) => RequestRefresh();
    partial void OnLevel1Changed(LevelChoice value) => RequestRefresh();
    partial void OnLevel2Changed(LevelChoice value) => RequestRefresh();
    partial void OnLevel3Changed(LevelChoice value) => RequestRefresh();
    partial void OnShowHiddenChanged(bool value) => Filter = Filter with { ShowHidden = value };

    partial void OnUseLocalTimeChanged(bool value)
    {
        TimeDisplay.UseLocal = value;
        _settings.UseLocalTime = value;
        _settings.Save();
        RequestRefresh();
    }

    partial void OnMessageTextChanged(string value) =>
        Filter = Filter with { MessageText = string.IsNullOrWhiteSpace(value) ? null : value.Trim() };

    partial void OnFileNamePatternTextChanged(string value)
    {
        var pattern = FileNamePattern.Parse(value);
        Filter = Filter with { FileNamePattern = pattern.IsEmpty ? null : pattern.ToString() };
    }

    partial void OnFromDateChanged(DateTime? value) => RangeError = "";
    partial void OnFromTimeChanged(string value) => RangeError = "";
    partial void OnToDateChanged(DateTime? value) => RangeError = "";
    partial void OnToTimeChanged(string value) => RangeError = "";

    partial void OnSelectedRowChanged(TreeRowViewModel? value)
    {
        if (value is null) return;
        if (value.IsEvent) ShowDetailAsync(value.EventId).Forget();
        else if (value.IsMore) LoadMoreAsync(value).Forget();
    }

    // ------------------------------------------------------------------ case lifecycle

    [RelayCommand]
    private async Task NewCase()
    {
        var dlg = new OpenFolderDialog { Title = "Choose an empty folder for the new case" };
        if (dlg.ShowDialog() != true) return;
        if (Directory.EnumerateFileSystemEntries(dlg.FolderName).Any() && !File.Exists(Path.Combine(dlg.FolderName, CaseStore.DatabaseFileName)))
        {
            if (MessageBox.Show("The folder is not empty. Create the case there anyway?", "New case",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        await OpenCaseAsync(dlg.FolderName);
    }

    [RelayCommand]
    private async Task OpenCase()
    {
        var dlg = new OpenFolderDialog { Title = "Open a case folder" };
        if (dlg.ShowDialog() != true) return;
        if (!File.Exists(Path.Combine(dlg.FolderName, CaseStore.DatabaseFileName)))
        {
            MessageBox.Show($"No {CaseStore.DatabaseFileName} in that folder. Use File > New case to create one.", "Open case",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await OpenCaseAsync(dlg.FolderName);
    }

    [RelayCommand]
    private Task OpenRecent(string folder) => OpenCaseAsync(folder);

    public async Task OpenCaseAsync(string folder)
    {
        try
        {
            IsBusy = true;
            BusyText = "Opening case…";
            _session?.Dispose();
            _session = await CaseSession.OpenAsync(folder);
            _settings.AddRecent(_session.Folder);
            RecentCases.Clear();
            foreach (var r in _settings.RecentCases) RecentCases.Add(r);
            Title = $"{_session.Name} — DFSR Analyser";
            IsCaseOpen = true;
            _zoomHistory.Clear();
            OnPropertyChanged(nameof(CanZoomOut));
            LoadProfileNames();
            Filter = new EventFilter { ShowHidden = ShowHidden };
        }
        catch (Exception ex)
        {
            ShowError("Could not open the case", ex);
            return;
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    public void Close()
    {
        _importCts?.Cancel();
        _session?.Dispose();
        _session = null;
    }

    // ------------------------------------------------------------------ import

    [RelayCommand]
    private async Task ImportFiles()
    {
        if (!await EnsureCaseAsync()) return;
        var dlg = new OpenFileDialog
        {
            Title = "Import DFSR debug logs",
            Filter = "DFSR debug logs (Dfsr*.log;Dfsr*.log.gz)|Dfsr*.log;Dfsr*.log.gz|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) await ConfirmAndImportAsync(dlg.FileNames);
    }

    [RelayCommand]
    private async Task ImportFolder()
    {
        if (!await EnsureCaseAsync()) return;
        var dlg = new OpenFolderDialog { Title = "Import every Dfsr*.log / Dfsr*.log.gz under a folder (one sub-folder per server is fine)" };
        if (dlg.ShowDialog() == true) await ConfirmAndImportAsync([dlg.FolderName]);
    }

    /// <summary>Shows the Import dialog (server naming, preview) and imports if the analyst confirms.</summary>
    private async Task ConfirmAndImportAsync(IReadOnlyList<string> paths)
    {
        if (_session is null) return;
        var vm = new ImportViewModel(_session, paths);
        if (new ImportWindow(vm) { Owner = Application.Current.MainWindow }.ShowDialog() == true)
            await ImportAsync(paths, vm.Options);
    }

    private async Task<bool> EnsureCaseAsync()
    {
        if (_session is not null) return true;
        MessageBox.Show("Create or open a case first (File menu).", "DFSR Analyser", MessageBoxButton.OK, MessageBoxImage.Information);
        await Task.CompletedTask;
        return false;
    }

    /// <param name="quiet">No message boxes (screenshot mode); the outcome still goes to the status bar and the import log.</param>
    public async Task ImportAsync(IReadOnlyList<string> paths, ImportOptions? options = null, bool quiet = false)
    {
        if (_session is null) return;
        options ??= new ImportOptions();
        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;
        IsBusy = true;
        BusyText = "Scanning for log files…";
        var progress = new Progress<ImportProgress>(p =>
            BusyText = $"Importing {Path.GetFileName(p.Path)}  ({p.FileNumber} of {p.FileCount})\n{p.EventsSoFar:N0} events so far");
        var sw = Stopwatch.StartNew();
        try
        {
            var job = await _session.RunAsync((store, _) => ImportJob.Run(store, paths, options, progress, ct), ct);
            var results = job.Files;
            LastImport = job;

            StatusText = $"Imported {job.Events:N0} events from {results.Count(r => r.Outcome is FileOutcome.Imported or FileOutcome.Resumed)} file(s) " +
                         $"({results.Count(r => r.Outcome == FileOutcome.Unchanged)} unchanged, {job.FailedCount} failed) in {sw.Elapsed.TotalSeconds:N1} s." +
                         (job.LogPath is null ? "" : $"  Log: {job.LogPath}");
            if (!quiet)
            {
                if (results.Count == 0)
                    MessageBox.Show("No Dfsr*.log or Dfsr*.log.gz files were found.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show(ImportSummary(job), job.FailedCount > 0 ? $"Import finished: {job.FailedCount} file(s) failed" : "Import finished",
                        MessageBoxButton.OK, job.FailedCount > 0 || job.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Import cancelled. Files already committed are kept; importing again resumes the rest.";
        }
        catch (Exception ex)
        {
            ShowError("Import failed", ex);
        }
        finally
        {
            IsBusy = false;
            _importCts = null;
        }
        await RefreshAsync();
    }

    /// <summary>The most recent import's outcome (screenshot mode reports it).</summary>
    internal ImportJobResult? LastImport { get; private set; }

    private static string ImportSummary(ImportJobResult job)
    {
        var sb = new StringBuilder();
        foreach (var s in job.Servers) sb.AppendLine($"{s.Server}: {ImportJob.SummaryLine(s)}");
        var failed = job.Files.Where(f => f.Outcome == FileOutcome.Failed).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine().AppendLine("Failed:");
            foreach (var f in failed.Take(15)) sb.AppendLine($"  {f.Path}: {f.Error}");
            if (failed.Count > 15) sb.AppendLine($"  … and {failed.Count - 15} more (see the log)");
        }
        if (job.Warnings.Count > 0)
        {
            sb.AppendLine().AppendLine("Warnings:");
            foreach (var w in job.Warnings.Take(10)) sb.AppendLine("  " + w);
        }
        if (job.LogPath is not null) sb.AppendLine().AppendLine("Details: " + job.LogPath);
        return sb.ToString();
    }

    [RelayCommand]
    private void Cancel() => _importCts?.Cancel();

    // ------------------------------------------------------------------ remote collection

    /// <summary>The open case (screenshot mode drives the Collect dialog with it).</summary>
    internal CaseSession? Session => _session;

    /// <summary>Names of the collection profiles saved in the open case (for Collect ▸ Top up).</summary>
    public ObservableCollection<string> ProfileNames { get; } = [];

    private void LoadProfileNames()
    {
        ProfileNames.Clear();
        if (_session is null) return;
        try
        {
            foreach (var p in Core.Collection.CaseSettings.Load(_session.Folder).Profiles) ProfileNames.Add(p.Name);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            StatusText = $"{Core.Collection.CaseSettings.FileName} could not be read: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenCollect() => ShowCollect(null);

    [RelayCommand]
    private void TopUp(string profile) => ShowCollect(profile);

    private void ShowCollect(string? profile)
    {
        if (_session is null)
        {
            MessageBox.Show("Create or open a case first (File menu).", "Collect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var collected = false;
        var vm = new CollectViewModel(_session, () => collected = true, profile);
        new CollectWindow(vm) { Owner = Application.Current.MainWindow }.ShowDialog();
        LoadProfileNames();
        if (collected) RefreshAsync().Forget();
    }

    [RelayCommand]
    private async Task ImportEventLogFile()
    {
        if (!await EnsureCaseAsync()) return;
        var dlg = new OpenFileDialog { Title = "Import an exported DFS Replication event log", Filter = "Event logs (*.evtx)|*.evtx", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        var guess = Path.GetFileNameWithoutExtension(dlg.FileNames[0]).Split('-', '_', ' ', '.')[0].ToUpperInvariant();
        var server = InputWindow.Ask(Application.Current.MainWindow, "Import event log", "Which server is this event log from?", guess);
        if (server is null) return;
        IsBusy = true;
        BusyText = "Importing event log…";
        try
        {
            var count = await _session!.RunAsync((store, _) =>
            {
                var source = new Collection.WindowsEventLogSource();
                return dlg.FileNames.Sum(f => store.ImportEventLog(server, source.ReadFile(f).ToList()));
            });
            StatusText = $"Imported {count:N0} event-log record(s) for {server}.";
        }
        catch (Exception ex)
        {
            ShowError("Event log import failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    // ------------------------------------------------------------------ rules

    [RelayCommand]
    private void EditRules()
    {
        if (_session is null) return;
        try { Process.Start(new ProcessStartInfo(_session.Store.RulesPath) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_session.Store.RulesPath}\""));
        }
    }

    [RelayCommand]
    private async Task ReapplyRules()
    {
        if (_session is null) return;
        IsBusy = true;
        BusyText = "Re-applying rules…";
        try
        {
            var sw = Stopwatch.StartNew();
            await _session.RunAsync((store, _) => store.ReloadAndApplyRules());
            StatusText = $"Rules re-applied in {sw.Elapsed.TotalSeconds:N1} s ({_session.Store.Rules.Rules.Count} rules, {_session.Store.Rules.Categories.Count} categories).";
        }
        catch (Exception ex)
        {
            ShowError("The rules could not be applied", ex);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    // ------------------------------------------------------------------ filters and time

    internal void ToggleFacetValue(string dimensionKey, string value) => Filter = Filter.Toggle(dimensionKey, value);

    [RelayCommand]
    private void ClearFilters()
    {
        MessageText = "";
        FileNamePatternText = "";
        Filter = Filter.ClearAll();
    }

    [RelayCommand]
    private void ClearFileNamePattern() => FileNamePatternText = "";

    /// <summary>
    /// Applies the typed From/To (display zone) as the time filter. Values outside the logs' span are pulled in
    /// to it; From must be before To. Goes through the zoom history like a chart selection.
    /// </summary>
    [RelayCommand]
    private void ApplyTimeRange()
    {
        if (_caseExtent is not { } extent) return;
        if (!TryReadTime(FromDate, FromTime, out var from) || !TryReadTime(ToDate, ToTime, out var to))
        {
            RangeError = "Enter a date and a time (HH:mm, HH:mm:ss or HH:mm:ss.fff) for both From and To.";
            return;
        }
        var (min, max) = SpanBounds(extent);
        if (to <= from)
        {
            RangeError = "From must be earlier than To.";
            return;
        }
        if (to <= min || from >= max)
        {
            RangeError = $"That range is outside the logs ({TimeDisplay.Full(min)[..19]} – {TimeDisplay.Full(max)[..19]} {TimeDisplay.ZoneLabel}).";
            return;
        }
        RangeError = "";
        from = from < min ? min : from;
        to = to > max ? max : to;
        if (from == Filter.FromUtc && to == Filter.ToUtc) return;
        SetTimeRange(from, to);
    }

    /// <summary>The last <paramref name="minutes"/> of the logs (relative to the latest event, not to now).</summary>
    [RelayCommand]
    private void LastOfLogs(string minutes)
    {
        if (_caseExtent is not { } extent) return;
        var (min, max) = SpanBounds(extent);
        var from = max - TimeSpan.FromMinutes(double.Parse(minutes, System.Globalization.CultureInfo.InvariantCulture));
        RangeError = "";
        SetTimeRange(from < min ? min : from, max);
    }

    /// <summary>The case span widened to whole seconds, so From/To round-trip through HH:mm:ss and include every event.</summary>
    private static (DateTime Min, DateTime Max) SpanBounds((DateTime Min, DateTime Max) extent) =>
        (FloorSecond(extent.Min), FloorSecond(extent.Max).AddSeconds(1));

    private static DateTime FloorSecond(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind);

    private static bool TryReadTime(DateTime? date, string time, out DateTime utc)
    {
        utc = default;
        if (date is not { } d) return false;
        var text = string.IsNullOrWhiteSpace(time) ? "00:00" : time.Trim();
        if (!TimeSpan.TryParseExact(text, [@"h\:mm", @"h\:mm\:ss", @"h\:mm\:ss\.FFF", @"hh\:mm", @"hh\:mm\:ss", @"hh\:mm\:ss\.FFF"],
                System.Globalization.CultureInfo.InvariantCulture, out var tod) || tod >= TimeSpan.FromDays(1))
            return false;
        utc = DateTime.SpecifyKind(TimeDisplay.FromDisplay(d.Date + tod), DateTimeKind.Utc);
        return true;
    }

    /// <summary>Shows the effective range in the From/To fields (display zone; milliseconds only when present).</summary>
    private void ShowRangeFields(DateTime fromUtc, DateTime toUtc)
    {
        static string Clock(DateTime t) => t.ToString(t.Millisecond == 0 ? "HH:mm:ss" : "HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var from = TimeDisplay.ToDisplay(fromUtc);
        var to = TimeDisplay.ToDisplay(toUtc);
        FromDate = from.Date;
        FromTime = Clock(from);
        ToDate = to.Date;
        ToTime = Clock(to);
        RangeError = "";
    }

    /// <summary>Restricts the view to a time range (from the chart). Remembers the previous range for Zoom out.</summary>
    public void SetTimeRange(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc) return;
        _zoomHistory.Push((Filter.FromUtc, Filter.ToUtc));
        OnPropertyChanged(nameof(CanZoomOut));
        Filter = Filter with { FromUtc = fromUtc, ToUtc = toUtc };
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (_zoomHistory.Count == 0) return;
        var (from, to) = _zoomHistory.Pop();
        OnPropertyChanged(nameof(CanZoomOut));
        Filter = Filter with { FromUtc = from, ToUtc = to };
    }

    [RelayCommand]
    private void ResetTime()
    {
        _zoomHistory.Clear();
        OnPropertyChanged(nameof(CanZoomOut));
        Filter = Filter with { FromUtc = null, ToUtc = null };
    }

    // ------------------------------------------------------------------ refresh

    private void RequestRefresh()
    {
        if (_session is null) return;
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        Task.Delay(150, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) RefreshAsync().Forget();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Re-runs every query for the current filter: chart, totals, grid root and expanded facets.</summary>
    public async Task RefreshAsync()
    {
        if (_session is null) return;
        var version = ++_version;
        var f = Filter;
        var stack = StackBy;
        var levels = Levels;
        var bucketChoice = SelectedBucket.Size;
        IsQuerying = true;
        try
        {
            var r = await _session.RunAsync((_, q) =>
            {
                var caseExtent = q.Extent(new EventFilter { ShowHidden = true });
                var extent = q.Extent(f);
                var from = f.FromUtc ?? extent?.Min ?? DateTime.UtcNow;
                var to = f.ToUtc ?? extent?.Max ?? from;
                var span = to - from;
                var bucket = bucketChoice ?? CaseQuery.AutoBucket(span < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : span);
                var series = q.Series(f, bucket, stack, topN: 8);
                var total = q.CountEvents(f);
                var groups = levels.Count > 0 ? q.Groups(f, [], levels[0], bucket) : [];
                return (extent, from, to, bucket, series, total, groups, caseExtent);
            });
            if (version != _version) return;

            _caseExtent = r.caseExtent;
            if (r.caseExtent is { } ce)
            {
                var (min, max) = SpanBounds(ce);
                CaseSpanText = $"logs span {TimeDisplay.Full(ce.Min)[..19]} – {TimeDisplay.Full(ce.Max)[..19]} ({TimeDisplay.Span(ce.Max - ce.Min)})";
                ShowRangeFields(f.FromUtc ?? min, f.ToUtc ?? max);
            }
            else
            {
                CaseSpanText = "no events in the case";
                FromDate = ToDate = null;
                FromTime = ToTime = "";
            }

            _bucket = r.bucket;
            _rangeMinutes = Math.Max((r.to - r.from).TotalMinutes, 1.0 / 60);
            TimeRangeText = r.extent is null
                ? "No events"
                : $"{TimeDisplay.Full(r.from)}  →  {TimeDisplay.Full(r.to)}  {TimeDisplay.ZoneLabel}  ({TimeDisplay.Span(r.to - r.from)})";
            BucketText = $"window {TimeDisplay.Span(r.bucket)}";
            StatusText = $"{r.total:N0} events in view" + (f.ShowHidden ? " (including hidden categories)" : "") +
                         (f.FileNamePattern is { } fp ? $"  ·  file name {fp}" : "") + $"  ·  {_session.Folder}";

            foreach (var s in r.series.Series) Palette.HexFor(s.Key);
            Series = r.series;
            SeriesChanged?.Invoke(this, EventArgs.Empty);

            Rows.Clear();
            SelectedRow = null;
            DetailText = "";
            if (levels.Count > 0)
            {
                foreach (var g in r.groups)
                    Rows.Add(TreeRowViewModel.Group(this, null, levels[0], g, _rangeMinutes, SwatchFor(levels[0], g.Value)));
                if (r.groups.Count == 0) Rows.Add(TreeRowViewModel.Note(this, null, "No events match the current filter."));
            }

            foreach (var facet in Facets.Where(x => x.IsExpanded))
            {
                await RefreshFacetAsync(facet);
                if (version != _version) return;
            }
        }
        catch (Exception ex)
        {
            if (version == _version) ShowError("Query failed", ex);
        }
        finally
        {
            if (version == _version) IsQuerying = false;
        }
    }

    internal async Task RefreshFacetAsync(FacetViewModel facet, bool debounce = false)
    {
        if (_session is null || !facet.IsExpanded) return;
        if (debounce) await Task.Delay(250);
        var version = _version;
        var f = Filter;
        var search = facet.Search;
        try
        {
            var values = await _session.RunAsync((_, q) => q.Facet(facet.Dimension, f, limit: 50, search: search));
            if (version == _version && search == facet.Search) facet.Apply(values);
        }
        catch (Exception ex)
        {
            ShowError($"Facet '{facet.Label}' failed", ex);
        }
    }

    private System.Windows.Media.Brush? SwatchFor(Dimension dim, string value) =>
        dim == StackBy && Palette.TryGet(value, out var hex) ? Palette.BrushFrom(hex) : null;

    // ------------------------------------------------------------------ tree grid

    internal async Task OnRowExpandedChanged(TreeRowViewModel row, bool expanded)
    {
        if (!expanded)
        {
            RemoveDescendants(row);
            return;
        }
        if (_session is null) return;
        var version = _version;
        var f = Filter;
        var levels = Levels;
        var depth = row.Path.Count;
        var bucket = _bucket;
        try
        {
            List<TreeRowViewModel> children;
            if (depth < levels.Count)
            {
                var dim = levels[depth];
                var groups = await _session.RunAsync((_, q) => q.Groups(f, row.Path, dim, bucket));
                children = groups.Select(g => TreeRowViewModel.Group(this, row, dim, g, _rangeMinutes, SwatchFor(dim, g.Value))).ToList();
            }
            else
            {
                var (events, total) = await _session.RunAsync((_, q) => (q.Events(f, row.Path, 0, EventPageSize), q.CountEvents(f, row.Path)));
                children = events.Select(e => TreeRowViewModel.Event(this, row, e)).ToList();
                if (total > events.Count) children.Add(TreeRowViewModel.More(this, row, events.Count, total - events.Count));
            }
            if (version != _version || !row.IsExpanded) return;
            var at = Rows.IndexOf(row);
            if (at < 0) return;
            for (var i = 0; i < children.Count; i++) Rows.Insert(at + 1 + i, children[i]);
        }
        catch (Exception ex)
        {
            ShowError("Could not expand the row", ex);
        }
    }

    private void RemoveDescendants(TreeRowViewModel row)
    {
        var at = Rows.IndexOf(row);
        if (at < 0) return;
        while (at + 1 < Rows.Count && row.IsAncestorOf(Rows[at + 1])) Rows.RemoveAt(at + 1);
    }

    private async Task LoadMoreAsync(TreeRowViewModel more)
    {
        if (_session is null || more.Parent is null) return;
        var version = _version;
        var f = Filter;
        var parent = more.Parent;
        var (events, total) = await _session.RunAsync((_, q) => (q.Events(f, parent.Path, more.Offset, EventPageSize), q.CountEvents(f, parent.Path)));
        if (version != _version) return;
        var at = Rows.IndexOf(more);
        if (at < 0) return;
        Rows.RemoveAt(at);
        var i = at;
        foreach (var e in events) Rows.Insert(i++, TreeRowViewModel.Event(this, parent, e));
        var shown = more.Offset + events.Count;
        if (total > shown) Rows.Insert(i, TreeRowViewModel.More(this, parent, shown, total - shown));
    }

    /// <summary>Expands a row and completes when its children are inserted (used by the screenshot mode).</summary>
    public Task ExpandAsync(TreeRowViewModel row)
    {
        row.SetExpandedSilently(true);
        return OnRowExpandedChanged(row, true);
    }

    // ------------------------------------------------------------------ detail

    private async Task ShowDetailAsync(long eventId)
    {
        if (_session is null) return;
        try
        {
            var d = await _session.RunAsync((_, q) => q.Detail(eventId));
            DetailText = d is null ? "" : FormatDetail(d);
        }
        catch (Exception ex)
        {
            DetailText = "Could not load the event: " + ex.Message;
        }
    }

    public Task SelectEventAsync(TreeRowViewModel row)
    {
        SelectedRow = row;
        return ShowDetailAsync(row.EventId);
    }

    private static string FormatDetail(EventDetail d)
    {
        var sb = new StringBuilder();
        var e = d.Row;
        sb.AppendLine($"{TimeDisplay.Full(e.TsUtc)} {TimeDisplay.ZoneLabel}   (server local {d.TsLocal:yyyy-MM-dd HH:mm:ss.fff})   {e.Server}   {d.FileName} line {d.LineNo}");
        sb.AppendLine($"{e.Category}   [{d.Group ?? "Other"}]   {e.Severity}");
        if (!string.IsNullOrEmpty(d.Note)) sb.AppendLine().AppendLine("Note: " + d.Note);
        sb.AppendLine().AppendLine($"{e.ClassMethod}  {e.Message}");
        if (d.Fields.Count > 0)
        {
            sb.AppendLine();
            var w = d.Fields.Max(x => x.Name.Length) + 2;
            foreach (var (name, value) in d.Fields) sb.Append("  ").Append(name.PadRight(w)).AppendLine(value);
        }
        if (d.Nested is not null)
        {
            sb.AppendLine().AppendLine($"Detail ({e.NestedCount} lines):");
            foreach (var line in d.Nested.Split('\n')) sb.Append("  ").AppendLine(line);
        }
        else if (e.NestedCount > 0)
        {
            sb.AppendLine().AppendLine($"Detail: {e.NestedCount} lines not stored{(d.NestedDropped ? " (Plumbing detail dropped at import)" : "")}.");
        }
        return sb.ToString();
    }

    private static void ShowError(string title, Exception ex) =>
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
