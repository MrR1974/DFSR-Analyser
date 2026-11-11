using System.Data.Common;
using DfsrAnalyser.Core.Parsing;
using DuckDB.NET.Data;

namespace DfsrAnalyser.Core.Storage;

/// <summary>
/// A case: a folder holding <c>case.duckdb</c>. Owns the connection and all SQL.
/// Not thread-safe; one importer or one UI session at a time.
/// </summary>
public sealed class CaseStore : IDisposable
{
    public const string DatabaseFileName = "case.duckdb";

    public string Folder { get; }
    public string DatabasePath { get; }
    internal DuckDBConnection Connection { get; }

    private CaseStore(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
        DatabasePath = Path.Combine(Folder, DatabaseFileName);
        Connection = new DuckDBConnection($"Data Source={DatabasePath}");
        Connection.Open();
        Execute(Schema.Ddl);
        Execute("INSERT INTO meta VALUES ('schema_version', ?) ON CONFLICT (key) DO NOTHING", Schema.Version.ToString());
    }

    public static CaseStore Open(string folder) => new(folder);

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
            is_complete = ?, imported_at = ?
        WHERE file_id = ?
        """,
        f.FileName, f.SourcePath, f.SizeBytes, f.HeaderComputer, f.TzId, f.TzName, f.TzBasis,
        f.LogLevel, f.MaxEntryCount, f.MaxFileCount, f.Sequence, f.FirstTsUtc, f.LastTsUtc,
        f.LineCount, f.EventCount, f.OrphanLines, f.Unrecognised, f.LastEventLine, f.LastEventId,
        f.IsComplete, f.ImportedAt, f.FileId);

    private List<LogFileRecord> QueryFiles(string tail, params object?[] args)
    {
        var list = new List<LogFileRecord>();
        using var cmd = Command($"""
            SELECT file_id, server_id, file_name, source_path, size_bytes, header_computer, tz_id, tz_name, tz_basis,
                   log_level, max_entry_count, max_file_count, sequence, idx, first_line_hash, first_ts_utc, last_ts_utc,
                   line_count, event_count, orphan_lines, unrecognised, last_event_line, last_event_id, is_complete, imported_at
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

    /// <summary>Loads CSV files produced by <see cref="CsvBulkWriter"/> with DuckDB's native COPY.</summary>
    public void LoadCsv(CsvBulkWriter csv)
    {
        CopyFrom("event", csv.EventPath);
        CopyFrom("event_nested", csv.NestedPath);
    }

    private void CopyFrom(string table, string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
        Execute($"COPY {table} FROM '{path.Replace("'", "''")}' " +
                "(FORMAT csv, HEADER false, DELIMITER ',', QUOTE '\"', ESCAPE '\"', NULLSTR '', ALLOW_QUOTED_NULLS false, AUTO_DETECT false)");
    }

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
