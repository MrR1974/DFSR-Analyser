using System.Net;

namespace DfsrAnalyser.Core.Collection;

/// <summary>A server to collect from.</summary>
/// <param name="Name">Name the logs are filed under in the case (the computer's short name, e.g. DFSRDC01).</param>
/// <param name="Host">Name used to connect (DNS name or short name).</param>
/// <param name="LogFolder">Folder holding Dfsr*.log; null means <c>\\Host\admin$\debug</c>.</param>
public sealed record ServerTarget(string Name, string Host, string? LogFolder = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveLogFolder => string.IsNullOrWhiteSpace(LogFolder) ? $@"\\{Host}\admin$\debug" : LogFolder!;
}

public sealed record DiscoveredMember(string Name, string? DnsHostName, Guid MemberGuid, bool IsDomainController, string ComputerDn);

public sealed record DiscoveredGroup(string Name, Guid Guid, bool IsSysvol, IReadOnlyList<DiscoveredMember> Members);

public sealed record DiscoveryResult(string Domain, string NamingContext, string? DomainController, IReadOnlyList<DiscoveredGroup> Groups);

/// <summary>Finds DFSR replication groups and their member servers (AD in production).</summary>
public interface IDirectoryService
{
    /// <param name="domain">DNS domain or DC name; null = the current computer's domain.</param>
    DiscoveryResult Discover(string? domain, NetworkCredential? credential, CancellationToken ct = default);
}

/// <summary>Alternate credentials were rejected (wrong user name or password, disabled account, …).</summary>
public sealed class CredentialRejectedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Runs remote work (share listing and copying, event-log RPC) under the requested identity.
/// In production: a LOGON32_LOGON_NEW_CREDENTIALS logon impersonated around the work, so every outbound
/// connection uses the alternate credentials regardless of connections the user already has.
/// </summary>
public interface IRemoteAccess
{
    /// <summary>Checks the credentials before any server is touched. Throws <see cref="CredentialRejectedException"/> when they are wrong.</summary>
    void Validate(NetworkCredential credential, string? domain);

    /// <summary>Runs <paramref name="work"/> as <paramref name="credential"/> (or as the current user when null).</summary>
    void Run(NetworkCredential? credential, Action work);
}

public sealed record EventLogRecordData(long RecordId, DateTime TimeUtc, int EventId, byte Level, string? Provider, string Message);

/// <summary>Reads the "DFS Replication" event log (remote session or an exported .evtx file).</summary>
public interface IEventLogSource
{
    /// <summary>Reads as the calling thread's identity (run it inside <see cref="IRemoteAccess.Run"/> for alternate credentials).</summary>
    IEnumerable<EventLogRecordData> ReadRemote(string host, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    IEnumerable<EventLogRecordData> ReadFile(string evtxPath, CancellationToken ct = default);
}

/// <summary>Current-user access, for runs without alternate credentials and for tests.</summary>
public sealed class CurrentUserAccess : IRemoteAccess
{
    public void Validate(NetworkCredential credential, string? domain) { }

    public void Run(NetworkCredential? credential, Action work) => work();
}
