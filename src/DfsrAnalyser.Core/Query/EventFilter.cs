namespace DfsrAnalyser.Core.Query;

/// <summary>
/// Immutable description of which events are in view. Values within one dimension are OR-ed;
/// dimensions are AND-ed. Hidden categories are excluded unless <see cref="ShowHidden"/> is set.
/// </summary>
public sealed record EventFilter
{
    /// <summary>Inclusive lower bound (UTC).</summary>
    public DateTime? FromUtc { get; init; }
    /// <summary>Exclusive upper bound (UTC).</summary>
    public DateTime? ToUtc { get; init; }
    public bool ShowHidden { get; init; }
    /// <summary>Case-insensitive substring of the message.</summary>
    public string? MessageText { get; init; }
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Include { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>();

    public IReadOnlySet<string> Selected(string dimensionKey) =>
        Include.TryGetValue(dimensionKey, out var set) ? set : EmptySet;

    /// <summary>Returns a filter with <paramref name="value"/> toggled in the dimension's include set.</summary>
    public EventFilter Toggle(string dimensionKey, string value)
    {
        var set = new HashSet<string>(Selected(dimensionKey), StringComparer.Ordinal);
        if (!set.Remove(value)) set.Add(value);
        return WithValues(dimensionKey, set);
    }

    /// <summary>Returns a filter with the dimension's include set replaced (empty set = no restriction).</summary>
    public EventFilter WithValues(string dimensionKey, IEnumerable<string> values)
    {
        var dict = Include.ToDictionary(kv => kv.Key, kv => kv.Value);
        var set = new HashSet<string>(values, StringComparer.Ordinal);
        if (set.Count == 0) dict.Remove(dimensionKey);
        else dict[dimensionKey] = set;
        return this with { Include = dict };
    }

    /// <summary>The same filter without any restriction on one dimension (used for that dimension's facet).</summary>
    public EventFilter Without(string dimensionKey) =>
        Include.ContainsKey(dimensionKey) ? WithValues(dimensionKey, []) : this;

    public EventFilter ClearAll() => this with { Include = new Dictionary<string, IReadOnlySet<string>>(), MessageText = null };

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();
}
