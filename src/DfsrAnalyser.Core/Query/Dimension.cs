namespace DfsrAnalyser.Core.Query;

/// <summary>
/// A property events can be filtered, faceted, stacked or grouped by. <see cref="Sql"/> is a VARCHAR
/// expression over <c>e</c> (event), <c>c</c> (category) and <c>s</c> (server), plus any name tables
/// listed in <see cref="Joins"/> (see <see cref="CaseQuery"/> for their aliases). Missing values are
/// reported as <see cref="None"/>; GUIDs no log names show as "unknown {XXXXXXXX…}".
/// </summary>
public sealed record Dimension(string Key, string Label, string Sql, bool Searchable = false, params string[] Joins)
{
    public const string None = "(none)";

    public override string ToString() => Label;
}

public static class Dimensions
{
    private static string OrNone(string expr) => $"coalesce(CAST({expr} AS VARCHAR), '{Dimension.None}')";

    /// <summary>"unknown {2314971A…}" for a GUID column, or (none) when null.</summary>
    private static string Unknown(string guidExpr) =>
        $"CASE WHEN {guidExpr} IS NULL THEN '{Dimension.None}' ELSE 'unknown {{' || upper(left(CAST({guidExpr} AS VARCHAR), 8)) || '…}}' END";

    public static readonly Dimension Group = new("group", "Group", "coalesce(c.grp, 'Other')");
    public static readonly Dimension Category = new("category", "Category", "e.category");
    public static readonly Dimension Server = new("server", "Server", "s.name");
    public static readonly Dimension Severity = new("severity", "Severity",
        "CASE e.severity WHEN 2 THEN 'Error' WHEN 1 THEN 'Warning' ELSE 'Info' END");

    public static readonly Dimension OriginServer = new("origin", "Origin server",
        $"coalesce(od.server, {Unknown("e.gvsn_db")})", false, "od");
    public static readonly Dimension CreatedOn = new("created", "Created on server",
        $"coalesce(ud.server, {Unknown("e.uid_db")})", false, "ud");
    public static readonly Dimension Partner = new("partner", "Partner",
        $"coalesce(nc.partner, {Unknown("e.conn_id")})", false, "nc");
    public static readonly Dimension Connection = new("conn", "Connection",
        $"CASE WHEN nc.partner IS NULL THEN {Unknown("e.conn_id")} WHEN nc.inbound THEN 'from ' || nc.partner ELSE 'to ' || nc.partner END",
        false, "nc");
    public static readonly Dimension Direction = new("direction", "Direction",
        $"CASE WHEN nc.inbound THEN 'Inbound' WHEN NOT nc.inbound THEN 'Outbound' ELSE '{Dimension.None}' END", false, "nc");

    public static readonly Dimension ReplicatedFolder = new("rf", "Replicated folder",
        $"coalesce(e.cs_name, nrf.name, {Unknown("e.cs_id")})", false, "nrf");
    public static readonly Dimension ReplicationGroup = new("rg", "Replication group",
        $"coalesce(e.rg_name, nrg.name, {Unknown("coalesce(e.replica_set_id, nrf.rg_guid)")})", false, "nrf", "nrg");

    public static readonly Dimension FileName = new("file", "File name", OrNone("e.file_name"), Searchable: true);
    public static readonly Dimension Extension = new("ext", "Extension", OrNone("e.extension"));
    public static readonly Dimension Kind = new("kind", "File or folder",
        $"CASE WHEN e.is_folder THEN 'Folder' WHEN NOT e.is_folder THEN 'File' ELSE '{Dimension.None}' END");
    public static readonly Dimension ErrorCode = new("error", "Error code", OrNone("e.error_code_inner"));
    public static readonly Dimension EventLogId = new("eventid", "Event ID", OrNone("e.event_log_id"));
    public static readonly Dimension TransferType = new("transfer", "Transfer type", OrNone("e.transfer_type"));
    public static readonly Dimension OriginDb = new("origindb", "Origin DB GUID", OrNone("e.gvsn_db"));
    public static readonly Dimension Source = new("source", "Source",
        "CASE WHEN e.module = 'EVTLOG' THEN 'Event log' ELSE 'Debug log' END");
    public static readonly Dimension Module = new("module", "Module", "e.module");
    public static readonly Dimension ClassMethod = new("method", "Class::Method", "e.class_method", Searchable: true);

    /// <summary>Every dimension, in the order offered to the user.</summary>
    public static readonly IReadOnlyList<Dimension> All =
    [
        Group, Category, Server, Severity, OriginServer, CreatedOn, Partner, Connection, Direction,
        ReplicatedFolder, ReplicationGroup, FileName, Extension, Kind, ErrorCode, EventLogId, TransferType,
        OriginDb, Source, Module, ClassMethod,
    ];

    public static Dimension Get(string key) =>
        All.FirstOrDefault(d => d.Key == key) ?? throw new KeyNotFoundException($"Unknown dimension '{key}'.");
}
