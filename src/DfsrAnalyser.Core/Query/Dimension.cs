namespace DfsrAnalyser.Core.Query;

/// <summary>
/// A property events can be filtered, faceted, stacked or grouped by. <see cref="Sql"/> is a VARCHAR
/// expression over the aliases <c>e</c> (event), <c>c</c> (category, left-joined) and <c>s</c> (server).
/// Missing values are reported as <see cref="None"/>.
/// </summary>
public sealed record Dimension(string Key, string Label, string Sql, bool Searchable = false)
{
    public const string None = "(none)";

    public override string ToString() => Label;
}

public static class Dimensions
{
    private static string OrNone(string expr) => $"coalesce(CAST({expr} AS VARCHAR), '{Dimension.None}')";

    public static readonly Dimension Group = new("group", "Group", "coalesce(c.grp, 'Other')");
    public static readonly Dimension Category = new("category", "Category", "e.category");
    public static readonly Dimension Server = new("server", "Server", "s.name");
    public static readonly Dimension Severity = new("severity", "Severity",
        "CASE e.severity WHEN 2 THEN 'Error' WHEN 1 THEN 'Warning' ELSE 'Info' END");
    public static readonly Dimension ReplicatedFolder = new("rf", "Replicated folder", OrNone("e.cs_name"));
    public static readonly Dimension ReplicationGroup = new("rg", "Replication group", OrNone("e.rg_name"));
    public static readonly Dimension FileName = new("file", "File name", OrNone("e.file_name"), Searchable: true);
    public static readonly Dimension Extension = new("ext", "Extension", OrNone("e.extension"));
    public static readonly Dimension Kind = new("kind", "File or folder",
        $"CASE WHEN e.is_folder THEN 'Folder' WHEN NOT e.is_folder THEN 'File' ELSE '{Dimension.None}' END");
    public static readonly Dimension ErrorCode = new("error", "Error code", OrNone("e.error_code_inner"));
    public static readonly Dimension EventLogId = new("eventid", "Event ID", OrNone("e.event_log_id"));
    public static readonly Dimension TransferType = new("transfer", "Transfer type", OrNone("e.transfer_type"));
    public static readonly Dimension Origin = new("origin", "Origin (GVSN DB GUID)", OrNone("e.gvsn_db"));
    public static readonly Dimension Connection = new("conn", "Connection", OrNone("e.conn_id"));
    public static readonly Dimension Module = new("module", "Module", "e.module");
    public static readonly Dimension ClassMethod = new("method", "Class::Method", "e.class_method", Searchable: true);

    /// <summary>Every dimension, in the order offered to the user.</summary>
    public static readonly IReadOnlyList<Dimension> All =
    [
        Group, Category, Server, Severity, ReplicatedFolder, ReplicationGroup, FileName, Extension, Kind,
        ErrorCode, EventLogId, TransferType, Origin, Connection, Module, ClassMethod,
    ];

    public static Dimension Get(string key) =>
        All.FirstOrDefault(d => d.Key == key) ?? throw new KeyNotFoundException($"Unknown dimension '{key}'.");
}
