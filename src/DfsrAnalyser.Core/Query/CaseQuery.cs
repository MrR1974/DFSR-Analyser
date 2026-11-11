using System.Globalization;
using System.Text;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Core.Query;

public sealed record FacetValue(string Value, long Count, bool Selected);

public sealed record TimeSeries(DateTime[] Buckets, TimeSpan BucketSize, IReadOnlyList<(string Key, long[] Counts)> Series);

/// <summary>One row of the aggregate grid: a dimension value with its counts over the filtered range.</summary>
public sealed record GroupRow(string Value, long Count, DateTime First, DateTime Last, long PeakCount, DateTime PeakAt, double[] Trend);

public sealed record EventRow(long EventId, DateTime TsUtc, string Server, string Category, string ClassMethod,
    string Severity, string? FileName, string Message, int NestedCount);

public sealed record EventDetail(EventRow Row, IReadOnlyList<(string Name, string Value)> Fields, string? Nested,
    bool NestedDropped, string? Group, string? Note, DateTime TsLocal, int LineNo, string FileName);

/// <summary>
/// Read queries for the UI: facets, time series, aggregate grid rows and events, all under an
/// <see cref="EventFilter"/>. Values are always passed as parameters; only trusted dimension SQL and
/// computed numbers are spliced in.
/// </summary>
public sealed class CaseQuery(CaseStore store)
{
    public const int TrendBins = 40;

    private const string BaseFrom = """
        FROM event e
        JOIN server s ON s.server_id = e.server_id
        LEFT JOIN category c ON c.name = e.category
        """;

    /// <summary>Name-table joins, in dependency order (nrg uses nrf). Added only when a dimension needs them.</summary>
    private static readonly (string Alias, string Sql)[] NameJoins =
    [
        ("od", "LEFT JOIN name_db od ON od.guid = e.gvsn_db"),
        ("ud", "LEFT JOIN name_db ud ON ud.guid = e.uid_db"),
        ("nc", "LEFT JOIN name_conn nc ON nc.conn_id = e.conn_id AND nc.server_id = e.server_id"),
        ("nrf", "LEFT JOIN name_rf nrf ON nrf.guid = e.cs_id"),
        ("nrg", "LEFT JOIN name_rg nrg ON nrg.guid = coalesce(e.replica_set_id, nrf.rg_guid)"),
    ];

    private static string FromFor(IEnumerable<Dimension> dims)
    {
        var needed = dims.SelectMany(d => d.Joins).ToHashSet(StringComparer.Ordinal);
        if (needed.Count == 0) return BaseFrom;
        return BaseFrom + "\n" + string.Join("\n", NameJoins.Where(j => needed.Contains(j.Alias)).Select(j => j.Sql));
    }

    private static string AllNamesFrom => BaseFrom + "\n" + string.Join("\n", NameJoins.Select(j => j.Sql));

    // ------------------------------------------------------------------ filter SQL

    /// <summary>FROM (with the joins every referenced dimension needs), WHERE and its parameters.</summary>
    private static (string From, string Where, List<object?> Args) WhereClause(
        EventFilter f, IEnumerable<(Dimension Dim, string Value)>? path = null, params Dimension[] extra)
    {
        var dims = new List<Dimension>(extra);
        var parts = new List<string>();
        var args = new List<object?>();
        if (f.FromUtc is { } from) { parts.Add("e.ts_utc >= ?"); args.Add(from); }
        if (f.ToUtc is { } to) { parts.Add("e.ts_utc < ?"); args.Add(to); }
        if (!f.ShowHidden) parts.Add("NOT coalesce(c.hidden, FALSE)");
        if (!string.IsNullOrWhiteSpace(f.MessageText)) { parts.Add("contains(lower(e.message), lower(?))"); args.Add(f.MessageText); }
        foreach (var (key, values) in f.Include)
        {
            if (values.Count == 0) continue;
            var dim = Dimensions.Get(key);
            dims.Add(dim);
            parts.Add($"{dim.Sql} IN ({string.Join(", ", values.Select(_ => "?"))})");
            args.AddRange(values);
        }
        if (path is not null)
            foreach (var (dim, value) in path)
            {
                dims.Add(dim);
                parts.Add($"{dim.Sql} = ?");
                args.Add(value);
            }
        return (FromFor(dims), parts.Count == 0 ? "" : "WHERE " + string.Join(" AND ", parts), args);
    }

    // ------------------------------------------------------------------ extent and bucketing

