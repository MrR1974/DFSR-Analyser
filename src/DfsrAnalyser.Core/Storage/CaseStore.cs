using System.Data.Common;
using DfsrAnalyser.Core.Parsing;
using DfsrAnalyser.Core.Taxonomy;
using DuckDB.NET.Data;

namespace DfsrAnalyser.Core.Storage;

/// <summary>
/// A case: a folder holding <c>case.duckdb</c> and <c>rules.json</c>. Owns the connection and all SQL.
/// Not thread-safe; one importer or one UI session at a time.
/// </summary>
public sealed class CaseStore : IDisposable
{
    public const string DatabaseFileName = "case.duckdb";
    public const string RulesFileName = "rules.json";

    public string Folder { get; }
    public string DatabasePath { get; }
    public string RulesPath => Path.Combine(Folder, RulesFileName);
    internal DuckDBConnection Connection { get; }

    /// <summary>The rule set used to categorise imports (the case's rules.json).</summary>
    public RuleSet Rules { get; private set; } = new();
    private string _categoryExpr = RuleSql.FallbackExpression;

    private CaseStore(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
        DatabasePath = Path.Combine(Folder, DatabaseFileName);
        Connection = new DuckDBConnection($"Data Source={DatabasePath}");
        Connection.Open();
        Execute(Schema.Ddl);
        Migrate();
        Execute("INSERT INTO meta VALUES ('schema_version', ?) ON CONFLICT (key) DO UPDATE SET value = excluded.value", Schema.Version.ToString());

        if (!File.Exists(RulesPath)) File.WriteAllText(RulesPath, RuleSet.DefaultJson());
        UseRules(RuleSet.Load(RulesPath));
    }

    public static CaseStore Open(string folder) => new(folder);

    private void Migrate()
    {
        // v1 -> v2: categories and dropped-detail flag.
        Execute("ALTER TABLE log_file ADD COLUMN IF NOT EXISTS nested_dropped BOOLEAN DEFAULT FALSE");
    }

    // ---------- taxonomy ----------

    /// <summary>
    /// Validates a rule set (structure, and every regex against DuckDB's RE2) and makes it the one
    /// used for subsequent imports. Does not touch existing rows; see <see cref="ApplyRules"/>.
    /// </summary>
    public void UseRules(RuleSet rules)
    {
        rules.Validate();
        for (var i = 0; i < rules.Rules.Count; i++)
        {
            var r = rules.Rules[i];
            foreach (var (what, pattern) in new[] { ("message", r.Message), ("usnReason", r.UsnReason) })
            {
                if (pattern is null) continue;
                try { Scalar<bool>($"SELECT regexp_matches('', {RuleSql.Literal(pattern)})"); }
                catch (DuckDBException ex) { throw new RuleSetException($"Rule {i + 1} ({r.Category}): invalid {what} regex: {ex.Message}", ex); }
            }
        }
        Rules = rules;
        _categoryExpr = RuleSql.CaseExpression(rules);

        Execute("DELETE FROM category");
        var ord = 0;
        foreach (var c in rules.Categories)
            Execute("INSERT INTO category VALUES (?, ?, ?, ?, ?)", c.Name, c.Group, c.Hidden, c.Note, ord++);
    }

    /// <summary>Saves a rule set as the case's rules.json and re-categorises every event.</summary>
    public void SaveAndApplyRules(RuleSet rules)
    {
        UseRules(rules);
        File.WriteAllText(RulesPath, System.Text.Json.JsonSerializer.Serialize(rules, RuleSet.JsonOptions));
        ApplyRules();
    }

    /// <summary>Reloads rules.json (after the user edited it) and re-categorises every event.</summary>
    public void ReloadAndApplyRules()
    {
        UseRules(RuleSet.Load(RulesPath));
        ApplyRules();
    }

    /// <summary>Re-categorises existing events with the current rules (no re-parse).</summary>
    public void ApplyRules(int? fileId = null)
    {
        var where = fileId is null ? "" : $" WHERE file_id = {fileId.Value}";
        Execute($"UPDATE event SET category = {_categoryExpr}{where}");
        Correlate(fileId);
    }

