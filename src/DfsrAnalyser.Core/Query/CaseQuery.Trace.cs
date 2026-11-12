using System.Globalization;
using System.Text.RegularExpressions;

namespace DfsrAnalyser.Core.Query;

/// <summary>A UID found for a file name search, with what the case holds for it.</summary>
public sealed record UidMatch(string Uid, string Names, string? CreatedOn, long Events, int Servers, int Versions, DateTime First, DateTime Last);

/// <param name="Stage">The category's group (Local change, Outbound, Inbound, Install…).</param>
/// <param name="ViaFid">No UID on the line; linked to the file by its FID on the same server (e.g. the USN-record line of a local change).</param>
public sealed record TraceEvent(long EventId, DateTime TsUtc, string Server, string Category, string Stage, bool Hidden,
    string ClassMethod, string Severity, string? FileName, string Message, string? Gvsn, string? Connection, bool ViaFid);

/// <summary>One version's arrival at a server other than its origin. Times are UTC; null when the logs do not show the step.</summary>
public sealed record TraceDelivery(string Server, DateTime? ReceivedAt, DateTime? TransferredAt, string? TransferType, DateTime? InstalledAt, string? Outcome);

/// <summary>One GVSN of the file: where and when it was made, and when each partner received and installed it.</summary>
public sealed record TraceVersion(string Gvsn, string? Origin, DateTime FirstSeen, DateTime? ChangedAt, string? ChangeKind,
    DateTime? OfferedAt, IReadOnlyList<TraceDelivery> Deliveries)
{
    /// <summary>Reference time for latencies: the change on the origin, or the first sighting when the origin's log is missing.</summary>
    public DateTime BaseTime => ChangedAt ?? FirstSeen;
}

public sealed record FileTraceResult(string Uid, IReadOnlyList<string> Names, string? CreatedOn,
    IReadOnlyList<TraceVersion> Versions, IReadOnlyList<TraceEvent> Events, bool Truncated);

public sealed partial class CaseQuery
{
    public const int MaxTraceEvents = 50_000;

