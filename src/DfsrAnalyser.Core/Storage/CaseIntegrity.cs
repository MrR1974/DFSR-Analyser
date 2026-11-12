using System.Globalization;

namespace DfsrAnalyser.Core.Storage;

public enum FindingLevel { Info, Warning, Defect }

public sealed record IntegrityFinding(FindingLevel Level, string Message);

public enum FileMatch
{
    /// <summary>Same events, categories and detail.</summary>
    Identical,
    /// <summary>One copy has more lines (an active log copied later); identical over the lines both hold.</summary>
    SamePrefix,
    Differs,
    OnlyInA,
    OnlyInB,
}

public sealed record FileComparison(
    string Server, int Index, string? FileA, string? FileB, long? EventsA, long? EventsB,
    long CommonEvents, bool ContentSame, bool CategoriesSame, bool DetailSame, FileMatch Match, string? Note);

/// <summary>
/// Consistency checks over one case (<see cref="Verify"/>) and an event-for-event comparison of two cases
/// (<see cref="Compare"/>), used to prove that importing and collecting the same logs give the same case.
/// </summary>
public static class CaseIntegrity
{
    /// <summary>A second's tolerance for events logged out of order around a file's first/last event.</summary>
    private const string SpanTolerance = "INTERVAL 1 SECOND";

    public static List<IntegrityFinding> Verify(CaseStore store)
    {
        var f = new List<IntegrityFinding>();
        void Defect(string m) => f.Add(new(FindingLevel.Defect, m));
        void Warn(string m) => f.Add(new(FindingLevel.Warning, m));
        long Count(string sql) => store.Scalar<long>(sql);
        IEnumerable<object?[]> Rows(string sql) => store.Query(sql).Skip(1);

        var servers = Count("SELECT count(*) FROM server");
        var files = Count("SELECT count(*) FROM log_file WHERE idx >= 0");
        var events = Count("SELECT count(*) FROM event");
        f.Add(new(FindingLevel.Info, $"{servers} server(s), {files} debug log file(s), {events:N0} events."));

        if (Count("SELECT count(*) FROM (SELECT event_id FROM event GROUP BY event_id HAVING count(*) > 1)") is > 0 and var dupEvents)
            Defect($"{dupEvents:N0} event id(s) occur more than once.");

        foreach (var r in Rows("""
                     SELECT s.name, f.idx, count(*) FROM log_file f JOIN server s USING (server_id)
                     GROUP BY s.name, f.idx, f.first_line_hash HAVING count(*) > 1 ORDER BY 1, 2
                     """))
            Defect($"{r[0]}: Index {r[1]} is registered {r[2]} times.");

        foreach (var r in Rows("""
                     SELECT s.name, f.idx, count(DISTINCT f.first_line_hash) FROM log_file f JOIN server s USING (server_id)
                     WHERE f.idx >= 0 GROUP BY s.name, f.idx HAVING count(DISTINCT f.first_line_hash) > 1 ORDER BY 1, 2
                     """))
            f.Add(new(FindingLevel.Info, $"{r[0]}: Index {r[1]} holds {r[2]} different logs (Index wrap-around, or logs from two computers under one name)."));

        if (Count("SELECT count(*) FROM event e LEFT JOIN log_file f USING (file_id) WHERE f.file_id IS NULL OR f.server_id <> e.server_id") is > 0 and var stray)
            Defect($"{stray:N0} event(s) belong to no log file, or to another server's file.");

        if (Count("SELECT count(*) FROM event WHERE event_id <> ((CAST(file_id AS BIGINT) << 32) | line_no)") is > 0 and var badIds)
            Defect($"{badIds:N0} event id(s) do not match their file and line.");

        foreach (var r in Rows("""
                     SELECT s.name, f.file_name, f.idx, coalesce(f.event_count, 0), count(e.event_id)
                     FROM log_file f JOIN server s USING (server_id) LEFT JOIN event e ON e.file_id = f.file_id
                     WHERE f.idx >= 0
                     GROUP BY s.name, f.file_name, f.idx, f.event_count
                     HAVING coalesce(f.event_count, 0) <> count(e.event_id) ORDER BY 1, 3
                     """))
            Defect($"{r[0]}: {r[1]} (Index {r[2]}) records {r[3]:N0} events but holds {r[4]:N0}.");

        if (Count("SELECT count(*) FROM event_nested n ANTI JOIN event e ON e.event_id = n.event_id") is > 0 and var lostDetail)
            Defect($"{lostDetail:N0} detail row(s) belong to no event.");
        if (Count("SELECT count(*) FROM (SELECT event_id FROM event_nested GROUP BY event_id HAVING count(*) > 1)") is > 0 and var dupDetail)
            Defect($"{dupDetail:N0} event(s) have more than one detail row.");

        foreach (var r in Rows("""
                     SELECT s.name, f.file_name, f.idx FROM log_file f JOIN server s USING (server_id)
                     WHERE f.idx >= 0 AND NOT f.is_complete
                       AND EXISTS (SELECT 1 FROM log_file g WHERE g.server_id = f.server_id AND g.idx > f.idx)
                     ORDER BY 1, 3
                     """))
            Defect($"{r[0]}: {r[1]} (Index {r[2]}) is marked as still growing although a later log exists.");

        foreach (var r in Rows($"""
                     SELECT s.name, f.file_name, f.idx, count(*) FROM event e JOIN log_file f USING (file_id) JOIN server s ON s.server_id = f.server_id
                     WHERE f.idx >= 0 AND (e.ts_utc < f.first_ts_utc - {SpanTolerance} OR e.ts_utc > f.last_ts_utc + {SpanTolerance})
                     GROUP BY 1, 2, 3 ORDER BY 1, 3
                     """))
            Warn($"{r[0]}: {r[3]:N0} event(s) in {r[1]} (Index {r[2]}) lie more than a second outside the file's first/last event times " +
                 "(a clock correction or daylight-saving change on that server?).");

        if (Count("SELECT count(*) FROM event WHERE category IS NULL") is > 0 and var uncategorised)
            Defect($"{uncategorised:N0} event(s) have no category (re-apply the rules).");

        foreach (var r in Rows("SELECT s.name, p.idx FROM pending_orphan p JOIN server s USING (server_id) ORDER BY 1, 2"))
            Warn($"{r[0]}: the opening lines of Index {r[1]} belong to Index {Convert.ToInt32(r[1], CultureInfo.InvariantCulture) - 1}, which is not in the case yet.");

        foreach (var r in Rows("""
                     SELECT name, idx, next_idx FROM (
                         SELECT s.name, f.idx, lead(f.idx) OVER (PARTITION BY f.server_id ORDER BY f.idx) AS next_idx
                         FROM (SELECT DISTINCT server_id, idx FROM log_file WHERE idx >= 0) f JOIN server s USING (server_id)
                     ) WHERE next_idx > idx + 1 ORDER BY 1, 2
                     """))
        {
            int a = Convert.ToInt32(r[1], CultureInfo.InvariantCulture) + 1, b = Convert.ToInt32(r[2], CultureInfo.InvariantCulture) - 1;
            Warn($"{r[0]}: Index {(a == b ? a.ToString(CultureInfo.InvariantCulture) : $"{a}–{b}")} not in the case (fine if those logs were deliberately left out).");
        }

        if (!f.Any(x => x.Level != FindingLevel.Info)) f.Add(new(FindingLevel.Info, "No defects found."));
        return f;
    }

