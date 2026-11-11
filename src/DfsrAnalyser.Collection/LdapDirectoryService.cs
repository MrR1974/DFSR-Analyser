using System.DirectoryServices.Protocols;
using System.Net;
using DfsrAnalyser.Core.Collection;

namespace DfsrAnalyser.Collection;

/// <summary>
/// Discovers DFSR replication groups and members over LDAP:
/// <c>CN=DFSR-GlobalSettings,CN=System,&lt;domain&gt;</c> holds one <c>msDFSR-ReplicationGroup</c> per RG
/// (SYSVOL is "Domain System Volume"); each RG's <c>CN=Topology</c> holds <c>msDFSR-Member</c> objects whose
/// <c>msDFSR-ComputerReference</c> points at the computer account (dNSHostName, and the DC flag in
/// userAccountControl).
/// </summary>
public sealed class LdapDirectoryService(CollectionLog? log = null) : IDirectoryService
{
    private const int ServerTrustAccount = 0x2000; // userAccountControl: domain controller

    public DiscoveryResult Discover(string? domain, NetworkCredential? credential, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        log?.Info($"LDAP: connecting to {target ?? "the current domain"} as {(credential is null ? "the current user" : credential.UserName)}.");
        using var conn = credential is null
            ? new LdapConnection(new LdapDirectoryIdentifier(target, 389))
            : new LdapConnection(new LdapDirectoryIdentifier(target, 389), credential);
        conn.AuthType = AuthType.Negotiate;
        conn.SessionOptions.ProtocolVersion = 3;
        conn.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        conn.SessionOptions.Signing = true;
        conn.SessionOptions.Sealing = true;
        conn.Timeout = TimeSpan.FromSeconds(30);
        conn.Bind();

        var root = Single(conn, "", "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "dnsHostName")
                   ?? throw new InvalidOperationException("RootDSE not readable.");
        var nc = Str(root, "defaultNamingContext") ?? throw new InvalidOperationException("No defaultNamingContext in RootDSE.");
        var dc = Str(root, "dnsHostName");
        log?.Info($"LDAP: bound to {dc ?? "?"}; naming context {nc}.");

        var groups = new List<DiscoveredGroup>();
        var settings = $"CN=DFSR-GlobalSettings,CN=System,{nc}";
        foreach (var rg in Search(conn, settings, "(objectClass=msDFSR-ReplicationGroup)", SearchScope.OneLevel, ct, "cn", "objectGUID"))
        {
            var name = Str(rg, "cn") ?? rg.DistinguishedName;
            var guid = GuidOf(rg) ?? Guid.Empty;
            var members = new List<DiscoveredMember>();
            foreach (var m in Search(conn, $"CN=Topology,{rg.DistinguishedName}", "(objectClass=msDFSR-Member)", SearchScope.OneLevel, ct,
                         "cn", "objectGUID", "msDFSR-ComputerReference"))
            {
                var computerDn = Str(m, "msDFSR-ComputerReference");
                string? dns = null, cn = null;
                var isDc = false;
                if (computerDn is not null)
                {
                    try
                    {
                        var computer = Single(conn, computerDn, "(objectClass=computer)", SearchScope.Base, "dNSHostName", "cn", "userAccountControl");
                        dns = computer is null ? null : Str(computer, "dNSHostName");
                        cn = computer is null ? null : Str(computer, "cn");
                        isDc = computer is not null && int.TryParse(Str(computer, "userAccountControl"), out var uac) && (uac & ServerTrustAccount) != 0;
                    }
                    catch (DirectoryOperationException ex)
                    {
                        log?.Warn($"LDAP: could not read computer {computerDn}: {ex.Message}");
                    }
                }
                cn ??= computerDn is null ? Str(m, "cn") : FirstRdn(computerDn);
                members.Add(new DiscoveredMember((cn ?? "?").ToUpperInvariant(), dns, GuidOf(m) ?? Guid.Empty, isDc, computerDn ?? ""));
            }
            var isSysvol = string.Equals(name, "Domain System Volume", StringComparison.OrdinalIgnoreCase);
            groups.Add(new DiscoveredGroup(name, guid, isSysvol, members.OrderBy(m => m.Name).ToList()));
            log?.Info($"LDAP: RG '{name}' {{{guid}}}: {members.Count} member(s): {string.Join(", ", members.Select(m => m.Name + (m.IsDomainController ? " (DC)" : "")))}.");
        }

        return new DiscoveryResult(target ?? DomainFromNc(nc), nc, dc, groups.OrderBy(g => g.IsSysvol).ThenBy(g => g.Name).ToList());
    }

    private static SearchResultEntry? Single(LdapConnection conn, string baseDn, string filter, SearchScope scope, params string[] attrs)
    {
        var resp = (SearchResponse)conn.SendRequest(new SearchRequest(baseDn, filter, scope, attrs));
        return resp.Entries.Count > 0 ? resp.Entries[0] : null;
    }

    /// <summary>Paged search (AD returns at most 1000 entries per page).</summary>
    private static IEnumerable<SearchResultEntry> Search(LdapConnection conn, string baseDn, string filter, SearchScope scope, CancellationToken ct, params string[] attrs)
    {
        var page = new PageResultRequestControl(500);
        var req = new SearchRequest(baseDn, filter, scope, attrs);
        req.Controls.Add(page);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            SearchResponse resp;
            try { resp = (SearchResponse)conn.SendRequest(req); }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject) { yield break; }
            foreach (SearchResultEntry e in resp.Entries) yield return e;
            var cookie = resp.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0) yield break;
            page.Cookie = cookie;
        }
    }

    private static string? Str(SearchResultEntry e, string attr) =>
        e.Attributes.Contains(attr) && e.Attributes[attr].Count > 0 ? e.Attributes[attr][0]?.ToString() : null;

    private static Guid? GuidOf(SearchResultEntry e) =>
        e.Attributes.Contains("objectGUID") && e.Attributes["objectGUID"].GetValues(typeof(byte[])) is { Length: > 0 } v && v[0] is byte[] { Length: 16 } b
            ? new Guid(b) : null;

    private static string FirstRdn(string dn)
    {
        var first = dn.Split(',')[0];
        var eq = first.IndexOf('=');
        return eq >= 0 ? first[(eq + 1)..] : first;
    }

    private static string DomainFromNc(string nc) =>
        string.Join('.', nc.Split(',').Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..]));
}