    private static readonly Regex UidPattern = new(@"^\s*\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?-v(\d+)\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>Parses <c>{GUID}-vN</c> (braces optional), as DFSR prints a UID or GVSN.</summary>
    public static bool TryParseUid(string? text, out Guid db, out long version)
    {
        db = default;
        version = 0;
        var m = text is null ? null : UidPattern.Match(text);
        if (m is not { Success: true }) return false;
        db = Guid.Parse(m.Groups[1].Value);
        return long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    public static string FormatUid(Guid db, long version) =>
        "{" + db.ToString().ToUpperInvariant() + "}-v" + version.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// UIDs whose events carry a matching file name (a wildcard pattern as in the file name filter, or a UID),
    /// optionally only events inside <paramref name="fromUtc"/>..<paramref name="toUtc"/>. Ordered by first sighting.
    /// </summary>
    public List<UidMatch> FindUids(string text, DateTime? fromUtc = null, DateTime? toUtc = null, int limit = 200)
    {
        var args = new List<object?>();
        string match;
        if (TryParseUid(text, out var db, out var ver))
        {
            match = "uid_db = ? AND uid_ver = ?";
            args.Add(db);
            args.Add(ver);
        }
        else
        {
            match = FileNamePattern.Parse(text).ToSql("file_name", args) ?? "FALSE";
            match = $"uid_db IS NOT NULL AND {match}";
        }
        if (fromUtc is { } from) { match += " AND ts_utc >= ?"; args.Add(from); }
        if (toUtc is { } to) { match += " AND ts_utc < ?"; args.Add(to); }

        var list = new List<UidMatch>();
        using var cmd = store.Command($"""
            SELECT e.uid_db, e.uid_ver,
                   string_agg(DISTINCT e.file_name, '; ' ORDER BY e.file_name),
                   any_value(ud.server), count(*), count(DISTINCT e.server_id),
                   count(DISTINCT CAST(e.gvsn_db AS VARCHAR) || '-' || CAST(e.gvsn_ver AS VARCHAR)),
                   min(e.ts_utc), max(e.ts_utc)
            FROM event e
            LEFT JOIN name_db ud ON ud.guid = e.uid_db
            WHERE (e.uid_db, e.uid_ver) IN (SELECT DISTINCT uid_db, uid_ver FROM event WHERE {match})
            GROUP BY e.uid_db, e.uid_ver
            ORDER BY min(e.ts_utc), e.uid_db, e.uid_ver
            LIMIT {limit}
            """, [.. args]);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UidMatch(FormatUid(r.GetGuid(0), r.GetInt64(1)), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                Convert.ToInt64(r.GetValue(4)), Convert.ToInt32(r.GetValue(5)), Convert.ToInt32(r.GetValue(6)), r.GetDateTime(7), r.GetDateTime(8)));
        return list;
    }

    /// <summary>
    /// Every event of one file (UID) on every server in UTC order, plus same-server events without a UID that
    /// share its FID within its time span, and per version the milestones from the log signatures of §7 of the
    /// reference. Hidden categories are included (flagged); <paramref name="fromUtc"/>/<paramref name="toUtc"/>
    /// optionally limit the events.
    /// </summary>
    public FileTraceResult Trace(Guid uidDb, long uidVer, DateTime? fromUtc = null, DateTime? toUtc = null, int maxEvents = MaxTraceEvents)
    {
        var args = new List<object?> { uidDb, uidVer };
        var time = "";
        if (fromUtc is not null) time += " AND e.ts_utc >= ?";
        if (toUtc is not null) time += " AND e.ts_utc < ?";

        // The FID link: within [first - 5 s, last + 5 s] of the UID's events on that server.
        var sql = $"""
            WITH u AS (SELECT event_id, server_id, ts_utc, fid FROM event WHERE uid_db = ? AND uid_ver = ?),
            spans AS (
                SELECT server_id, min(ts_utc) - INTERVAL 5 SECOND AS lo, max(ts_utc) + INTERVAL 5 SECOND AS hi,
                       list(DISTINCT fid) FILTER (WHERE fid IS NOT NULL) AS fids
                FROM u GROUP BY server_id
            ),
            picked AS (
                SELECT event_id, FALSE AS via FROM u
                UNION ALL
                SELECT e.event_id, TRUE FROM event e JOIN spans p ON p.server_id = e.server_id
                WHERE e.uid_db IS NULL AND e.fid IS NOT NULL AND list_contains(p.fids, e.fid) AND e.ts_utc BETWEEN p.lo AND p.hi
            )
            SELECT e.event_id, e.ts_utc, s.name, coalesce(e.category, ''), coalesce(c.grp, 'Other'), coalesce(c.hidden, FALSE),
                   coalesce(e.class_method, ''), CASE e.severity WHEN 2 THEN 'Error' WHEN 1 THEN 'Warning' ELSE 'Info' END,
                   e.file_name, coalesce(e.message, ''), e.gvsn_db, e.gvsn_ver, e.transfer_type, nc.partner, nc.inbound, p.via
            FROM picked p
            JOIN event e ON e.event_id = p.event_id
            JOIN server s ON s.server_id = e.server_id
            LEFT JOIN category c ON c.name = e.category
            LEFT JOIN name_conn nc ON nc.conn_id = e.conn_id AND nc.server_id = e.server_id
            WHERE TRUE {time}
            ORDER BY e.ts_utc, e.event_id
            LIMIT {maxEvents + 1}
            """;
        if (fromUtc is { } f2) args.Add(f2);
        if (toUtc is { } t2) args.Add(t2);

        var rows = new List<(TraceEvent Ev, Guid? GDb, long? GVer, string? Transfer)>();
        using (var cmd = store.Command(sql, [.. args]))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                Guid? gdb = r.IsDBNull(10) ? null : r.GetGuid(10);
                long? gver = r.IsDBNull(11) ? null : r.GetInt64(11);
                var conn = r.IsDBNull(13) ? null : (r.IsDBNull(14) ? "" : r.GetBoolean(14) ? "from " : "to ") + r.GetString(13);
                var ev = new TraceEvent(r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5),
                    r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9),
                    gdb is { } g && gver is { } v ? FormatUid(g, v) : null, conn, r.GetBoolean(15));
                rows.Add((ev, gdb, gver, r.IsDBNull(12) ? null : r.GetString(12)));
            }
        var truncated = rows.Count > maxEvents;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var dbServer = new Dictionary<Guid, string>();
        using (var cmd = store.Command("SELECT guid, server FROM name_db WHERE NOT ambiguous"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) dbServer[r.GetGuid(0)] = r.GetString(1);

        // Lines without a GVSN (FID-linked USN records) take the version of the nearest same-server line that has one.
        var events = new List<(TraceEvent Ev, string? Gvsn, Guid? GDb, string? Transfer)>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var (ev, gdb, _, transfer) = rows[i];
            if (ev.Gvsn is not null || !ev.ViaFid) { events.Add((ev, ev.Gvsn, gdb, transfer)); continue; }
            (string Gvsn, Guid Db, double Gap)? best = null;
            foreach (var step in (ReadOnlySpan<int>)[-1, 1])
                for (var j = i + step; j >= 0 && j < rows.Count; j += step)
                {
                    var o = rows[j];
                    var gap = Math.Abs((o.Ev.TsUtc - ev.TsUtc).TotalSeconds);
                    if (gap > 2) break; // rows are in time order
                    if (o.Ev.Gvsn is null || o.Ev.Server != ev.Server) continue;
                    if (best is null || gap < best.Value.Gap) best = (o.Ev.Gvsn, o.GDb!.Value, gap);
                }
            var assigned = best?.Gvsn;
            events.Add((ev with { Gvsn = assigned }, assigned, best?.Db, transfer));
        }

        var versions = new List<TraceVersion>();
        foreach (var g in events.Where(e => e.Gvsn is not null).GroupBy(e => e.Gvsn!).OrderBy(g => g.Min(e => e.Ev.TsUtc)))
        {
            var evs = g.Select(e => e.Ev).ToList();
            var gdb = g.First(e => e.GDb is not null).GDb!.Value;
            var changes = evs.Where(e => e.ClassMethod.StartsWith("UsnConsumer::", StringComparison.Ordinal)).ToList();
            var origin = dbServer.TryGetValue(gdb, out var o) ? o : changes.FirstOrDefault()?.Server;
            var originChanges = changes.Where(e => origin is null || e.Server == origin).ToList();
            var changed = originChanges.Count > 0 ? originChanges.Min(e => e.TsUtc) : (DateTime?)null;
            var kind = (originChanges.FirstOrDefault(e => !e.Hidden) ?? originChanges.FirstOrDefault())?.Category;
            var offered = evs.Where(e => e.ClassMethod == "Join::SubmitUpdate" && e.Message.StartsWith("Sent:", StringComparison.Ordinal))
                .Select(e => (DateTime?)e.TsUtc).Min();

            var deliveries = new List<TraceDelivery>();
            foreach (var s in g.Where(e => e.Ev.Server != origin).GroupBy(e => e.Ev.Server).OrderBy(s => s.Min(e => e.Ev.TsUtc)))
            {
                var received = s.Where(e => e.Ev.ClassMethod == "InConnection::ReceiveUpdates").Select(e => (DateTime?)e.Ev.TsUtc).Min();
                var transfer = s.Where(e => e.Ev.ClassMethod == "InConnection::LogTransferActivity").OrderBy(e => e.Ev.TsUtc).FirstOrDefault();
                var install = s.Select(e => e.Ev).Where(IsInstallDone).OrderBy(e => e.TsUtc).FirstOrDefault();
                if (received is null && transfer.Ev is null && install is null) continue;
                deliveries.Add(new TraceDelivery(s.Key, received, transfer.Ev?.TsUtc, transfer.Transfer, install?.TsUtc, install?.Category));
            }
            versions.Add(new TraceVersion(g.Key, origin, evs.Min(e => e.TsUtc), changed, kind, offered, deliveries));
        }

        var names = events.Select(e => e.Ev.FileName).Where(n => n is not null).Distinct(StringComparer.OrdinalIgnoreCase).Select(n => n!).ToList();
        var createdOn = dbServer.TryGetValue(uidDb, out var c) ? c : null;
        return new FileTraceResult(FormatUid(uidDb, uidVer), names, createdOn, versions, events.Select(e => e.Ev).ToList(), truncated);
    }

    private static readonly HashSet<string> InstallMethods = new(StringComparer.Ordinal)
    {
        "Meet::InstallRename", "Meet::InstallTombstone", "Meet::InstallMove", "Meet::InstallOverwrite",
    };

    /// <summary>"-> DONE" of an install step: the version is live (or its delete applied) on that server.</summary>
    private static bool IsInstallDone(TraceEvent e) =>
        InstallMethods.Contains(e.ClassMethod) && e.Message.StartsWith("-> DONE", StringComparison.Ordinal);
}
