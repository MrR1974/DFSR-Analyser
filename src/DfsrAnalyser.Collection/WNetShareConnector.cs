using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using DfsrAnalyser.Core.Collection;

namespace DfsrAnalyser.Collection;

/// <summary>
/// Applies alternate credentials to a host by connecting <c>\\host\IPC$</c> with WNetAddConnection2; every
/// UNC access to that host then uses them until the connection is cancelled on dispose. With no credential
/// nothing is done and the current user's identity is used.
/// </summary>
public sealed class WNetShareConnector(CollectionLog? log = null) : IShareConnector
{
    private const int ResourceTypeAny = 0;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorAlreadyAssigned = 85;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    public IDisposable Connect(string host, NetworkCredential? credential)
    {
        if (credential is null || string.IsNullOrEmpty(credential.UserName)) return new Connection(null, null);

        var remote = $@"\\{host}\IPC$";
        var user = string.IsNullOrEmpty(credential.Domain) ? credential.UserName : $@"{credential.Domain}\{credential.UserName}";
        var nr = new NetResource { Type = ResourceTypeAny, RemoteName = remote };
        var rc = WNetAddConnection2(ref nr, credential.Password, user, 0);
        switch (rc)
        {
            case 0:
                log?.Info($"Connected {remote} as {user}.", host);
                return new Connection(remote, log);
            case ErrorSessionCredentialConflict:
                // Windows allows one set of credentials per server per logon session.
                log?.Warn($"{remote}: an existing connection to this server uses different credentials (error 1219). " +
                          "Continuing with that connection. To use the alternate account, close it first (net use \\\\" + host + "\\IPC$ /delete).", host);
                return new Connection(null, null);
            case ErrorAlreadyAssigned:
                return new Connection(null, null);
            default:
                throw new Win32Exception(rc, $"Could not connect to {remote} as {user}: {new Win32Exception(rc).Message}");
        }
    }

    private sealed class Connection(string? remote, CollectionLog? log) : IDisposable
    {
        public void Dispose()
        {
            if (remote is null) return;
            var rc = WNetCancelConnection2(remote, 0, true);
            if (rc != 0) log?.Warn($"Disconnecting {remote} returned {rc}.");
        }
    }
}
