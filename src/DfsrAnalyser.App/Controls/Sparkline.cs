using System.Windows;
using System.Windows.Media;

namespace DfsrAnalyser.App.Controls;

/// <summary>A tiny bar trend drawn directly in OnRender (cheap enough for thousands of virtualised rows).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values is null || values.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var max = values.Max();
        if (max <= 0) return;
        var w = ActualWidth / values.Count;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] <= 0) continue;
            var h = Math.Max(1, values[i] / max * ActualHeight);
            dc.DrawRectangle(Fill, null, new Rect(i * w, ActualHeight - h, Math.Max(1, w - 0.5), h));
        }
    }
}
