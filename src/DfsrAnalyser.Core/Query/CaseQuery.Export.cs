namespace DfsrAnalyser.Core.Query;

/// <summary>One leaf of the grid hierarchy: a value per level, with counts.</summary>
public sealed record GridLeaf(string[] Values, long Count, DateTime First, DateTime Last);

/// <summary>
/// CSV exports. Times are UTC; when <c>local</c> is given, a column in that zone follows each UTC column.
/// </summary>
public sealed partial class CaseQuery
{
    /// <summary>Minutes covered by the filter (its bounds, else the events' extent), as the grid's per-minute rates use.</summary>
    public double RangeMinutes(EventFilter f)
    {
        var extent = Extent(f);
        var from = f.FromUtc ?? extent?.Min ?? DateTime.UnixEpoch;
        var to = f.ToUtc ?? extent?.Max ?? from;
        return Math.Max((to - from).TotalMinutes, 1.0 / 60);
    }

    /// <summary>
    /// Every combination of the levels' values under the filter, ordered as the grid shows them: by each level's
    /// total, descending, then by value.
    /// </summary>
    public List<GridLeaf> GridLeaves(EventFilter f, IReadOnlyList<Dimension> levels, IReadOnlyList<(Dimension Dim, string Value)>? path = null)
    {
        if (levels.Count == 0) throw new ArgumentException("At least one level is needed.", nameof(levels));
        var (from, where, args) = WhereClause(f, path, [.. levels]);
        var cols = levels.Select((d, i) => $"{d.Sql} AS v{i}").ToList();
        var keys = Enumerable.Range(0, levels.Count).Select(i => $"v{i}").ToList();
        var totals = Enumerable.Range(0, levels.Count - 1)
            .Select(i => $"sum(n) OVER (PARTITION BY {string.Join(", ", keys.Take(i + 1))}) AS t{i}").ToList();
        var order = new List<string>();
        for (var i = 0; i < levels.Count - 1; i++) order.AddRange([$"t{i} DESC", $"v{i}"]);
        order.AddRange(["n DESC", $"v{levels.Count - 1}"]);

        var sql = $"""
            SELECT * {(totals.Count == 0 ? "" : ", " + string.Join(", ", totals))} FROM (
                SELECT {string.Join(", ", cols)}, count(*) AS n, min(e.ts_utc) AS first_ts, max(e.ts_utc) AS last_ts
                {from} {where}
                GROUP BY {string.Join(", ", keys)}
            )
            ORDER BY {string.Join(", ", order)}
            """;
        var list = new List<GridLeaf>();
        using var cmd = store.Command(sql, [.. args]);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var values = new string[levels.Count];
            for (var i = 0; i < values.Length; i++) values[i] = r.GetString(i);
            list.Add(new GridLeaf(values, Convert.ToInt64(r.GetValue(levels.Count)), r.GetDateTime(levels.Count + 1), r.GetDateTime(levels.Count + 2)));
        }
        return list;
    }

    public long ExportGrid(EventFilter f, IReadOnlyList<Dimension> levels, CsvWriter csv, TimeZoneInfo? local = null)
    {
        var minutes = RangeMinutes(f);
        var header = levels.Select(l => l.Label).Concat(["Events", "Per minute"]).Concat(TimeHeaders(local, "First", "Last")).ToArray();
        csv.Header(header);
        var rows = GridLeaves(f, levels);
        foreach (var leaf in rows)
        {
            foreach (var v in leaf.Values) csv.Text(v);
            csv.Value(leaf.Count).Value(leaf.Count / minutes);
            Times(csv, local, leaf.First, leaf.Last);
            csv.EndRow();
        }
        return rows.Count;
    }

    /// <summary>
    /// Streams every event under the filter (and an optional grid path) in time order. Returns the row count.
    /// </summary>
    public long ExportEvents(EventFilter f, IReadOnlyList<(Dimension Dim, string Value)>? path, CsvWriter csv,
        TimeZoneInfo? local = null, CancellationToken ct = default)
    {
        var (_, where, args) = WhereClause(f, path, [Dimensions.OriginServer, Dimensions.Partner]);
        var from = AllNamesFrom + "\nJOIN log_file lf ON lf.file_id = e.file_id";
        csv.Header([
            .. TimeHeaders(local, "Time"), "Server local time", "Server", "Category", "Group", "Severity", "Module", "Class::Method",
            "File name", "UID", "GVSN", "Origin server", "Connection", "Transfer", "Error code", "Event ID", "Message", "Log file", "Line",
        ]);
        using var cmd = store.Command($"""
            SELECT e.ts_utc, e.ts_local, s.name, e.category, c.grp,
                   CASE e.severity WHEN 2 THEN 'Error' WHEN 1 THEN 'Warning' ELSE 'Info' END, e.module, e.class_method,
                   e.file_name,
                   {VersionSql("uid")}, {VersionSql("gvsn")},
                   od.server,
                   CASE WHEN nc.partner IS NULL THEN NULL WHEN nc.inbound THEN 'from ' || nc.partner ELSE 'to ' || nc.partner END,
                   e.transfer_type, e.error_code_inner, e.event_log_id, e.message, lf.file_name, e.line_no
            {from} {where}
            ORDER BY e.ts_utc, e.event_id
            """, [.. args]);
        using var r = cmd.ExecuteReader();
        long n = 0;
        while (r.Read())
        {
            if ((++n & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            string? S(int i) => r.IsDBNull(i) ? null : r.GetValue(i).ToString();
            Times(csv, local, r.GetDateTime(0));
            csv.Time(r.IsDBNull(1) ? null : r.GetDateTime(1));
            for (var i = 2; i <= 13; i++) csv.Text(S(i));
            csv.Value(r.IsDBNull(14) ? null : r.GetValue(14)).Value(r.IsDBNull(15) ? null : r.GetValue(15));
            csv.Text(S(16)).Text(S(17)).Value(r.GetValue(18));
            csv.EndRow();
        }
        return n;
    }

    /// <summary>"{GUID}-vN" for a uid/gvsn/parent column pair, or NULL.</summary>
    private static string VersionSql(string col) =>
        $"CASE WHEN e.{col}_db IS NOT NULL THEN '{{' || upper(CAST(e.{col}_db AS VARCHAR)) || '}}-v' || CAST(e.{col}_ver AS VARCHAR) END";

    public static void ExportTopN(TopNResult result, double rangeMinutes, CsvWriter csv, TimeZoneInfo? local = null)
    {
        csv.Header(["Rank", result.List.Dimension.Label, "Events", "Share", "Files (UIDs)", "Servers", "Per minute", "Peak in window",
            .. TimeHeaders(local, "Peak at", "First", "Last")]);
        foreach (var row in result.Rows)
        {
            var g = row.Group;
            csv.Value(row.Rank).Text(g.Value).Value(g.Count).Value(Math.Round(row.Share, 4)).Value(row.Files).Value(row.Servers)
               .Value(g.Count / rangeMinutes).Value(g.PeakCount);
            Times(csv, local, g.PeakAt, g.First, g.Last);
            csv.EndRow();
        }
    }

    /// <summary>The trace's events, each with its version's latency from the change (seconds).</summary>
    public static void ExportTrace(FileTraceResult trace, CsvWriter csv, TimeZoneInfo? local = null, bool includeHidden = true)
    {
        var baseTimes = trace.Versions.ToDictionary(v => v.Gvsn, v => v.BaseTime);
        csv.Header([.. TimeHeaders(local, "Time"), "Since change (s)", "Server", "Stage", "Category", "Class::Method", "GVSN", "Connection",
            "File name", "Linked by", "Message"]);
        foreach (var e in trace.Events.Where(e => includeHidden || !e.Hidden))
        {
            Times(csv, local, e.TsUtc);
            csv.Value(e.Gvsn is not null && baseTimes.TryGetValue(e.Gvsn, out var b) ? Math.Round((e.TsUtc - b).TotalSeconds, 3) : null)
               .Text(e.Server).Text(e.Stage).Text(e.Category).Text(e.ClassMethod).Text(e.Gvsn).Text(e.Connection).Text(e.FileName)
               .Text(e.ViaFid ? "FID" : "UID").Text(e.Message);
            csv.EndRow();
        }
    }

    private static string[] TimeHeaders(TimeZoneInfo? local, params string[] names) =>
        [.. names.SelectMany(n => local is null ? [n + " (UTC)"] : new[] { n + " (UTC)", n + " (local)" })];

    private static void Times(CsvWriter csv, TimeZoneInfo? local, params DateTime[] utc)
    {
        foreach (var t in utc)
        {
            csv.Time(t);
            if (local is not null) csv.Time(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), local));
        }
    }
}
