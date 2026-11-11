namespace DfsrAnalyser.Core.Storage;

public sealed record ServerSummary(string Server, long Files, long Events, long NestedLines, DateTime? FirstUtc, DateTime? LastUtc);

public sealed record CountRow(string Server, string Key, long Count);

/// <summary>Read-only summary queries over a case (used by the CLI harness; the UI will grow its own).</summary>
public sealed class CaseStats(CaseStore store)
{
    /// <summary>Signature checks taken from PLAN.md §1a, so CLI output can be compared with grep counts.</summary>
    public static readonly (string Label, string Where)[] Signatures =
    [
        ("Local create (CreateNewRecord insert)", "class_method = 'UsnConsumer::CreateNewRecord' AND message LIKE 'LDB Inserting%'"),
        ("Local modify/rename (UpdateIdRecord)", "class_method = 'UsnConsumer::UpdateIdRecord' AND message LIKE 'LDB Updating%'"),
        ("Local delete (TombstoneOrDelete)", "class_method = 'UsnConsumer::TombstoneOrDelete' AND message LIKE 'LDB Updating%'"),
        ("Update offered (Join::SubmitUpdate Sent)", "class_method = 'Join::SubmitUpdate' AND message LIKE 'Sent:%'"),
        ("Update received (ReceiveUpdates)", "class_method = 'InConnection::ReceiveUpdates' AND message LIKE 'Received:%'"),
        ("RAWGET received", "class_method = 'InConnection::LogTransferActivity' AND transfer_type = 'RAWGET' AND message LIKE 'Received%'"),
        ("RDCGET received", "class_method = 'InConnection::LogTransferActivity' AND transfer_type = 'RDCGET' AND message LIKE 'Received%'"),
        ("Install-rename completed", "class_method = 'Meet::InstallRename' AND message LIKE '-> DONE Install-rename completed%'"),
        ("Install-move completed", "class_method = 'Meet::InstallMove' AND message LIKE '-> DONE Install-move completed%'"),
        ("Tombstone installed", "class_method = 'Meet::InstallTombstone' AND message LIKE '-> DONE Install Tombstone complete%'"),
        ("Conflict check (LocalDominates Conflicting)", "class_method = 'Meet::LocalDominates' AND message LIKE 'Conflicting%'"),
        ("Event 4412 logged", "class_method = 'EventLog::Report' AND message LIKE 'Logging eventId:4412 %'"),
        ("Event 4202 logged", "class_method = 'EventLog::Report' AND message LIKE 'Logging eventId:4202 %'"),
        ("Event 4204 logged", "class_method = 'EventLog::Report' AND message LIKE 'Logging eventId:4204 %'"),
        ("[ERROR]", "severity = 2"),
        ("[WARN]", "severity = 1"),
    ];

    public static readonly (string Label, string Where)[] FieldCoverage =
    [
        ("uid", "uid_db IS NOT NULL"),
        ("gvsn", "gvsn_db IS NOT NULL"),
        ("connId", "conn_id IS NOT NULL"),
        ("csId", "cs_id IS NOT NULL"),
        ("csName", "cs_name IS NOT NULL"),
        ("rgName", "rg_name IS NOT NULL"),
        ("file name", "file_name IS NOT NULL"),
        ("extension", "extension IS NOT NULL"),
        ("attributes", "attributes IS NOT NULL"),
        ("USN reason", "usn_reason IS NOT NULL"),
        ("error code", "error_code IS NOT NULL"),
        ("event log id", "event_log_id IS NOT NULL"),
        ("staged size", "staged_size IS NOT NULL"),
    ];

    public List<ServerSummary> Servers()
    {
        var list = new List<ServerSummary>();
        using var cmd = store.Command("""
            SELECT s.name,
                   (SELECT count(*) FROM log_file f WHERE f.server_id = s.server_id),
                   count(e.event_id), CAST(coalesce(sum(e.nested_count), 0) AS BIGINT), min(e.ts_utc), max(e.ts_utc)
            FROM server s LEFT JOIN event e ON e.server_id = s.server_id
            GROUP BY s.server_id, s.name ORDER BY s.name
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ServerSummary(r.GetString(0), Convert.ToInt64(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)),
                Convert.ToInt64(r.GetValue(3)), CaseStore.Get<DateTime?>(r, 4), CaseStore.Get<DateTime?>(r, 5)));
        return list;
    }

    /// <summary>Counts per server for each (label, where) pair.</summary>
    public List<CountRow> CountBy((string Label, string Where)[] checks)
    {
        var rows = new List<CountRow>();
        foreach (var (label, where) in checks)
        {
            using var cmd = store.Command($"""
                SELECT s.name, count(e.event_id)
                FROM server s LEFT JOIN event e ON e.server_id = s.server_id AND ({where})
                GROUP BY s.name ORDER BY s.name
                """);
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new CountRow(r.GetString(0), label, Convert.ToInt64(r.GetValue(1))));
        }
        return rows;
    }

    public List<CountRow> TopClassMethods(int top)
    {
        var rows = new List<CountRow>();
        using var cmd = store.Command("""
            SELECT 'all', module || ' ' || class_method, count(*) AS n
            FROM event GROUP BY 2 ORDER BY n DESC LIMIT ?
            """, top);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new CountRow(r.GetString(0), r.GetString(1), Convert.ToInt64(r.GetValue(2))));
        return rows;
    }

    public long PendingOrphans() => store.Scalar<long>("SELECT count(*) FROM pending_orphan");
}
