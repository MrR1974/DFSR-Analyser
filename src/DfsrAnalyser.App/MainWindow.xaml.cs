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
        if (App.StartupImport is { } import) await _vm.ImportAsync([import]);
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

        // Third image: the other controls — stack by server, server-first grouping, hidden categories
        // shown, and a message search.
        _vm.ResetTimeCommand.Execute(null);
        _vm.ClearFiltersCommand.Execute(null);
        _vm.StackBy = Dimensions.Server;
        _vm.Level1 = _vm.LevelChoices.First(l => l.Dimension == Dimensions.Server);
        _vm.Level2 = _vm.OptionalLevelChoices.First(l => l.Dimension == Dimensions.Group);
        _vm.Level3 = _vm.OptionalLevelChoices.First(l => l.Dimension == Dimensions.Category);
        _vm.ShowHidden = true;
        _vm.MessageText = "RDCGET";
        await Task.Delay(400);
        await _vm.RefreshAsync();
        foreach (var r in _vm.Rows.Where(r => r.CanExpand).ToList()) await _vm.ExpandAsync(r);
        await SavePngAsync(Path.Combine(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png) + "-controls.png"));
        Application.Current.Shutdown();
    }

    private async Task SavePngAsync(string png)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(500);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var dpi = VisualTreeHelper.GetDpi(this);
        var content = (FrameworkElement)Content;
        var bmp = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        await using var fs = File.Create(png);
        encoder.Save(fs);
    }
}
