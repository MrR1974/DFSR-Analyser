using System.ComponentModel;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DfsrAnalyser.Core.Collection;
using Microsoft.Win32.SafeHandles;

namespace DfsrAnalyser.Collection;

/// <summary>
/// Alternate credentials for remote work.
/// <list type="bullet">
/// <item><b>Validate</b>: an LDAP bind to the domain with the credentials. A rejected logon (LDAP 49) is fatal
/// with a clear message; if the domain cannot be reached the check is skipped with a warning and the first
/// server access will report the problem instead.</item>
/// <item><b>Run</b>: <c>LogonUser(LOGON32_LOGON_NEW_CREDENTIALS)</c> creates a logon session whose <em>outbound</em>
/// network identity is the alternate account (local access is unchanged); the work runs impersonating it. SMB and
/// RPC connections made inside therefore authenticate as the alternate account and never collide with the user's
/// existing connections (no error 1219). NEW_CREDENTIALS does not check the password itself — hence Validate.</item>
/// </list>
/// </summary>
public sealed class ImpersonatingRemoteAccess(CollectionLog? log = null) : IRemoteAccess, IDisposable
{
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderWinNt50 = 3;
    private const int LdapInvalidCredentials = 49;

    private readonly object _gate = new();
    private SafeAccessTokenHandle? _token;
    private string? _tokenFor;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);

    public void Validate(NetworkCredential credential, string? domain)
    {
        var who = Who(credential);
        // A NetBIOS domain prefix (DFSRTEST\user) cannot be used as an LDAP host; use the given DNS domain or the current one.
        var target = !string.IsNullOrWhiteSpace(domain) ? domain : credential.Domain is { } d && d.Contains('.') ? d : null;
        log?.Info($"Checking the credentials for {who} with an LDAP bind to {target ?? "the current domain"}.");
        try
        {
            using var conn = new LdapConnection(new LdapDirectoryIdentifier(target, 389), credential, AuthType.Negotiate);
            conn.SessionOptions.ProtocolVersion = 3;
            conn.SessionOptions.Signing = true;
            conn.SessionOptions.Sealing = true;
            conn.Timeout = TimeSpan.FromSeconds(30);
            conn.Bind();
            log?.Info($"Credentials for {who} accepted by the domain.");
        }
        catch (LdapException ex) when (ex.ErrorCode == LdapInvalidCredentials)
        {
            log?.Error($"Credentials for {who} were rejected", ex: ex);
            throw new CredentialRejectedException(
                $"The user name or password for {who} is incorrect (or the account is disabled, locked or expired). Nothing was collected.", ex);
        }
        catch (LdapException ex)
        {
            log?.Warn($"Could not check the credentials for {who} ({ex.Message}). Continuing; server access will report a logon failure if they are wrong.");
        }
    }

    public void Run(NetworkCredential? credential, Action work)
    {
        if (credential is null || string.IsNullOrEmpty(credential.UserName))
        {
            work();
            return;
        }
        WindowsIdentity.RunImpersonated(TokenFor(credential), work);
    }

    private SafeAccessTokenHandle TokenFor(NetworkCredential credential)
    {
        var key = Who(credential);
        lock (_gate)
        {
            if (_token is { IsInvalid: false } && _tokenFor == key) return _token;
            _token?.Dispose();
            var domain = string.IsNullOrEmpty(credential.Domain) ? null : credential.Domain;
            if (!LogonUser(credential.UserName, domain, credential.Password, Logon32LogonNewCredentials, Logon32ProviderWinNt50, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"LogonUser (new credentials) failed for {key}");
            log?.Info($"Remote access will use {key} (new-credentials logon; local access is unchanged).");
            _token = token;
            _tokenFor = key;
            return token;
        }
    }

    private static string Who(NetworkCredential c) => string.IsNullOrEmpty(c.Domain) ? c.UserName : $@"{c.Domain}\{c.UserName}";

    public void Dispose()
    {
        lock (_gate) _token?.Dispose();
    }
}
