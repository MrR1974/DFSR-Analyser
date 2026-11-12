using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DfsrAnalyser.App.Services;
using DfsrAnalyser.App.ViewModels;
using DfsrAnalyser.Core.Query;
using ScottPlot;

namespace DfsrAnalyser.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private double? _dragStartX;
    private Point _dragStartPixel;
    private ScottPlot.Plottables.HorizontalSpan? _span;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Chart.UserInputProcessor.Disable();
        StyleChart();
        _vm.SeriesChanged += (_, _) => RenderChart();

        Chart.MouseLeftButtonDown += OnChartMouseDown;
        Chart.MouseMove += OnChartMouseMove;
        Chart.MouseLeftButtonUp += OnChartMouseUp;
        Chart.MouseRightButtonUp += (_, _) => _vm.ZoomOutCommand.Execute(null);

        Loaded += OnLoaded;
        Closing += (_, _) => _vm.Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.StartupCase is { } folder) await _vm.OpenCaseAsync(folder);
        if (App.StartupImport is { } import)
        {
            if (App.ScreenshotPath is { } shot && _vm.Session is { } session) await ScreenshotImportDialogAsync(session, import, shot);
            await _vm.ImportAsync([import], StartupImportOptions, quiet: App.ScreenshotPath is not null);
        }
        if (App.ScreenshotPath is { } png) await ScreenshotAsync(png);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ chart

    private void StyleChart()
    {
        var plt = Chart.Plot;
        plt.FigureBackground.Color = ScottPlot.Colors.White;
        plt.DataBackground.Color = ScottPlot.Colors.White;
        plt.Grid.MajorLineColor = ScottPlot.Color.FromHex("#ECEEF1");
        plt.Axes.Color(ScottPlot.Color.FromHex("#6B7280"));
        plt.Axes.Left.Label.Text = "Events per window";
        plt.Axes.Left.Label.FontSize = 11;
        plt.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { IntegerTicksOnly = true };
        // Once only: each ShowLegend(Edge) call adds another legend panel.
        plt.ShowLegend(Edge.Right);
        plt.Legend.FontSize = 10;
        plt.Legend.Orientation = Orientation.Vertical;
    }

    private void RenderChart()
    {
        var plt = Chart.Plot;
        plt.Clear();
        _span = null;
        var s = _vm.Series;
        if (s is null || s.Buckets.Length == 0)
        {
            Chart.Refresh();
            return;
        }

        var width = s.BucketSize.TotalDays;
        var baseValues = new double[s.Buckets.Length];
        var positions = s.Buckets.Select(b => TimeDisplay.ToDisplay(b).ToOADate() + width / 2).ToArray();
        foreach (var (key, counts) in s.Series)
        {
            var color = ScottPlot.Color.FromHex(_vm.Palette.HexFor(key));
            var bars = new List<Bar>();
            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                bars.Add(new Bar
                {
                    Position = positions[i],
                    ValueBase = baseValues[i],
                    Value = baseValues[i] + counts[i],
                    Size = width * 0.92,
                    FillColor = color,
                    LineWidth = 0,
                });
                baseValues[i] += counts[i];
            }
            if (bars.Count == 0) continue;
            var plot = plt.Add.Bars(bars);
            plot.LegendText = key;
            plot.Color = color;
        }

        var axis = plt.Axes.DateTimeTicksBottom();
        var multiDay = (s.Buckets[^1] - s.Buckets[0]) >= TimeSpan.FromDays(1);
        var subSecond = s.BucketSize < TimeSpan.FromSeconds(1);
        if (axis.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic ticks)
            ticks.LabelFormatter = d => multiDay ? d.ToString("MM-dd HH:mm") : d.ToString(subSecond ? "HH:mm:ss.f" : "HH:mm:ss");
        var max = baseValues.Length > 0 ? baseValues.Max() : 1;
        plt.Axes.SetLimitsY(0, Math.Max(1, max * 1.08));
        plt.Axes.SetLimitsX(TimeDisplay.ToDisplay(s.Buckets[0]).ToOADate(), TimeDisplay.ToDisplay(s.Buckets[^1]).ToOADate() + width);
        Chart.Refresh();
    }

    private double PixelToX(Point p)
    {
        var scale = Chart.DisplayScale;
        return Chart.Plot.GetCoordinates((float)(p.X * scale), (float)(p.Y * scale)).X;
    }

    private void OnChartMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Series is null || _vm.Series.Buckets.Length == 0) return;
        _dragStartPixel = e.GetPosition(Chart);
        _dragStartX = PixelToX(_dragStartPixel);
        Chart.CaptureMouse();
    }

    private void OnChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartX is not { } x0 || e.LeftButton != MouseButtonState.Pressed) return;
        var x1 = PixelToX(e.GetPosition(Chart));
        _span ??= Chart.Plot.Add.HorizontalSpan(x0, x1, ScottPlot.Color.FromHex("#2E6FD8").WithAlpha(0.15));
        _span.X1 = Math.Min(x0, x1);
        _span.X2 = Math.Max(x0, x1);
        Chart.Refresh();
    }

    private void OnChartMouseUp(object sender, MouseButtonEventArgs e)
    {
        Chart.ReleaseMouseCapture();
        if (_dragStartX is not { } x0 || _vm.Series is not { } s) return;
        _dragStartX = null;
        var end = e.GetPosition(Chart);
        var x1 = PixelToX(end);
        if (_span is not null)
        {
            Chart.Plot.Remove(_span);
            _span = null;
        }

        DateTime ToUtc(double oa) => TimeDisplay.FromDisplay(DateTime.FromOADate(oa));
        if (Math.Abs(end.X - _dragStartPixel.X) < 4)
        {
            // A click selects the window under the cursor.
            var start = CaseQuery.BucketStart(ToUtc(x0), s.BucketSize);
            _vm.SetTimeRange(start, start + s.BucketSize);
        }
        else
        {
            _vm.SetTimeRange(ToUtc(Math.Min(x0, x1)), ToUtc(Math.Max(x0, x1)));
        }
    }

    // ------------------------------------------------------------------ screenshot mode

    private static Core.Import.ImportOptions StartupImportOptions => App.ImportNaming.ToLowerInvariant() switch
    {
        "header" => new(),
        "folder" => new(Core.Import.ServerNaming.FolderName),
        _ => new(Core.Import.ServerNaming.Fixed, App.ImportNaming),
    };

    /// <summary>The Import dialog for the start-up import, after its scan, saved as &lt;png&gt;-import.png.</summary>
    private async Task ScreenshotImportDialogAsync(CaseSession session, string import, string png)
    {
        var ivm = new ImportViewModel(session, [import]);
        var options = StartupImportOptions;
        ivm.UseFolderNames = options.Naming == Core.Import.ServerNaming.FolderName;
        ivm.UseFixedName = options.Naming == Core.Import.ServerNaming.Fixed;
        if (ivm.UseFixedName) ivm.FixedName = options.FixedName!;
        var dlg = new ImportWindow(ivm) { Owner = this };
        dlg.Show();
        await ivm.RescanAsync();
        await SavePngAsync(Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png) + "-import.png"), dlg);
        dlg.Close();
    }

    /// <summary>
    /// Exercises the main views (chart, three grid levels, event detail) on the open case, renders the
    /// window to a PNG and exits. Lets the UI be checked without a person at the screen.
    /// </summary>
    private async Task ScreenshotAsync(string png)
    {
        Width = 1600;
        Height = 1000;
        await _vm.RefreshAsync();

        var row = _vm.Rows.FirstOrDefault(r => r.CanExpand);
        while (row is not null)
        {
            await _vm.ExpandAsync(row);
            var at = _vm.Rows.IndexOf(row);
            var next = at + 1 < _vm.Rows.Count ? _vm.Rows[at + 1] : null;
            row = next is { CanExpand: true } ? next : null;
            if (next is { IsEvent: true }) await _vm.SelectEventAsync(next);
        }

        await SavePngAsync(png);

        // Second image: zoomed to the busiest window's neighbourhood and filtered to one server,
        // exercising the filter -> refresh path the way chart and facet clicks do.
        if (_vm.Series is { Buckets.Length: > 0 } s)
        {
            var totals = s.Buckets.Select((_, i) => s.Series.Sum(x => x.Counts[i])).ToArray();
            var peak = Array.IndexOf(totals, totals.Max());
            var from = s.Buckets[Math.Max(0, peak - 6)];
            var to = s.Buckets[Math.Min(s.Buckets.Length - 1, peak + 6)] + s.BucketSize;
            _vm.SetTimeRange(from, to);
            var server = _vm.Facets.First(f => f.Dimension == Dimensions.Server).Values.FirstOrDefault()?.Value;
            if (server is not null) _vm.Filter = _vm.Filter.Toggle(Dimensions.Server.Key, server);
            await _vm.RefreshAsync();
            if (_vm.Rows.FirstOrDefault(r => r.CanExpand) is { } first) await _vm.ExpandAsync(first);
            await SavePngAsync(Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png) + "-filtered.png"));
        }

        // Third image: resolved names — stacked by origin server, grouped by connection then category,
        // limited to the Local change / Outbound / Inbound groups.
        _vm.ResetTimeCommand.Execute(null);
        _vm.ClearFiltersCommand.Execute(null);
        _vm.StackBy = Dimensions.OriginServer;
        _vm.Level1 = _vm.LevelChoices.First(l => l.Dimension == Dimensions.Connection);
        _vm.Level2 = _vm.OptionalLevelChoices.First(l => l.Dimension == Dimensions.Category);
        _vm.Level3 = _vm.OptionalLevelChoices.First(l => l.Dimension is null);
        _vm.Filter = _vm.Filter.WithValues(Dimensions.Group.Key, ["Local change", "Outbound", "Inbound"]);
        await Task.Delay(400);
        await _vm.RefreshAsync();
        foreach (var r in _vm.Rows.Where(r => r.CanExpand).ToList()) await _vm.ExpandAsync(r);
        await SavePngAsync(Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png) + "-controls.png"));

        // Fifth image: typed time range (the middle third of the logs) and a wildcard file name filter.
        _vm.StackBy = Dimensions.Category;
        _vm.Level1 = _vm.LevelChoices.First(l => l.Dimension == Dimensions.Category);
        _vm.Level2 = _vm.OptionalLevelChoices.First(l => l.Dimension == Dimensions.Server);
        _vm.Level3 = _vm.OptionalLevelChoices.First(l => l.Dimension == Dimensions.FileName);
        _vm.ClearFiltersCommand.Execute(null);
        _vm.ResetTimeCommand.Execute(null);
        await Task.Delay(400);
        await _vm.RefreshAsync();
        if (_vm.FromDate is { } fd && _vm.ToDate is { } td &&
            TimeSpan.TryParse(_vm.FromTime, System.Globalization.CultureInfo.InvariantCulture, out var ft) &&
            TimeSpan.TryParse(_vm.ToTime, System.Globalization.CultureInfo.InvariantCulture, out var tt))
        {
            DateTime start = fd.Date + ft, end = td.Date + tt;
            var third = TimeSpan.FromSeconds(Math.Round((end - start).TotalSeconds / 3));
            DateTime a = start + third, b = start + third + third;
            _vm.FromDate = a.Date;
            _vm.FromTime = a.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            _vm.ToDate = b.Date;
            _vm.ToTime = b.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            _vm.ApplyTimeRangeCommand.Execute(null);
        }
        _vm.FileNamePatternText = App.ScreenshotFilePattern;
        await Task.Delay(600);
        await _vm.RefreshAsync();
        foreach (var r in _vm.Rows.Where(r => r.CanExpand).Take(2).ToList()) await _vm.ExpandAsync(r);
        var stem = Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png));
        await SavePngAsync(stem + "-filters.png");
        await File.WriteAllTextAsync(stem + "-filters.txt",
            $"From {_vm.FromDate:yyyy-MM-dd} {_vm.FromTime}  To {_vm.ToDate:yyyy-MM-dd} {_vm.ToTime}  ({TimeDisplay.ZoneLabel})\n" +
            $"Filter UTC: {_vm.Filter.FromUtc:yyyy-MM-dd HH:mm:ss.fff} - {_vm.Filter.ToUtc:yyyy-MM-dd HH:mm:ss.fff}, file name {_vm.Filter.FileNamePattern}\n" +
            $"{_vm.StatusText}\n" + (_vm.LastImport is { } li ? $"Last import: {li.Events:N0} events, {li.FailedCount} failed, log {li.LogPath}\n" : ""));

        // Fourth image: the Collect dialog for the first saved profile, after a real dry run.
        if (_vm.Session is { } session && _vm.ProfileNames.FirstOrDefault() is { } profile)
        {
            var cvm = new CollectViewModel(session, () => { }, profile);
            var dlg = new CollectWindow(cvm) { Owner = this };
            dlg.Show();
            await cvm.DryRunCommand.ExecuteAsync(null);
            await SavePngAsync(Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png) + "-collect.png"), dlg);
            dlg.Close();
        }
        Application.Current.Shutdown();
    }

    private async Task SavePngAsync(string png, Window? window = null)
    {
        window ??= this;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var dpi = VisualTreeHelper.GetDpi(window);
        var content = (FrameworkElement)window.Content;
        var bmp = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        await using var fs = File.Create(png);
        encoder.Save(fs);
    }
}
