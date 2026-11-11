using System.Globalization;
using System.Text.RegularExpressions;

namespace DfsrAnalyser.Core.Parsing;

/// <summary>
/// The two '*' lines at the top of every DFSR debug log file.
/// </summary>
public sealed partial record LogHeader
{
    public int Sequence { get; init; }
    public int Index { get; init; }
    public string Computer { get; init; } = "";
    /// <summary>Display name as written, e.g. "AUS Eastern Daylight Time".</summary>
    public string TimeZoneName { get; init; } = "";
    /// <summary>Offset in effect when the file was started, e.g. +11:00.</summary>
    public TimeSpan? GmtOffset { get; init; }
    /// <summary>Windows time zone ID, e.g. "AUS Eastern Standard Time". Used for DST-correct conversion.</summary>
    public string TzId { get; init; } = "";
    public string Build { get; init; } = "";
    public int? LogLevel { get; init; }
    public int? MaxEntryCount { get; init; }
    public int? MaxFileCount { get; init; }
    public string LogPath { get; init; } = "";

    [GeneratedRegex(@"Sequence:(?<seq>\d+)\s+Index:(?<idx>\d+)\s+Computer:(?<comp>\S+)")]
    private static partial Regex IdentityRx();

    [GeneratedRegex(@"TimeZone:(?<name>.*?)\s*\(GMT(?<off>[+-]\d{1,2}:\d{2})?\)")]
    private static partial Regex TimeZoneRx();

    [GeneratedRegex(@"\(TzId>(?<id>.*?)<dIzT\)")]
    private static partial Regex TzIdRx();

    [GeneratedRegex(@"Build:\[(?<b>.*?)\]")]
    private static partial Regex BuildRx();

    [GeneratedRegex(@"logLevel:(?<lvl>\d+)")]
    private static partial Regex LevelRx();

    [GeneratedRegex(@"maxEntryCount:(?<n>\d+)")]
    private static partial Regex MaxEntryRx();

    [GeneratedRegex(@"maxFileCount:(?<n>\d+)")]
    private static partial Regex MaxFileRx();

    [GeneratedRegex(@"logPath:(?<p>.*)$")]
    private static partial Regex LogPathRx();

    /// <summary>Folds one '*' line into the header. Unknown lines are ignored.</summary>
    public LogHeader Apply(string line)
    {
        var h = this;
        var m = IdentityRx().Match(line);
        if (m.Success)
        {
            h = h with
            {
                Sequence = int.Parse(m.Groups["seq"].Value, CultureInfo.InvariantCulture),
                Index = int.Parse(m.Groups["idx"].Value, CultureInfo.InvariantCulture),
                Computer = m.Groups["comp"].Value,
            };
        }

        m = TimeZoneRx().Match(line);
        if (m.Success)
        {
            TimeSpan? off = null;
            if (m.Groups["off"].Success)
            {
                var s = m.Groups["off"].Value;
                var neg = s[0] == '-';
                var ts = TimeSpan.ParseExact(s[1..].PadLeft(5, '0'), @"hh\:mm", CultureInfo.InvariantCulture);
                off = neg ? -ts : ts;
            }
            h = h with { TimeZoneName = m.Groups["name"].Value.Trim(), GmtOffset = off };
        }

        m = TzIdRx().Match(line);
        if (m.Success) h = h with { TzId = m.Groups["id"].Value.Trim() };

        m = BuildRx().Match(line);
        if (m.Success) h = h with { Build = m.Groups["b"].Value };

        m = LevelRx().Match(line);
        if (m.Success) h = h with { LogLevel = int.Parse(m.Groups["lvl"].Value, CultureInfo.InvariantCulture) };

        m = MaxEntryRx().Match(line);
        if (m.Success) h = h with { MaxEntryCount = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) };

        m = MaxFileRx().Match(line);
        if (m.Success) h = h with { MaxFileCount = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) };

        m = LogPathRx().Match(line);
        if (m.Success && line.Contains("logLevel:", StringComparison.Ordinal))
            h = h with { LogPath = m.Groups["p"].Value.Trim() };

        return h;
    }
}