    /// <summary>Time span of the events matching the filter (ignoring its own time bounds when <paramref name="ignoreTime"/>).</summary>
    public (DateTime Min, DateTime Max)? Extent(EventFilter f, bool ignoreTime = false)
    {
        var filter = ignoreTime ? f with { FromUtc = null, ToUtc = null } : f;
        var (from, where, args) = WhereClause(filter);
        using var cmd = store.Command($"SELECT min(e.ts_utc), max(e.ts_utc) {from} {where}", [.. args]);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return null;
        return (r.GetDateTime(0), r.GetDateTime(1));
    }

    public static readonly TimeSpan[] BucketPresets =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromDays(1),
    ];

    /// <summary>The smallest preset giving at most <paramref name="maxBuckets"/> buckets over the span.</summary>
    public static TimeSpan AutoBucket(TimeSpan span, int maxBuckets = 240)
    {
        foreach (var b in BucketPresets)
            if (span.Ticks / b.Ticks <= maxBuckets) return b;
        return TimeSpan.FromDays(Math.Ceiling(span.TotalDays / maxBuckets));
    }

    /// <summary>Start of the bucket containing <paramref name="t"/> (buckets aligned to the epoch).</summary>
    public static DateTime BucketStart(DateTime t, TimeSpan bucket) =>
        new(t.Ticks - (t.Ticks % bucket.Ticks), DateTimeKind.Utc);

    private static string BucketSql(TimeSpan bucket) =>
        $"time_bucket(INTERVAL '{(long)bucket.TotalMilliseconds} milliseconds', e.ts_utc, TIMESTAMP '1970-01-01')";

    // ------------------------------------------------------------------ facets

    /// <summary>
    /// Values of a dimension with counts under the filter <em>excluding the dimension's own selection</em>,
    /// selected values first, then by count. Selected values with no matches are still returned (count 0).
    /// </summary>
    public List<FacetValue> Facet(Dimension dim, EventFilter f, int limit = 50, string? search = null)
    {
        var selected = f.Selected(dim.Key);
        var (from, where, args) = WhereClause(f.Without(dim.Key), null, dim);
        var sb = new StringBuilder($"SELECT {dim.Sql} AS v, count(*) AS n {from} {where}");
        if (!string.IsNullOrWhiteSpace(search))
        {
            sb.Append(where.Length == 0 ? " WHERE " : " AND ").Append($"contains(lower({dim.Sql}), lower(?))");
            args.Add(search);
        }
        sb.Append(" GROUP BY v ORDER BY n DESC, v LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));

        var rows = new List<FacetValue>();
        using (var cmd = store.Command(sb.ToString(), [.. args]))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                rows.Add(new FacetValue(r.GetString(0), Convert.ToInt64(r.GetValue(1)), selected.Contains(r.GetString(0))));

        foreach (var s in selected)
            if (rows.All(x => x.Value != s)) rows.Add(new FacetValue(s, 0, true));
        return [.. rows.OrderByDescending(x => x.Selected).ThenByDescending(x => x.Count).ThenBy(x => x.Value, StringComparer.Ordinal)];
    }

    // ------------------------------------------------------------------ time series

    /// <summary>
    /// Counts per bucket, split by the top <paramref name="topN"/> values of <paramref name="stack"/>
    /// (the rest summed as "Other"). Buckets with no events are present with zero counts.
    /// </summary>
    public TimeSeries Series(EventFilter f, TimeSpan bucket, Dimension stack, int topN = 10)
    {
        var extent = Extent(f);
        if (extent is null) return new TimeSeries([], bucket, []);
        var (from, where, args) = WhereClause(f, null, stack);

        var top = new List<string>();
        using (var cmd = store.Command($"SELECT {stack.Sql} AS k, count(*) n {from} {where} GROUP BY k ORDER BY n DESC, k LIMIT {topN + 1}", [.. args]))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) top.Add(r.GetString(0));
        var keys = top.Count > topN ? top[..topN] : top;
        var hasOther = top.Count > topN;

        var start = BucketStart(f.FromUtc ?? extent.Value.Min, bucket);
        var end = f.ToUtc ?? extent.Value.Max.AddTicks(1);
        var count = (int)Math.Max(1, Math.Ceiling((end - start).Ticks / (double)bucket.Ticks));
        var buckets = Enumerable.Range(0, count).Select(i => start + bucket * i).ToArray();
        var series = keys.Select(k => (Key: k, Counts: new long[count])).ToList();
        if (hasOther) series.Add(("Other", new long[count]));
        var index = series.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i);

        // Parameters bind in text order: the CASE's key list comes before the subquery's WHERE.
        var keySql = keys.Count == 0 ? "'Other'" : $"CASE WHEN raw IN ({string.Join(", ", keys.Select(_ => "?"))}) THEN raw ELSE 'Other' END";
        var allArgs = new List<object?>(keys);
        allArgs.AddRange(args);
        var sql = $"""
            SELECT b, {keySql} AS k, count(*) FROM (
                SELECT {BucketSql(bucket)} AS b, {stack.Sql} AS raw {from} {where}
            ) x
            GROUP BY b, k
            """;
        using (var cmd = store.Command(sql, [.. allArgs]))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var b = r.GetDateTime(0);
                var i = (int)((b - start).Ticks / bucket.Ticks);
                if (i < 0 || i >= count) continue;
                if (index.TryGetValue(r.GetString(1), out var si)) series[si].Counts[i] += Convert.ToInt64(r.GetValue(2));
            }
        return new TimeSeries(buckets, bucket, series);
    }

    // ------------------------------------------------------------------ aggregate grid

    /// <summary>
    /// Rows for one level of the grid: the values of <paramref name="dim"/> under the filter and the parent
    /// path, with first/last time, the busiest bucket and a <see cref="TrendBins"/>-point trend over the range.
    /// </summary>
    public List<GroupRow> Groups(EventFilter f, IReadOnlyList<(Dimension Dim, string Value)> path, Dimension dim, TimeSpan bucket, int limit = 500)
    {
        var (from, where, args) = WhereClause(f, path, dim);
        var stats = new List<(string V, long N, DateTime First, DateTime Last)>();
        using (var cmd = store.Command(
                   $"SELECT {dim.Sql} AS v, count(*) n, min(e.ts_utc), max(e.ts_utc) {from} {where} GROUP BY v ORDER BY n DESC, v LIMIT {limit}", [.. args]))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) stats.Add((r.GetString(0), Convert.ToInt64(r.GetValue(1)), r.GetDateTime(2), r.GetDateTime(3)));
        if (stats.Count == 0) return [];

        var rangeStart = f.FromUtc ?? stats.Min(x => x.First);
        var rangeEnd = f.ToUtc ?? stats.Max(x => x.Last).AddTicks(1);
        var binMs = Math.Max(1.0, (rangeEnd - rangeStart).TotalMilliseconds / TrendBins);
        var rangeStartSql = rangeStart.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        var peaks = new Dictionary<string, (long N, DateTime At)>();
        var trends = stats.ToDictionary(x => x.V, _ => new double[TrendBins]);
        var valueArgs = new List<object?>(args);
        valueArgs.AddRange(stats.Select(x => x.V));
        var inValues = $"{dim.Sql} IN ({string.Join(", ", stats.Select(_ => "?"))})";
        var whereValues = (where.Length == 0 ? "WHERE " : where + " AND ") + inValues;

        // Peak = busiest window; ties resolve to the earliest window so the result is stable.
        using (var cmd = store.Command($"""
                   SELECT v, min(b) FILTER (WHERE n = mx), max(mx) FROM (
                       SELECT v, b, n, max(n) OVER (PARTITION BY v) AS mx FROM (
                           SELECT {dim.Sql} AS v, {BucketSql(bucket)} AS b, count(*) AS n {from} {whereValues} GROUP BY v, b
                       )
                   ) GROUP BY v
                   """, [.. valueArgs]))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) peaks[r.GetString(0)] = (Convert.ToInt64(r.GetValue(2)), r.GetDateTime(1));

        using (var cmd = store.Command($"""
                   SELECT {dim.Sql} AS v,
                          CAST(floor(epoch_ms(e.ts_utc - TIMESTAMP '{rangeStartSql}') / {binMs.ToString(CultureInfo.InvariantCulture)}) AS INTEGER) AS bin,
                          count(*)
                   {from} {whereValues} GROUP BY v, bin
                   """, [.. valueArgs]))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var bin = Math.Clamp(r.GetInt32(1), 0, TrendBins - 1);
                if (trends.TryGetValue(r.GetString(0), out var t)) t[bin] += Convert.ToInt64(r.GetValue(2));
            }

        return stats.Select(x =>
        {
            var p = peaks.TryGetValue(x.V, out var found) ? found : (N: x.N, At: x.First);
            return new GroupRow(x.V, x.N, x.First, x.Last, p.N, p.At, trends[x.V]);
        }).ToList();
    }

    // ------------------------------------------------------------------ events

    private const string EventColumns = """
        e.event_id, e.ts_utc, s.name, e.category, e.class_method,
        CASE e.severity WHEN 2 THEN 'Error' WHEN 1 THEN 'Warning' ELSE 'Info' END, e.file_name, e.message, e.nested_count
        """;

    private static EventRow ReadEvent(System.Data.Common.DbDataReader r) => new(
        r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? "" : r.GetString(7), r.IsDBNull(8) ? 0 : r.GetInt32(8));

    public long CountEvents(EventFilter f, IReadOnlyList<(Dimension Dim, string Value)>? path = null)
    {
        var (from, where, args) = WhereClause(f, path);
        return store.Scalar<long>($"SELECT count(*) {from} {where}", [.. args]);
    }

    /// <summary>A page of events in time order (ties broken by server log order).</summary>
    public List<EventRow> Events(EventFilter f, IReadOnlyList<(Dimension Dim, string Value)>? path, int offset, int limit)
    {
        var (from, where, args) = WhereClause(f, path);
        var rows = new List<EventRow>();
        using var cmd = store.Command(
            $"SELECT {EventColumns} {from} {where} ORDER BY e.ts_utc, e.event_id LIMIT {limit} OFFSET {offset}", [.. args]);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(ReadEvent(r));
        return rows;
    }

    public EventDetail? Detail(long eventId)
    {
        using var cmd = store.Command($"""
            SELECT {EventColumns}, c.grp, c.note, e.ts_local, e.line_no, f.file_name, f.nested_dropped, n.nested_text,
                   e.thread, e.module, e.src_line, e.conn_id, e.cs_id, e.cs_name, e.replica_set_id, e.rg_name,
                   e.uid_db, e.uid_ver, e.gvsn_db, e.gvsn_ver, e.parent_db, e.parent_ver, e.fid, e.extension, e.attributes,
                   e.is_folder, e.present, e.meet_install, e.usn_reason, e.error_code, e.error_code_inner, e.event_log_id,
                   e.transfer_type, e.staged_size, e.retries, e.update_type,
                   od.server, ud.server, nc.partner, nc.inbound, nrf.name, nrg.name
            {AllNamesFrom}
            JOIN log_file f ON f.file_id = e.file_id
            LEFT JOIN event_nested n ON n.event_id = e.event_id
            WHERE e.event_id = ?
            """, eventId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var row = ReadEvent(r);
        string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
        string? Version(int g, int v) => S(g) is { } guid ? $"{{{guid.ToUpperInvariant()}}}-v{S(v)}" : null;

        // Column indexes: 0-8 EventColumns, 9 grp, 10 note, 11 ts_local, 12 line_no, 13 log file name,
        // 14 nested_dropped, 15 nested_text, 16-43 event fields in SELECT order, 44-49 resolved names
        // (origin server, created-on server, partner, inbound, RF name, RG name).
        string? G(int i) => S(i) is { } g ? "{" + g.ToUpperInvariant() + "}" : null;
        string? Named(string? value, string? name) => value is null ? null : name is null ? value : $"{value}   ({name})";
        var direction = r.IsDBNull(47) ? null : r.GetBoolean(47) ? "from " : "to ";
        var fields = new List<(string, string?)>
        {
            ("Thread", S(16)), ("Module", S(17)), ("Source line", S(18)),
            ("Connection", Named(G(19), S(46) is { } partner ? direction + partner : null)),
            ("Replicated folder", Named(G(20), S(21) ?? S(48))),
            ("Replication group", S(23) is { } rg ? (G(22) is { } rgId ? $"{rgId}   ({rg})" : rg) : S(49)),
            ("UID", Named(Version(24, 25), S(45) is { } c ? "created on " + c : null)),
            ("GVSN", Named(Version(26, 27), S(44) is { } o ? "last changed on " + o : null)),
            ("Parent", Version(28, 29)),
            ("FID", r.IsDBNull(30) ? null : "0x" + r.GetInt64(30).ToString("X", CultureInfo.InvariantCulture)),
            ("Extension", S(31)),
            ("Attributes", r.IsDBNull(32) ? null : "0x" + r.GetInt32(32).ToString("X", CultureInfo.InvariantCulture)),
            ("Folder", S(33)), ("Present", S(34)), ("Arrived by replication", S(35)), ("USN reason", S(36)),
            ("Error code", S(37)), ("Innermost error", S(38)), ("Event ID", S(39)), ("Transfer", S(40)),
            ("Staged size", S(41)), ("Retries", S(42)), ("Update type", S(43)),
        };
        return new EventDetail(row,
            fields.Where(x => x.Item2 is not null).Select(x => (x.Item1, x.Item2!)).ToList(),
            S(15), !r.IsDBNull(14) && r.GetBoolean(14), S(9), S(10), r.GetDateTime(11), r.GetInt32(12), r.GetString(13));
    }
}
