using System.Globalization;

namespace DfsrAnalyser.Core.Collection;

/// <summary>
/// Diagnostic log for a collection run, written to <c>&lt;case&gt;\logs\collect-*.log</c> as it happens so it
/// survives a crash. Every step records what was attempted and the outcome; this is what gets pasted back
/// when testing in an environment the developer cannot reach. Passwords are never written.
/// </summary>
public sealed class CollectionLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    public string? Path { get; }
    public event Action<string>? Line;

    public CollectionLog(string? folder)
    {
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        Path = System.IO.Path.Combine(folder, $"collect-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        // Shared for reading so the log can be opened in an editor while a collection runs.
        var fs = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs) { AutoFlush = true };
        Info("DFSR Analyser collection log. Times are local to this machine; UTC offset " +
             TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).ToString(@"hh\:mm", CultureInfo.InvariantCulture) + ".");
        Info($"Machine {Environment.MachineName}, user {Environment.UserDomainName}\\{Environment.UserName}, OS {Environment.OSVersion}.");
    }

    public void Info(string message, string? server = null) => Write("INFO ", server, message);

    public void Warn(string message, string? server = null) => Write("WARN ", server, message);

    public void Error(string message, string? server = null, Exception? ex = null) =>
        Write("ERROR", server, ex is null ? message : $"{message}: {Describe(ex)}");

    /// <summary>Exception type, message, Win32/HResult code and inner exceptions, on one line.</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var code = e is System.ComponentModel.Win32Exception w ? $" (Win32 {w.NativeErrorCode})" : $" (0x{e.HResult:X8})";
            parts.Add($"{e.GetType().Name}: {e.Message.Trim()}{code}");
        }
        return string.Join(" <- ", parts);
    }

    private void Write(string level, string? server, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {(server is null ? "" : $"[{server}] ")}{message}";
        lock (_gate) _writer?.WriteLine(line);
        Line?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_gate) _writer?.Dispose();
    }
}
