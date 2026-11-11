namespace DfsrAnalyser.Core.Storage;

internal static class Schema
{
    public const int Version = 2;

    /// <summary>
    /// The event table's columns, in storage order. The DDL and the CSV load's column spec
    /// are both generated from this list, and <see cref="CsvBulkWriter"/> writes in this order.
    /// </summary>
    public static readonly (string Name, string Type)[] EventColumns =
    [
        ("event_id", "BIGINT"),
        ("file_id", "INTEGER"),
        ("server_id", "INTEGER"),
        ("line_no", "INTEGER"),
        ("ts_utc", "TIMESTAMP"),
        ("ts_local", "TIMESTAMP"),
        ("thread", "INTEGER"),
        ("module", "VARCHAR"),
        ("src_line", "INTEGER"),
        ("severity", "UTINYINT"),
        ("class_method", "VARCHAR"),
        ("message", "VARCHAR"),
        ("nested_count", "INTEGER"),
        ("category", "VARCHAR"),
        ("conn_id", "UUID"),
        ("cs_id", "UUID"),
        ("cs_name", "VARCHAR"),
        ("replica_set_id", "UUID"),
        ("rg_name", "VARCHAR"),
        ("uid_db", "UUID"),
        ("uid_ver", "BIGINT"),
        ("gvsn_db", "UUID"),
        ("gvsn_ver", "BIGINT"),
        ("parent_db", "UUID"),
        ("parent_ver", "BIGINT"),
        ("fid", "BIGINT"),
        ("file_name", "VARCHAR"),
        ("extension", "VARCHAR"),
        ("attributes", "INTEGER"),
        ("is_folder", "BOOLEAN"),
        ("present", "BOOLEAN"),
        ("meet_install", "BOOLEAN"),
        ("usn_reason", "VARCHAR"),
        ("error_code", "INTEGER"),
        ("error_code_inner", "INTEGER"),
        ("event_log_id", "INTEGER"),
        ("transfer_type", "VARCHAR"),
        ("staged_size", "BIGINT"),
        ("retries", "INTEGER"),
        ("update_type", "VARCHAR"),
    ];

    public static readonly (string Name, string Type)[] NestedColumns =
    [
        ("event_id", "BIGINT"),
        ("nested_text", "VARCHAR"),
    ];

    /// <summary>DuckDB <c>read_csv(columns = {...})</c> argument for a column list.</summary>
    public static string CsvColumnSpec((string Name, string Type)[] columns) =>
        "{" + string.Join(", ", columns.Select(c => $"'{c.Name}': '{c.Type}'")) + "}";

    private static string EventTableDdl =>
        "CREATE TABLE IF NOT EXISTS event (\n    " +
        string.Join(",\n    ", EventColumns.Select(c => $"{c.Name} {c.Type}")) +
        "\n);";

    /// <summary>
    /// No primary keys or indexes on the event tables: they would cost memory during bulk
    /// appends and DuckDB's zone maps already serve range and equality scans well.
    /// </summary>
    public static string Ddl => """
        CREATE TABLE IF NOT EXISTS meta (
            key   VARCHAR PRIMARY KEY,
            value VARCHAR
        );

        CREATE TABLE IF NOT EXISTS server (
            server_id INTEGER PRIMARY KEY,
            name      VARCHAR NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS server_alias (
            server_id INTEGER NOT NULL,
            alias     VARCHAR NOT NULL,
            PRIMARY KEY (server_id, alias)
        );

        CREATE TABLE IF NOT EXISTS log_file (
            file_id         INTEGER PRIMARY KEY,
            server_id       INTEGER NOT NULL,
            file_name       VARCHAR NOT NULL,
            source_path     VARCHAR,
            size_bytes      BIGINT,
            header_computer VARCHAR,
            tz_id           VARCHAR,
            tz_name         VARCHAR,
            tz_basis        VARCHAR,
            log_level       INTEGER,
            max_entry_count INTEGER,
            max_file_count  INTEGER,
            sequence        INTEGER,
            idx             INTEGER NOT NULL,
            first_line_hash BIGINT NOT NULL,
            first_ts_utc    TIMESTAMP,
            last_ts_utc     TIMESTAMP,
            line_count      INTEGER,
            event_count     INTEGER,
            orphan_lines    INTEGER,
            unrecognised    INTEGER,
            last_event_line INTEGER,
            last_event_id   BIGINT,
            nested_dropped  BOOLEAN NOT NULL DEFAULT FALSE,
            is_complete     BOOLEAN NOT NULL DEFAULT FALSE,
            imported_at     TIMESTAMP
        );

        """ + EventTableDdl + """


        CREATE TABLE IF NOT EXISTS event_nested (
            event_id    BIGINT NOT NULL,
            nested_text VARCHAR NOT NULL
        );

        -- '+' lines at the start of a rolled file whose owning event (last event of Index-1)
        -- is not in the case yet.
        CREATE TABLE IF NOT EXISTS pending_orphan (
            server_id   INTEGER NOT NULL,
            idx         INTEGER NOT NULL,
            nested_text VARCHAR NOT NULL
        );

        -- The categories of the rule set last applied (display order, group, visibility, notes).
        CREATE TABLE IF NOT EXISTS category (
            name   VARCHAR PRIMARY KEY,
            grp    VARCHAR NOT NULL,
            hidden BOOLEAN NOT NULL,
            note   VARCHAR,
            ord    INTEGER NOT NULL
        );
        """;
}
