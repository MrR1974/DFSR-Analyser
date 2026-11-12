namespace DfsrAnalyser.Core.Query;

/// <summary>
/// A Top-N list: a dimension plus a condition that keeps only events having a value for it, so a
/// ranking of files does not lead with "(none)".
/// </summary>
public sealed record TopNList(string Key, string Label, Dimension Dimension, string Condition)
{
    public override string ToString() => Label;
}

public sealed record TopNRow(int Rank, GroupRow Group, double Share, long Files, int Servers);

/// <param name="Total">Events in view that have a value for the list (the base for <see cref="TopNRow.Share"/>).</param>
public sealed record TopNResult(TopNList List, long Total, IReadOnlyList<TopNRow> Rows);

public sealed partial class CaseQuery
{
    public static readonly IReadOnlyList<TopNList> TopNLists =
    [
        new("files", "Busiest files", Dimensions.FileName, "e.file_name IS NOT NULL AND NOT coalesce(e.is_folder, FALSE)"),
        new("folders", "Busiest folders", Dimensions.FileName, "e.file_name IS NOT NULL AND e.is_folder"),
        new("ext", "Extensions", Dimensions.Extension, "e.extension IS NOT NULL"),
        new("origin", "Originating servers", Dimensions.OriginServer, "e.gvsn_db IS NOT NULL"),
        new("partner", "Partners", Dimensions.Partner, "e.conn_id IS NOT NULL"),
        new("server", "Servers", Dimensions.Server, "TRUE"),
        new("category", "Categories", Dimensions.Category, "TRUE"),
        new("error", "Error codes", Dimensions.ErrorCode, "e.error_code_inner IS NOT NULL"),
        new("method", "Class::Method", Dimensions.ClassMethod, "TRUE"),
    ];

    public static TopNList GetTopNList(string key) =>
        TopNLists.FirstOrDefault(l => l.Key == key)
        ?? throw new ArgumentException($"Unknown Top-N list '{key}'. Use one of: {string.Join(", ", TopNLists.Select(l => l.Key))}.");

    /// <summary>
    /// The <paramref name="n"/> values of the list's dimension with the most events under the filter, with
    /// share, distinct files (UIDs) and servers, peak window and trend (as the grid computes them).
    /// </summary>
    public TopNResult TopN(EventFilter f, TopNList list, int n, TimeSpan bucket)
    {
        var groups = GroupsCore(f, [], list.Dimension, bucket, n, list.Condition);
        var (from, where, args) = WhereClause(f, null, list.Dimension);
        where = AndCondition(where, list.Condition);
        var total = store.Scalar<long>($"SELECT count(*) {from} {where}", [.. args]);

        var extra = new Dictionary<string, (long Files, int Servers)>(StringComparer.Ordinal);
        if (groups.Count > 0)
        {
            var valueArgs = new List<object?>(args);
            valueArgs.AddRange(groups.Select(g => g.Value));
            var inValues = $"{list.Dimension.Sql} IN ({string.Join(", ", groups.Select(_ => "?"))})";
            using var cmd = store.Command($"""
                SELECT {list.Dimension.Sql} AS v,
                       count(DISTINCT CASE WHEN e.uid_db IS NOT NULL THEN CAST(e.uid_db AS VARCHAR) || '-' || CAST(e.uid_ver AS VARCHAR) END),
                       count(DISTINCT e.server_id)
                {from} {AndCondition(where, inValues)} GROUP BY v
                """, [.. valueArgs]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) extra[r.GetString(0)] = (Convert.ToInt64(r.GetValue(1)), Convert.ToInt32(r.GetValue(2)));
        }

        var rows = groups.Select((g, i) =>
        {
            var (files, servers) = extra.TryGetValue(g.Value, out var x) ? x : (0, 0);
            return new TopNRow(i + 1, g, total == 0 ? 0 : (double)g.Count / total, files, servers);
        }).ToList();
        return new TopNResult(list, total, rows);
    }
}
