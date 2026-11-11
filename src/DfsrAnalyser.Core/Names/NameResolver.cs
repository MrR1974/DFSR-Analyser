using System.Globalization;
using System.Text.RegularExpressions;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Core.Names;

public sealed record NameSummary(int ReplicationGroups, int ReplicatedFolders, int Members, int Connections, int DatabaseGuids, int AmbiguousDatabaseGuids);

/// <summary>
/// Rebuilds the name tables from what the logs say about themselves:
/// <list type="bullet">
/// <item>Configuration dumps (<c>CPAR</c> lines: <c>[[ DfsrReplicationGroup ]]</c>, <c>[[ DfsrMember ]]</c>,
/// <c>[[ DfsrConnection ]]</c>, <c>[[ DfsrReplicatedFolder ]]</c>) give RG, RF, member and connection names, the
/// partner and direction of each connection as seen by the logging server, and each RF's configuration.</item>
/// <item>Inline <c>csId</c>/<c>csName</c> and <c>replicaSetId</c>/<c>rgName</c> pairs fill gaps.</item>
/// <item>DB GUID → server is inferred: a local change written to the DFSR database (create, update, tombstone)
/// carries the writing server's DB GUID in its GVSN.</item>
/// </list>
/// Later dumps override earlier ones. The whole set is recomputed each time; it is small.
/// </summary>
public sealed partial class NameResolver(CaseStore store)
{
    [GeneratedRegex(@"^\[\[\s*(?<block>\w+)\s*\]\]")]
    private static partial Regex BlockRx();

    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z0-9.]*):\s?(?<value>.*?)(\s*\[Flags:0x[0-9A-Fa-f]+\])?\s*$")]
    private static partial Regex ParamRx();

    private sealed record Conn(Guid ConnId, int ServerId, string Partner, Guid? PartnerGuid, bool? Inbound, bool? Enabled, bool? RdcEnabled, Guid? RgGuid);

    private sealed record RfConfig(int ServerId, Guid RfGuid, string? RootPath, int? StagingMb, int? ConflictMb, bool? IsPrimary, bool? ReadOnly, bool? Enabled, string? FileFilter);

    public NameSummary Resolve()
    {
        var rgs = new Dictionary<Guid, string>();
        var rfs = new Dictionary<Guid, (string Name, Guid? Rg)>();
        var members = new Dictionary<Guid, string>();
        var conns = new Dictionary<(Guid, int), Conn>();
        var rfConfigs = new Dictionary<(int, Guid), RfConfig>();

        ReadDumps(rgs, rfs, members, conns, rfConfigs);

        store.Execute("BEGIN TRANSACTION");
        try
        {
            foreach (var t in new[] { "name_rg", "name_rf", "name_member", "name_conn", "name_db", "rf_config" })
                store.Execute($"DELETE FROM {t}");

            foreach (var (g, n) in rgs) store.Execute("INSERT INTO name_rg VALUES (?, ?, 'config dump')", g, n);
            foreach (var (g, v) in rfs) store.Execute("INSERT INTO name_rf VALUES (?, ?, ?, 'config dump')", g, v.Name, v.Rg);
            foreach (var (g, n) in members) store.Execute("INSERT INTO name_member VALUES (?, ?)", g, n);
            foreach (var c in conns.Values)
                store.Execute("INSERT INTO name_conn VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                    c.ConnId, c.ServerId, c.Partner, c.PartnerGuid, c.Inbound, c.Enabled, c.RdcEnabled, c.RgGuid);
            foreach (var r in rfConfigs.Values)
                store.Execute("INSERT INTO rf_config VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                    r.ServerId, r.RfGuid, r.RootPath, r.StagingMb, r.ConflictMb, r.IsPrimary, r.ReadOnly, r.Enabled, r.FileFilter);

            // Inline names fill gaps the dumps did not cover (most common value wins).
            store.Execute("""
                INSERT INTO name_rf
                SELECT cs_id, mode(cs_name), NULL, 'inline csName' FROM event
                WHERE cs_id IS NOT NULL AND cs_name IS NOT NULL AND cs_id NOT IN (SELECT guid FROM name_rf)
                GROUP BY cs_id
                """);
            store.Execute("""
                INSERT INTO name_rg
                SELECT replica_set_id, mode(rg_name), 'inline rgName' FROM event
                WHERE replica_set_id IS NOT NULL AND rg_name IS NOT NULL AND replica_set_id NOT IN (SELECT guid FROM name_rg)
                GROUP BY replica_set_id
                """);

            // DB GUID -> server, from local changes written to the database. UpdateUsnOnly is excluded:
            // it keeps the existing (possibly remote) GVSN.
            store.Execute("""
                INSERT INTO name_db
                WITH ev AS (
                    SELECT gvsn_db AS guid, server_id, count(*) AS n FROM event
                    WHERE class_method IN ('UsnConsumer::CreateNewRecord', 'UsnConsumer::UpdateIdRecord', 'UsnConsumer::TombstoneOrDelete')
                      AND starts_with(message, 'LDB ') AND gvsn_db IS NOT NULL
                    GROUP BY gvsn_db, server_id
                ), ranked AS (
                    SELECT *, row_number() OVER (PARTITION BY guid ORDER BY n DESC, server_id) AS rn,
                              sum(n) OVER (PARTITION BY guid) AS total
                    FROM ev
                )
                SELECT r.guid, r.server_id, s.name, r.n, r.total > r.n
                FROM ranked r JOIN server s ON s.server_id = r.server_id
                WHERE r.rn = 1
                """);
            store.Execute("COMMIT");
        }
        catch
        {
            store.Execute("ROLLBACK");
            throw;
        }

        return new NameSummary(
            (int)store.Scalar<long>("SELECT count(*) FROM name_rg"),
            (int)store.Scalar<long>("SELECT count(*) FROM name_rf"),
            (int)store.Scalar<long>("SELECT count(*) FROM name_member"),
            (int)store.Scalar<long>("SELECT count(*) FROM name_conn"),
            (int)store.Scalar<long>("SELECT count(*) FROM name_db"),
            (int)store.Scalar<long>("SELECT count(*) FROM name_db WHERE ambiguous"));
    }

    private void ReadDumps(
        Dictionary<Guid, string> rgs, Dictionary<Guid, (string Name, Guid? Rg)> rfs, Dictionary<Guid, string> members,
        Dictionary<(Guid, int), Conn> conns, Dictionary<(int, Guid), RfConfig> rfConfigs)
    {
        // A dump is printed by one thread; walk each (file, thread) stream in line order.
        using var cmd = store.Command("""
            SELECT server_id, file_id, thread, message FROM event
            WHERE module = 'CPAR'
            ORDER BY server_id, file_id, thread, line_no
            """);
        using var r = cmd.ExecuteReader();

        (int Server, int File, int Thread) stream = (-1, -1, -1);
        string? block = null;
        Guid? currentRg = null;
        var p = new Dictionary<string, string>(StringComparer.Ordinal);

        while (r.Read())
        {
            var key = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
            if (key != stream)
            {
                Flush();
                stream = key;
                block = null;
                currentRg = null;
            }
            var msg = r.IsDBNull(3) ? "" : r.GetString(3).Trim();

            var b = BlockRx().Match(msg);
            if (b.Success)
            {
                Flush();
                block = b.Groups["block"].Value;
                continue;
            }
            var m = ParamRx().Match(msg);
            if (m.Success && block is not null)
                p[m.Groups["key"].Value] = m.Groups["value"].Value.Trim();
        }
        Flush();

        void Flush()
        {
            if (block is null || p.Count == 0) { p.Clear(); return; }
            var server = stream.Server;
            switch (block)
            {
                case "DfsrReplicationGroup":
                    if (G("ReplicationGroupGuid") is { } rg)
                    {
                        currentRg = rg;
                        if (S("ReplicationGroupName") is { } n) rgs[rg] = n;
                    }
                    break;
                case "DfsrMember":
                    if (G("MemberGuid") is { } mg && S("MemberName") is { } mn) members[mg] = mn;
                    break;
                case "DfsrConnection":
                    if (G("ConnectionGuid") is { } cg && S("PartnerName") is { } partner)
                    {
                        conns[(cg, server)] = new Conn(cg, server, partner, G("PartnerGuid"), B("Inbound"), B("Enabled"), B("RdcEnabled"), currentRg);
                        if (G("PartnerGuid") is { } pg) members[pg] = partner;
                    }
                    break;
                case "DfsrReplicatedFolder":
                    if (G("ReplicatedFolderGuid") is { } rf)
                    {
                        var rgGuid = G("ReplicationGroupGuid") ?? currentRg;
                        if (S("ReplicatedFolderName") is { } n) rfs[rf] = (n, rgGuid);
                        rfConfigs[(server, rf)] = new RfConfig(server, rf, S("RootPath"), I("StagingSizeInMb"), I("ConflictSizeInMb"),
                            B("IsPrimary"), B("ReadOnly"), B("Enabled"), S("FileFilter"));
                    }
                    break;
            }
            p.Clear();
        }

        string? S(string k) => p.TryGetValue(k, out var v) && v.Length > 0 && v != "<null>" ? v : null;
        Guid? G(string k) => S(k) is { } v && Guid.TryParse(v, out var g) ? g : null;
        bool? B(string k) => S(k) switch { "TRUE" => true, "FALSE" => false, _ => null };
        int? I(string k) => S(k) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }
}
