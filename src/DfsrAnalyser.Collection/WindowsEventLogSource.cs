using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net;
using System.Security;
using DfsrAnalyser.Core.Collection;

namespace DfsrAnalyser.Collection;

/// <summary>
/// Reads the "DFS Replication" event log through an EventLogSession (remote RPC; uses the given credentials
/// or the current user), or an exported .evtx file. Messages are rendered with the publisher's metadata
/// where available; otherwise the event's insertion strings are joined.
/// </summary>
public sealed class WindowsEventLogSource : IEventLogSource
{
    public const string LogName = "DFS Replication";

    public IEnumerable<EventLogRecordData> ReadRemote(string host, DateTime fromUtc, DateTime toUtc, NetworkCredential? credential, CancellationToken ct = default)
    {
        using var session = credential is null || string.IsNullOrEmpty(credential.UserName)
            ? new EventLogSession(host)
            : new EventLogSession(host, credential.Domain, credential.UserName, ToSecure(credential.Password), SessionAuthentication.Default);
        var query = new EventLogQuery(LogName, PathType.LogName, TimeFilter(fromUtc, toUtc)) { Session = session };
        foreach (var r in Read(query, ct)) yield return r;
    }

    public IEnumerable<EventLogRecordData> ReadFile(string evtxPath, CancellationToken ct = default) =>
        Read(new EventLogQuery(evtxPath, PathType.FilePath), ct);

    private static IEnumerable<EventLogRecordData> Read(EventLogQuery query, CancellationToken ct)
    {
        using var reader = new EventLogReader(query);
        while (reader.ReadEvent() is { } e)
        {
            using (e)
            {
                ct.ThrowIfCancellationRequested();
                string message;
                try { message = e.FormatDescription() ?? Fallback(e); }
                catch (EventLogException) { message = Fallback(e); }
                var time = e.TimeCreated?.ToUniversalTime() ?? DateTime.MinValue;
                yield return new EventLogRecordData(e.RecordId ?? 0, DateTime.SpecifyKind(time, DateTimeKind.Utc), e.Id, e.Level ?? 4, e.ProviderName, message);
            }
        }
    }

    private static string Fallback(EventRecord e) =>
        string.Join(" | ", e.Properties.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture)));

    private static string TimeFilter(DateTime fromUtc, DateTime toUtc) =>
        $"*[System[TimeCreated[@SystemTime>='{fromUtc:yyyy-MM-ddTHH:mm:ss.fffZ}' and @SystemTime<='{toUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";

    private static SecureString ToSecure(string s)
    {
        var ss = new SecureString();
        foreach (var c in s) ss.AppendChar(c);
        ss.MakeReadOnly();
        return ss;
    }
}