    /// <summary>
    /// Context-dependent categories that a single line cannot decide. A move to ConflictAndDeleted is a
    /// conflict loss when a conflict for the same file is logged on the same server within 5 s, otherwise a
    /// remote delete when it happens inside a tombstone install on the same thread.
    /// </summary>
    public void Correlate(int? fileId = null)
    {
        const string pending = "'Moved to ConflictAndDeleted'";
        var scope = fileId is null ? "" : $" AND e.file_id = {fileId.Value}";
        Execute($"""
            UPDATE event e SET category = 'Moved to ConflictAndDeleted: conflict loser'
            WHERE e.category = {pending}{scope} AND EXISTS (
                SELECT 1 FROM event c
                WHERE c.server_id = e.server_id AND c.file_name = e.file_name
                  AND c.category IN ('Conflict detected', 'Conflict reported (4412)')
                  AND c.ts_utc BETWEEN e.ts_utc - INTERVAL 5 SECOND AND e.ts_utc + INTERVAL 5 SECOND)
            """);
        Execute($"""
            UPDATE event e SET category = 'Moved to ConflictAndDeleted: remote delete'
            WHERE e.category = {pending}{scope} AND EXISTS (
                SELECT 1 FROM event t
                WHERE t.server_id = e.server_id AND t.thread = e.thread AND t.file_name = e.file_name
                  AND t.class_method = 'Meet::InstallTombstone'
                  AND t.ts_utc BETWEEN e.ts_utc - INTERVAL 2 SECOND AND e.ts_utc + INTERVAL 2 SECOND)
            """);
    }

    /// <summary>Categorises one event (after a single-event rewrite).</summary>
    public void CategoriseEvent(long eventId) =>
        Execute($"UPDATE event SET category = {_categoryExpr} WHERE event_id = ?", eventId);

    /// <summary>Category names in the Plumbing group (nested detail optional).</summary>
    public string PlumbingCategoriesSql =>
        "(SELECT name FROM category WHERE grp = " + RuleSql.Literal(RuleSet.PlumbingGroup) + ")";

    public void Dispose() => Connection.Dispose();

    // ---------- servers ----------

    public int GetOrAddServer(string name)
    {
        var existing = Scalar<int?>("SELECT server_id FROM server WHERE lower(name) = lower(?)", name);
        if (existing is { } id) return id;
        var next = Scalar<int>("SELECT coalesce(max(server_id), 0) + 1 FROM server");
        Execute("INSERT INTO server VALUES (?, ?)", next, name);
        return next;
    }

    public void AddAlias(int serverId, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;
        Execute("INSERT INTO server_alias VALUES (?, ?) ON CONFLICT DO NOTHING", serverId, alias);
    }

    // ---------- files ----------

    public LogFileRecord? FindFile(int serverId, int index, long firstLineHash) =>
        QueryFiles("WHERE server_id = ? AND idx = ? AND first_line_hash = ?", serverId, index, firstLineHash).FirstOrDefault();

    /// <summary>Most recently imported file with this Index (Index can wrap, so newest wins).</summary>
    public LogFileRecord? FindLatestByIndex(int serverId, int index) =>
        QueryFiles("WHERE server_id = ? AND idx = ? ORDER BY imported_at DESC, file_id DESC", serverId, index).FirstOrDefault();

    public IReadOnlyList<LogFileRecord> ListFiles() => QueryFiles("ORDER BY server_id, idx, file_id");

    public bool HasHigherIndex(int serverId, int index) =>
        Scalar<long>("SELECT count(*) FROM log_file WHERE server_id = ? AND idx > ?", serverId, index) > 0;

    public void MarkEarlierComplete(int serverId, int index) =>
        Execute("UPDATE log_file SET is_complete = TRUE WHERE server_id = ? AND idx < ? AND NOT is_complete", serverId, index);

    public int AddFile(LogFileRecord f)
    {
        f.FileId = Scalar<int>("SELECT coalesce(max(file_id), 0) + 1 FROM log_file");
        Execute("""
            INSERT INTO log_file (file_id, server_id, file_name, idx, first_line_hash, is_complete)
            VALUES (?, ?, ?, ?, ?, FALSE)
            """, f.FileId, f.ServerId, f.FileName, f.Index, f.FirstLineHash);
        UpdateFile(f);
        return f.FileId;
    }

