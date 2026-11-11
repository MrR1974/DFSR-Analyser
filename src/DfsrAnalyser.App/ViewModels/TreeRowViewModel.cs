using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DfsrAnalyser.App.Services;
using DfsrAnalyser.Core.Query;

namespace DfsrAnalyser.App.ViewModels;

public enum RowKind { Group, Event, More, Note }

/// <summary>
/// A row of the flattened tree grid. Group rows expand into the next level (or into events at the
/// last level); event rows select into the detail pane; a More row loads the next page of events.
/// </summary>
public sealed partial class TreeRowViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    private TreeRowViewModel(MainViewModel owner, RowKind kind, int level, IReadOnlyList<(Dimension, string)> path)
    {
        _owner = owner;
        Kind = kind;
        Level = level;
        Path = path;
    }

    public RowKind Kind { get; }
    public int Level { get; }
    /// <summary>Dimension/value pairs from the root down to (and including) this row.</summary>
    public IReadOnlyList<(Dimension Dim, string Value)> Path { get; }
    public TreeRowViewModel? Parent { get; private init; }

    public string Text { get; private init; } = "";
    public string? ToolTip { get; private init; }
    public string? CountText { get; private init; }
    public string? RateText { get; private init; }
    public string? PeakText { get; private init; }
    public string? PeakAtText { get; private init; }
    public string? FirstText { get; private init; }
    public string? LastText { get; private init; }
    public double[]? Trend { get; private init; }
    public Brush? Swatch { get; private init; }
    public long Count { get; private init; }

    public long EventId { get; private init; }
    public int Offset { get; private init; }

    public bool CanExpand => Kind == RowKind.Group;
    public double Indent => Level * 16;
    public bool IsEvent => Kind == RowKind.Event;
    public bool IsMore => Kind == RowKind.More;
    public bool IsError { get; private init; }

    [ObservableProperty] private bool _isExpanded;
    private bool _suppressExpandCallback;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!_suppressExpandCallback) _owner.OnRowExpandedChanged(this, value).Forget();
    }

    /// <summary>Sets IsExpanded without triggering the load (the caller runs and awaits it).</summary>
    internal void SetExpandedSilently(bool value)
    {
        _suppressExpandCallback = true;
        try { IsExpanded = value; }
        finally { _suppressExpandCallback = false; }
    }

    public static TreeRowViewModel Group(MainViewModel owner, TreeRowViewModel? parent, Dimension dim, GroupRow g, double rangeMinutes, Brush? swatch)
    {
        var path = new List<(Dimension, string)>(parent?.Path ?? []) { (dim, g.Value) };
        return new TreeRowViewModel(owner, RowKind.Group, path.Count - 1, path)
        {
            Parent = parent,
            Text = g.Value,
            ToolTip = $"{dim.Label}: {g.Value}",
            Count = g.Count,
            CountText = g.Count.ToString("N0"),
            RateText = rangeMinutes > 0 ? (g.Count / rangeMinutes).ToString(g.Count / rangeMinutes >= 10 ? "N0" : "0.##") : null,
            PeakText = g.PeakCount.ToString("N0"),
            PeakAtText = TimeDisplay.Short(g.PeakAt),
            FirstText = TimeDisplay.Short(g.First),
            LastText = TimeDisplay.Short(g.Last),
            Trend = g.Trend,
            Swatch = swatch,
        };
    }

    public static TreeRowViewModel Event(MainViewModel owner, TreeRowViewModel parent, EventRow e)
    {
        var msg = e.Message.Length > 200 ? e.Message[..200] + "…" : e.Message;
        var method = e.ClassMethod.LastIndexOf("::", StringComparison.Ordinal) is var i and >= 0 ? e.ClassMethod[(i + 2)..] : e.ClassMethod;
        return new TreeRowViewModel(owner, RowKind.Event, parent.Level + 1, parent.Path)
        {
            Parent = parent,
            EventId = e.EventId,
            Text = $"{TimeDisplay.ToDisplay(e.TsUtc):MM-dd HH:mm:ss.fff}  {e.Server}  {method}  {msg}",
            ToolTip = $"{TimeDisplay.Full(e.TsUtc)} {TimeDisplay.ZoneLabel}   {e.Server}\n{e.Category}\n{e.ClassMethod}\n{e.Message}",
            CountText = e.NestedCount > 0 ? $"+{e.NestedCount}" : null,
            IsError = e.Severity != "Info",
        };
    }

    public static TreeRowViewModel More(MainViewModel owner, TreeRowViewModel parent, int offset, long remaining) =>
        new(owner, RowKind.More, parent.Level + 1, parent.Path)
        {
            Parent = parent,
            Offset = offset,
            Text = $"Show more events… ({remaining:N0} remaining)",
        };

    public static TreeRowViewModel Note(MainViewModel owner, TreeRowViewModel? parent, string text) =>
        new(owner, RowKind.Note, (parent?.Level ?? -1) + 1, parent?.Path ?? []) { Parent = parent, Text = text };

    /// <summary>True when <paramref name="row"/> is this row or one of its descendants.</summary>
    public bool IsAncestorOf(TreeRowViewModel row)
    {
        for (var r = row.Parent; r is not null; r = r.Parent)
            if (ReferenceEquals(r, this)) return true;
        return false;
    }
}
