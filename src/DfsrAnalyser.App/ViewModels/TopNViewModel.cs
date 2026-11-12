using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DfsrAnalyser.App.Services;
using DfsrAnalyser.Core.Query;

namespace DfsrAnalyser.App.ViewModels;

public sealed record TopNRowViewModel(TopNRow Row, string Value, string Count, string Share, string Files, string Servers, string Rate,
    string Peak, string PeakAt, string First, string Last, double[] Trend);

/// <summary>The Top-N tab: one ranked list under the main filter, refreshed while the tab is visible.</summary>
public sealed partial class TopNViewModel(MainViewModel owner) : ObservableObject
{
    public IReadOnlyList<TopNList> Lists => CaseQuery.TopNLists;
    public IReadOnlyList<int> Sizes { get; } = [10, 25, 50, 100];
    public ObservableCollection<TopNRowViewModel> Rows { get; } = [];

    [ObservableProperty] private TopNList _selectedList = CaseQuery.TopNLists[0];
    [ObservableProperty] private int _size = 25;
    [ObservableProperty] private TopNRowViewModel? _selectedRow;
    [ObservableProperty] private string _summary = "";

    /// <summary>The last result shown (for export).</summary>
    public TopNResult? Result { get; private set; }
    internal double RangeMinutes { get; private set; }

    public bool CanTrace => SelectedList.Dimension == Dimensions.FileName && SelectedRow is not null;

    partial void OnSelectedListChanged(TopNList value)
    {
        OnPropertyChanged(nameof(CanTrace));
        owner.RefreshTopNAsync().Forget();
    }

    partial void OnSizeChanged(int value) => owner.RefreshTopNAsync().Forget();
    partial void OnSelectedRowChanged(TopNRowViewModel? value) => OnPropertyChanged(nameof(CanTrace));

    internal void Apply(TopNResult result, double rangeMinutes)
    {
        Result = result;
        RangeMinutes = rangeMinutes;
        Rows.Clear();
        foreach (var r in result.Rows)
        {
            var g = r.Group;
            var rate = g.Count / rangeMinutes;
            Rows.Add(new TopNRowViewModel(r, g.Value, g.Count.ToString("N0"), r.Share.ToString("P1"), r.Files == 0 ? "" : r.Files.ToString("N0"),
                r.Servers.ToString("N0"), rate.ToString(rate >= 10 ? "N0" : "0.##"), g.PeakCount.ToString("N0"),
                TimeDisplay.Short(g.PeakAt), TimeDisplay.Short(g.First), TimeDisplay.Short(g.Last), g.Trend));
        }
        Summary = result.Rows.Count == 0
            ? $"{result.List.Label}: nothing in view."
            : $"{result.List.Label}: top {result.Rows.Count} of {result.Total:N0} events in view that have a value · double-click to filter";
    }

    /// <summary>Adds the selected value to the main filter (as a facet click would).</summary>
    [RelayCommand]
    private void FilterToSelected()
    {
        if (SelectedRow is null) return;
        if (!owner.Filter.Selected(SelectedList.Dimension.Key).Contains(SelectedRow.Value))
            owner.ToggleFacetValue(SelectedList.Dimension.Key, SelectedRow.Value);
    }

    [RelayCommand]
    private void TraceSelected()
    {
        if (CanTrace) owner.TraceName(SelectedRow!.Value);
    }
}
