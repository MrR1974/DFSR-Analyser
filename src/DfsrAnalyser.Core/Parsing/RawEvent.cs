namespace DfsrAnalyser.Core.Parsing;

public enum Severity : byte
{
    Info = 0,
    Warn = 1,
    Error = 2,
}

/// <summary>
/// One dated line from a DFSR debug log plus the nested '+' lines that follow it.
/// </summary>
public sealed class RawEvent
{
    /// <summary>1-based physical line number of the dated line within its file.</summary>
    public int LineNo { get; init; }
    /// <summary>Server-local time as written in the log.</summary>
    public DateTime LocalTime { get; init; }
    public int Thread { get; init; }
    public string Module { get; init; } = "";
    public int SourceLine { get; init; }
    public Severity Severity { get; init; }
    /// <summary>e.g. "UsnConsumer::CreateNewRecord", or "main".</summary>
    public string ClassMethod { get; init; } = "";
    public string Message { get; init; } = "";
    /// <summary>Nested lines with the leading "+" and one tab removed.</summary>
    public List<string> Nested { get; } = [];
}