    public void UpdateFile(LogFileRecord f) => Execute("""
        UPDATE log_file SET
            file_name = ?, source_path = ?, size_bytes = ?, header_computer = ?, tz_id = ?, tz_name = ?, tz_basis = ?,
            log_level = ?, max_entry_count = ?, max_file_count = ?, sequence = ?, first_ts_utc = ?, last_ts_utc = ?,
            line_count = ?, event_count = ?, orphan_lines = ?, unrecognised = ?, last_event_line = ?, last_event_id = ?,
            nested_dropped = ?, is_complete = ?, imported_at = ?
        WHERE file_id = ?
        """,
        f.FileName, f.SourcePath, f.SizeBytes, f.HeaderComputer, f.TzId, f.TzName, f.TzBasis,
        f.LogLevel, f.MaxEntryCount, f.MaxFileCount, f.Sequence, f.FirstTsUtc, f.LastTsUtc,
        f.LineCount, f.EventCount, f.OrphanLines, f.Unrecognised, f.LastEventLine, f.LastEventId,
        f.NestedDropped, f.IsComplete, f.ImportedAt, f.FileId);

    private List<LogFileRecord> QueryFiles(string tail, params object?[] args)
    {
        var list = new List<LogFileRecord>();
        using var cmd = Command($"""
            SELECT file_id, server_id, file_name, source_path, size_bytes, header_computer, tz_id, tz_name, tz_basis,
                   log_level, max_entry_count, max_file_count, sequence, idx, first_line_hash, first_ts_utc, last_ts_utc,
                   line_count, event_count, orphan_lines, unrecognised, last_event_line, last_event_id, is_complete, imported_at, nested_dropped
            FROM log_file {tail}
            """, args);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LogFileRecord
            {
                FileId = r.GetInt32(0),
                ServerId = r.GetInt32(1),
                FileName = r.GetString(2),
                SourcePath = Get<string>(r, 3),
                SizeBytes = Get<long?>(r, 4),
                HeaderComputer = Get<string>(r, 5),
                TzId = Get<string>(r, 6),
                TzName = Get<string>(r, 7),
                TzBasis = Get<string>(r, 8),
                LogLevel = Get<int?>(r, 9),
                MaxEntryCount = Get<int?>(r, 10),
                MaxFileCount = Get<int?>(r, 11),
                Sequence = Get<int?>(r, 12),
                Index = r.GetInt32(13),
                FirstLineHash = r.GetInt64(14),
                FirstTsUtc = Get<DateTime?>(r, 15),
                LastTsUtc = Get<DateTime?>(r, 16),
                LineCount = Get<int?>(r, 17),
                EventCount = Get<int?>(r, 18),
                OrphanLines = Get<int?>(r, 19),
                Unrecognised = Get<int?>(r, 20),
                LastEventLine = Get<int?>(r, 21),
                LastEventId = Get<long?>(r, 22),
                IsComplete = r.GetBoolean(23),
                ImportedAt = Get<DateTime?>(r, 24),
                NestedDropped = Get<bool?>(r, 25) ?? false,
            });
        }
        return list;
    }

    // ---------- events ----------

    /// <summary>
    /// Event ids are derived, not allocated: file id in the high 32 bits, dated line number in the low.
    /// Files can then be parsed in parallel, and a resumed or re-stitched event keeps its id.
    /// </summary>
    public static long EventId(int fileId, int lineNo) => ((long)fileId << 32) | (uint)lineNo;

    public int CountEvents(int fileId) => (int)Scalar<long>("SELECT count(*) FROM event WHERE file_id = ?", fileId);

    /// <summary>Folder for temporary CSV files produced during import.</summary>
    public string TempFolder => Path.Combine(Folder, "tmp");

    /// <summary>
    /// Loads CSV files produced by <see cref="CsvBulkWriter"/> using DuckDB's native CSV reader, assigning
    /// categories with the current rules as rows are inserted. When <paramref name="dropPlumbingDetail"/>
    /// is set, nested text of Plumbing-category events is not stored.
    /// </summary>
    public void LoadCsv(CsvBulkWriter csv, int fileId, bool dropPlumbingDetail = false)
    {
        if (HasData(csv.EventPath))
        {
            Execute($"""
                INSERT INTO event
                SELECT * REPLACE ({_categoryExpr} AS category)
                FROM {ReadCsv(csv.EventPath, Schema.EventColumns)}
                """);
        }
        if (HasData(csv.NestedPath))
        {
            var filter = dropPlumbingDetail
                ? $"""
                   WHERE n.event_id NOT IN (SELECT event_id FROM event WHERE file_id = {fileId} AND category IN {PlumbingCategoriesSql})
                   """
                : "";
            Execute($"INSERT INTO event_nested SELECT n.* FROM {ReadCsv(csv.NestedPath, Schema.NestedColumns)} n {filter}");
        }

        static bool HasData(string p) => File.Exists(p) && new FileInfo(p).Length > 0;
    }

    private static string ReadCsv(string path, (string Name, string Type)[] columns) =>
        $"read_csv({RuleSql.Literal(path)}, columns = {Schema.CsvColumnSpec(columns)}, header = false, delim = ',', " +
        "quote = '\"', escape = '\"', nullstr = '', allow_quoted_nulls = false, auto_detect = false)";

    /// <summary>Row appender for writing a handful of events.</summary>
    public IEventWriter OpenSmallWriter() => new EventWriter(Connection);

    public void DeleteFile(int fileId)
    {
        Execute("DELETE FROM event_nested WHERE event_id IN (SELECT event_id FROM event WHERE file_id = ?)", fileId);
        Execute("DELETE FROM event WHERE file_id = ?", fileId);
        Execute("DELETE FROM log_file WHERE file_id = ?", fileId);
    }

    /// <summary>Removes the events of a file from a dated line onwards (resuming an active log).</summary>
    public void DeleteEventsFrom(int fileId, int fromLine)
    {
        Execute("DELETE FROM event_nested WHERE event_id IN (SELECT event_id FROM event WHERE file_id = ? AND line_no >= ?)", fileId, fromLine);
        Execute("DELETE FROM event WHERE file_id = ? AND line_no >= ?", fileId, fromLine);
    }

    /// <summary>Reads one event back into raw form (for re-extraction after its nested lines change).</summary>
    public StoredEvent? LoadEvent(long eventId)
    {
        using var cmd = Command("""
            SELECT e.file_id, e.server_id, e.line_no, e.ts_utc, e.ts_local, e.thread, e.module, e.src_line, e.severity,
                   e.class_method, e.message, n.nested_text
            FROM event e LEFT JOIN event_nested n ON n.event_id = e.event_id
            WHERE e.event_id = ?
            """, eventId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var raw = new RawEvent
        {
            LineNo = r.GetInt32(2),
            LocalTime = r.GetDateTime(4),
            Thread = Get<int?>(r, 5) ?? 0,
            Module = Get<string>(r, 6) ?? "",
            SourceLine = Get<int?>(r, 7) ?? 0,
            Severity = (Severity)(Get<byte?>(r, 8) ?? 0),
            ClassMethod = Get<string>(r, 9) ?? "",
            Message = Get<string>(r, 10) ?? "",
        };
        if (Get<string>(r, 11) is { } nested) raw.Nested.AddRange(nested.Split('\n'));
        return new StoredEvent(eventId, r.GetInt32(0), r.GetInt32(1), r.GetDateTime(3), raw);
    }

    public void DeleteEvent(long eventId)
    {
        Execute("DELETE FROM event_nested WHERE event_id = ?", eventId);
        Execute("DELETE FROM event WHERE event_id = ?", eventId);
    }

    // ---------- Windows event log ----------

    /// <summary>Index used for the synthetic log_file row holding a server's DFS Replication event-log records.</summary>
    public const int EventLogFileIndex = -1;
    public const string EventLogModule = "EVTLOG";

    /// <summary>
    /// Stores DFS Replication event-log records as events (module <see cref="EventLogModule"/>) under a synthetic
    /// log file per server, so they share the timeline, filters and grid with debug-log events. Records already
    /// stored for the same server are replaced (event id = file id + record id). Returns the number stored.
    /// </summary>
    public int ImportEventLog(string serverName, IReadOnlyList<Collection.EventLogRecordData> records)
    {
        if (records.Count == 0) return 0;
        var serverId = GetOrAddServer(serverName);
        var file = FindLatestByIndex(serverId, EventLogFileIndex);
        if (file is null)
        {
            file = new LogFileRecord { ServerId = serverId, Index = EventLogFileIndex, FirstLineHash = 0, FileName = "DFS Replication event log", IsComplete = true };
            AddFile(file);
        }

        var ids = records.Select(r => EventId(file.FileId, (int)(uint)r.RecordId)).ToList();
        Execute($"DELETE FROM event WHERE file_id = ? AND event_id IN ({string.Join(",", ids)})", file.FileId);

        using (var writer = OpenSmallWriter())
        {
            foreach (var r in records)
            {
                var lines = r.Message.Replace("\r\n", "\n").Split('\n');
                var raw = new RawEvent
                {
                    LineNo = (int)(uint)r.RecordId,
                    LocalTime = r.TimeUtc,
                    Module = EventLogModule,
                    ClassMethod = "EventLog::" + (r.Provider ?? "DFSR"),
                    Severity = r.Level switch { 1 or 2 => Severity.Error, 3 => Severity.Warn, _ => Severity.Info },
                    Message = $"Event {r.EventId}: {lines[0].Trim()}",
                };
                raw.Nested.AddRange(lines.Skip(1).Select(l => l.TrimEnd()).Where(l => l.Length > 0));
                var fields = new EventFields { EventLogId = r.EventId };
                writer.Write(EventId(file.FileId, raw.LineNo), file.FileId, serverId, raw, r.TimeUtc, fields);
            }
        }
        ApplyRules(file.FileId);

        file.EventCount = CountEvents(file.FileId);
        file.FirstTsUtc = Scalar<DateTime?>("SELECT min(ts_utc) FROM event WHERE file_id = ?", file.FileId);
        file.LastTsUtc = Scalar<DateTime?>("SELECT max(ts_utc) FROM event WHERE file_id = ?", file.FileId);
        file.ImportedAt = DateTime.UtcNow;
        file.TzBasis = "UTC (event log)";
        UpdateFile(file);
        return records.Count;
    }

    // ---------- orphans ----------

    public void AddPendingOrphan(int serverId, int index, IEnumerable<string> lines) =>
        Execute("INSERT INTO pending_orphan VALUES (?, ?, ?)", serverId, index, string.Join('\n', lines));

    /// <summary>Removes and returns orphan lines waiting for the file before <paramref name="index"/>.</summary>
    public List<string>? TakePendingOrphan(int serverId, int index)
    {
        var text = Scalar<string?>("SELECT nested_text FROM pending_orphan WHERE server_id = ? AND idx = ? LIMIT 1", serverId, index);
        if (text is null) return null;
        Execute("DELETE FROM pending_orphan WHERE server_id = ? AND idx = ?", serverId, index);
        return [.. text.Split('\n')];
    }

    // ---------- generic helpers ----------

    /// <summary>Runs an ad-hoc query; the first row yielded is the column names.</summary>
    public IEnumerable<object?[]> Query(string sql, params object?[] args)
    {
        using var cmd = Command(sql, args);
        using var r = cmd.ExecuteReader();
        var names = new object?[r.FieldCount];
        for (var i = 0; i < r.FieldCount; i++) names[i] = r.GetName(i);
        yield return names;
        while (r.Read())
        {
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            yield return row;
        }
    }

    internal DbCommand Command(string sql, params object?[] args)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var a in args) cmd.Parameters.Add(new DuckDBParameter(a ?? DBNull.Value));
        return cmd;
    }

    internal int Execute(string sql, params object?[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    internal T Scalar<T>(string sql, params object?[] args)
    {
        using var cmd = Command(sql, args);
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static T? Get<T>(DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return default;
        var v = r.GetValue(i);
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return v.GetType() == target ? (T)v : (T)Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed record StoredEvent(long EventId, int FileId, int ServerId, DateTime TsUtc, RawEvent Raw);