    /// <summary>
    /// Compares the case open in <paramref name="a"/> with the case database at <paramref name="otherCaseFolder"/>
    /// (attached read-only). Files are matched by server name, Index and first-line fingerprint; shared files are
    /// compared over the lines both copies hold, so an active log copied at different times still compares.
    /// </summary>
    public static List<FileComparison> Compare(CaseStore a, string otherCaseFolder)
    {
        var otherDb = Path.Combine(Path.GetFullPath(otherCaseFolder), CaseStore.DatabaseFileName);
        if (!File.Exists(otherDb)) throw new FileNotFoundException($"No {CaseStore.DatabaseFileName} in {otherCaseFolder}.");
        if (string.Equals(otherDb, a.DatabasePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Both paths are the same case.");

        a.Execute($"ATTACH '{otherDb.Replace("'", "''")}' AS other (READ_ONLY)");
        try
        {
            var pairs = a.Query("""
                WITH fa AS (SELECT lower(s.name) AS srv, s.name AS sname, f.* FROM log_file f JOIN server s USING (server_id)),
                     fb AS (SELECT lower(s.name) AS srv, s.name AS sname, f.* FROM other.log_file f JOIN other.server s USING (server_id))
                SELECT coalesce(fa.sname, fb.sname), coalesce(fa.idx, fb.idx), fa.file_id, fb.file_id, fa.file_name, fb.file_name,
                       fa.event_count, fb.event_count, fa.last_event_line, fb.last_event_line, fa.nested_dropped, fb.nested_dropped
                FROM fa FULL OUTER JOIN fb ON fa.srv = fb.srv AND fa.idx = fb.idx AND fa.first_line_hash = fb.first_line_hash
                ORDER BY lower(coalesce(fa.sname, fb.sname)), coalesce(fa.idx, fb.idx), coalesce(fa.file_name, fb.file_name)
                """).Skip(1).ToList();

            var list = new List<FileComparison>();
            foreach (var r in pairs)
            {
                var server = (string)r[0]!;
                var idx = Convert.ToInt32(r[1], CultureInfo.InvariantCulture);
                int? idA = r[2] is null ? null : Convert.ToInt32(r[2], CultureInfo.InvariantCulture);
                int? idB = r[3] is null ? null : Convert.ToInt32(r[3], CultureInfo.InvariantCulture);
                long? evA = r[6] is null ? null : Convert.ToInt64(r[6], CultureInfo.InvariantCulture);
                long? evB = r[7] is null ? null : Convert.ToInt64(r[7], CultureInfo.InvariantCulture);
                var name = idx < 0 ? "(event log)" : null;
                if (idA is null || idB is null)
                {
                    list.Add(new FileComparison(server, idx, name ?? r[4] as string, name ?? r[5] as string, evA, evB, 0, false, false, false,
                        idA is null ? FileMatch.OnlyInB : FileMatch.OnlyInA, null));
                    continue;
                }

                var lastA = r[8] is null ? int.MaxValue : Convert.ToInt32(r[8], CultureInfo.InvariantCulture);
                var lastB = r[9] is null ? int.MaxValue : Convert.ToInt32(r[9], CultureInfo.InvariantCulture);
                var limit = Math.Min(lastA, lastB);
                var fa = Fingerprint(a, "", idA.Value, limit);
                var fb = Fingerprint(a, "other.", idB.Value, limit);

                var content = fa.Count == fb.Count && fa.Content == fb.Content;
                var categories = fa.Categories == fb.Categories;
                var detail = fa.Detail == fb.Detail;
                var droppedA = r[10] is true;
                var droppedB = r[11] is true;
                var match = !content || !categories ? FileMatch.Differs
                    : evA == evB ? FileMatch.Identical
                    : FileMatch.SamePrefix;
                string? note = null;
                if (match == FileMatch.SamePrefix) note = $"compared over the first {fa.Count:N0} events; {(evA > evB ? "A" : "B")} was copied later";
                if (!categories && content) note = "same events, different categories (different rules?)";
                if (!detail) note = (note is null ? "" : note + "; ") +
                                    (droppedA != droppedB ? "Plumbing detail dropped in one case" : "nested detail differs (a roll's opening lines stitched in one case only?)");
                list.Add(new FileComparison(server, idx, name ?? r[4] as string, name ?? r[5] as string, evA, evB, fa.Count, content, categories, detail, match, note));
            }
            return list;
        }
        finally
        {
            a.Execute("DETACH other");
        }
    }

    private readonly record struct Print(long Count, decimal Content, decimal Categories, decimal Detail);

    /// <summary>Order-independent hashes of a file's events up to <paramref name="maxLine"/>.</summary>
    private static Print Fingerprint(CaseStore store, string schema, int fileId, int maxLine)
    {
        using var cmd = store.Command($"""
            SELECT count(*),
                   coalesce(bit_xor(hash(e.line_no, e.ts_utc, e.severity, e.module, e.class_method, e.message, e.file_name,
                                         e.uid_db, e.uid_ver, e.gvsn_db, e.gvsn_ver, e.error_code, e.event_log_id)), 0),
                   coalesce(bit_xor(hash(e.line_no, e.category)), 0),
                   coalesce(bit_xor(hash(e.line_no, n.nested_text)), 0)
            FROM {schema}event e LEFT JOIN {schema}event_nested n ON n.event_id = e.event_id
            WHERE e.file_id = ? AND e.line_no <= ?
            """, fileId, maxLine);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new Print(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Convert.ToDecimal(r.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToDecimal(r.GetValue(2), CultureInfo.InvariantCulture), Convert.ToDecimal(r.GetValue(3), CultureInfo.InvariantCulture));
    }
}
