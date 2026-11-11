using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DfsrAnalyser.Core.Query;

namespace DfsrAnalyser.App.ViewModels;

/// <summary>One facet in the filter pane: a dimension's values with counts under the other filters.</summary>
public sealed partial class FacetViewModel(Dimension dimension, MainViewModel owner, bool expanded) : ObservableObject
{
    public Dimension Dimension { get; } = dimension;
    public string Label => Dimension.Label;
    public bool Searchable => Dimension.Searchable;
    public ObservableCollection<FacetValueViewModel> Values { get; } = [];

    [ObservableProperty] private bool _isExpanded = expanded;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _selectedCount;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) owner.RefreshFacetAsync(this).Forget();
    }

    partial void OnSearchChanged(string value) => owner.RefreshFacetAsync(this, debounce: true).Forget();

    internal void Toggle(string value) => owner.ToggleFacetValue(Dimension.Key, value);

    internal void Apply(IReadOnlyList<FacetValue> values)
    {
        Values.Clear();
        foreach (var v in values) Values.Add(new FacetValueViewModel(this, v.Value, v.Count, v.Selected));
        SelectedCount = values.Count(v => v.Selected);
    }
}

public sealed partial class FacetValueViewModel : ObservableObject
{
    private readonly FacetViewModel _facet;
    private bool _isSelected;

    internal FacetValueViewModel(FacetViewModel facet, string value, long count, bool selected)
    {
        _facet = facet;
        Value = value;
        Count = count;
        _isSelected = selected;
    }

    public string Value { get; }
    public long Count { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            _facet.Toggle(Value);
        }
    }
}

internal static class TaskExtensions
{
    /// <summary>Fire-and-forget for UI work whose errors are reported by the callee.</summary>
    public static async void Forget(this Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
