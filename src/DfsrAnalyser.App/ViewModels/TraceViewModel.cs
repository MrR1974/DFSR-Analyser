using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DfsrAnalyser.App.Services;
using DfsrAnalyser.Core.Query;

namespace DfsrAnalyser.App.ViewModels;

public sealed record UidMatchRow(UidMatch Match, string Text, string ToolTip);

/// <summary>One line of the milestone table: a version's change on its origin, or its arrival at one partner.</summary>
public sealed record TraceMilestoneRow(string Version, string Server, string First, string Second, string Installed, string Outcome, bool IsOrigin, string ToolTip);

public sealed record TraceEventRow(TraceEvent Event, string Time, string Since, string Method, bool IsError);

/// <summary>
/// The File trace tab: find a file by name (wildcards) or UID, pick the UID when a name had several lives, and
/// show its versions' milestones and every event on every server in UTC order.
/// </summary>
public sealed partial class TraceViewModel(MainViewModel owner) : ObservableObject
{
    public ObservableCollection<UidMatchRow> Matches { get; } = [];
    public ObservableCollection<TraceMilestoneRow> Milestones { get; } = [];
    public ObservableCollection<TraceEventRow> Events { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _limitToRange;
    [ObservableProperty] private UidMatchRow? _selectedMatch;
    [ObservableProperty] private TraceEventRow? _selectedEvent;
    [ObservableProperty] private string _summary = "Enter a file name (wildcards allowed) or a UID such as {BFF1148F-…}-v1019, then press Enter.";
    [ObservableProperty] private bool _hasMatches;

    /// <summary>The trace shown (for export and screenshots).</summary>
    public FileTraceResult? Result { get; private set; }

    partial void OnSelectedMatchChanged(UidMatchRow? value)
    {
        if (value is not null) TraceAsync(value.Match.Uid).Forget();
    }

    partial void OnSelectedEventChanged(TraceEventRow? value)
    {
        if (value is not null) owner.ShowEventDetail(value.Event.EventId);
    }

    partial void OnLimitToRangeChanged(bool value)
    {
        if (Result is not null) TraceAsync(Result.Uid).Forget();
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0 || owner.Session is not { } session) return;
        if (CaseQuery.TryParseUid(text, out _, out _))
        {
            Matches.Clear();
            HasMatches = false;
            await TraceAsync(text);
            return;
        }
        var f = owner.Filter;
        var (from, to) = LimitToRange ? (f.FromUtc, f.ToUtc) : (null, null);
        try
        {
            var found = await session.RunAsync((_, q) => q.FindUids(text, from, to));
            Matches.Clear();
            foreach (var m in found)
                Matches.Add(new UidMatchRow(m,
                    $"{m.Names}   ·   created on {m.CreatedOn ?? "?"}   ·   {m.Versions} version(s), {m.Servers} server(s), {m.Events:N0} events   ·   {TimeDisplay.Short(m.First)} – {TimeDisplay.Short(m.Last)}",
                    m.Uid));
            HasMatches = found.Count > 1;
            if (found.Count == 0)
            {
                Clear();
                Summary = $"No file (UID) has a name matching '{text}'{(LimitToRange ? " in the current time range" : "")}.";
            }
            else if (found.Count == 1) await TraceAsync(found[0].Uid);
            else
            {
                Clear();
                Summary = $"{found.Count} files (UIDs) match '{text}' — a name can have several lives (deleted and re-created). Pick one.";
            }
        }
        catch (Exception ex)
        {
            Summary = "Search failed: " + ex.Message;
        }
    }

    private void Clear()
    {
        Result = null;
        Milestones.Clear();
        Events.Clear();
    }

    public async Task TraceAsync(string uid)
    {
        if (owner.Session is not { } session || !CaseQuery.TryParseUid(uid, out var db, out var ver)) return;
        var f = owner.Filter;
        var (from, to) = LimitToRange ? (f.FromUtc, f.ToUtc) : (null, null);
        try
        {
            Summary = $"Tracing {uid}…";
            var t = await session.RunAsync((_, q) => q.Trace(db, ver, from, to));
            Result = t;
            Show();
        }
        catch (Exception ex)
        {
            Summary = "Trace failed: " + ex.Message;
        }
    }

    /// <summary>Re-renders the current trace (display zone or hidden categories changed).</summary>
    internal void Show()
    {
        if (Result is not { } t) return;
        static string Since(DateTime? at, DateTime b) => at is { } x ? "+" + TimeDisplay.Latency(x - b) : "–";
        static string ShortId(string gvsn) => gvsn.Length > 12 ? gvsn[..9] + "…" + gvsn[(gvsn.LastIndexOf('-'))..] : gvsn;

        Milestones.Clear();
        foreach (var v in t.Versions)
        {
            Milestones.Add(new TraceMilestoneRow(ShortId(v.Gvsn), v.Origin ?? "unknown origin",
                v.ChangedAt is { } c ? $"{v.ChangeKind} at {TimeDisplay.Full(c)[11..]}" : $"first seen {TimeDisplay.Full(v.FirstSeen)[11..]} (change not in the logs)",
                v.OfferedAt is { } o ? $"offered {Since(o, v.BaseTime)}" : "", "", "", true,
                $"Version {v.Gvsn}\nOrigin {v.Origin ?? "unknown"} · {TimeDisplay.Full(v.BaseTime)} {TimeDisplay.ZoneLabel}"));
            foreach (var d in v.Deliveries)
                Milestones.Add(new TraceMilestoneRow("", "  → " + d.Server,
                    $"received {Since(d.ReceivedAt, v.BaseTime)}",
                    d.TransferredAt is null ? "" : $"{d.TransferType ?? "transferred"} {Since(d.TransferredAt, v.BaseTime)}",
                    d.InstalledAt is null ? "not installed in these logs" : Since(d.InstalledAt, v.BaseTime),
                    d.Outcome ?? "", false,
                    $"Times since the change on {v.Origin ?? "the origin"}" +
                    (d.InstalledAt is { } i ? $"\nInstalled at {TimeDisplay.Full(i)} {TimeDisplay.ZoneLabel}" : "")));
        }

        var bases = t.Versions.ToDictionary(v => v.Gvsn, v => v.BaseTime);
        Events.Clear();
        var showHidden = owner.ShowHidden;
        foreach (var e in t.Events.Where(e => showHidden || !e.Hidden))
            Events.Add(new TraceEventRow(e, TimeDisplay.Full(e.TsUtc)[5..],
                e.Gvsn is not null && bases.TryGetValue(e.Gvsn, out var b) ? $"+{(e.TsUtc - b).TotalSeconds:0.000}" : "",
                e.ClassMethod + (e.ViaFid ? "  [by FID]" : ""), e.Severity != "Info"));

        var hidden = t.Events.Count(e => e.Hidden);
        Summary = $"{t.Uid}   {string.Join(", ", t.Names)}   created on {t.CreatedOn ?? "unknown"}   ·   {t.Versions.Count} version(s), " +
                  $"{t.Events.Count:N0} events{(t.Truncated ? $" (first {CaseQuery.MaxTraceEvents:N0})" : "")}" +
                  (!showHidden && hidden > 0 ? $", {hidden:N0} detail lines hidden (View ▸ Show hidden)" : "") +
                  $"   ·   times {TimeDisplay.ZoneLabel}, latency from the change";
    }
}
