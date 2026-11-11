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

/// <summary>
/// Makes alternate credentials apply to a host for file access (WNetAddConnection2 in production).
/// Dispose to disconnect. With no credential, returns a no-op connection.
/// </summary>
public interface IShareConnector
{
    IDisposable Connect(string host, NetworkCredential? credential);
}

public sealed record EventLogRecordData(long RecordId, DateTime TimeUtc, int EventId, byte Level, string? Provider, string Message);

/// <summary>Reads the "DFS Replication" event log (remote session or an exported .evtx file).</summary>
public interface IEventLogSource
{
    IEnumerable<EventLogRecordData> ReadRemote(string host, DateTime fromUtc, DateTime toUtc, NetworkCredential? credential, CancellationToken ct = default);
    IEnumerable<EventLogRecordData> ReadFile(string evtxPath, CancellationToken ct = default);
}

/// <summary>Credential-free connector, for the current user and for tests.</summary>
public sealed class NoCredentialConnector : IShareConnector
{
    public IDisposable Connect(string host, NetworkCredential? credential) => new Nothing();

    private sealed class Nothing : IDisposable { public void Dispose() { } }
}
