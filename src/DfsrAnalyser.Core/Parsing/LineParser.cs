namespace DfsrAnalyser.Core.Parsing;

/// <summary>
/// Span-based parsing of individual log lines. Layout of a dated line:
/// <c>YYYYMMDD HH:MM:SS.mmm &lt;thread&gt; &lt;MODULE&gt; &lt;srcline&gt; [ERROR|WARN]? Class::Method message…</c>
/// with variable padding between the fields.
/// </summary>
public static class LineParser
{
    public static bool IsDated(ReadOnlySpan<char> line)
    {
        if (line.Length < 21 || line[8] != ' ' || line[11] != ':' || line[14] != ':' || line[17] != '.')
            return false;
        for (var i = 0; i < 8; i++)
            if (!char.IsAsciiDigit(line[i])) return false;
        return true;
    }

    public static bool TryParseDated(string line, int lineNo, out RawEvent? ev)
    {
        ev = null;
        var s = line.AsSpan();
        if (!IsDated(s) || !TryParseTimestamp(s[..21], out var local))
            return false;

        var pos = 21;
        if (!NextToken(s, ref pos, out var threadTok) || !int.TryParse(threadTok, out var thread)) return false;
        if (!NextToken(s, ref pos, out var moduleTok)) return false;
        if (!NextToken(s, ref pos, out var srcTok) || !int.TryParse(srcTok, out var srcLine)) return false;

        var severity = Severity.Info;
        var save = pos;
        if (!NextToken(s, ref pos, out var tok))
        {
            ev = new RawEvent { LineNo = lineNo, LocalTime = local, Thread = thread, Module = moduleTok.ToString(), SourceLine = srcLine };
            return true;
        }
        if (tok.SequenceEqual("[ERROR]")) severity = Severity.Error;
        else if (tok.SequenceEqual("[WARN]")) severity = Severity.Warn;
        else pos = save;

        NextToken(s, ref pos, out var methodTok);

        // Message: the rest after one separating space, keeping internal spacing.
        var msg = pos < s.Length ? s[pos..].TrimStart(' ').TrimEnd() : ReadOnlySpan<char>.Empty;

        ev = new RawEvent
        {
            LineNo = lineNo,
            LocalTime = local,
            Thread = thread,
            Module = moduleTok.ToString(),
            SourceLine = srcLine,
            Severity = severity,
            ClassMethod = methodTok.ToString(),
            Message = msg.ToString(),
        };
        return true;
    }

    /// <summary>Parses "YYYYMMDD HH:MM:SS.mmm".</summary>
    public static bool TryParseTimestamp(ReadOnlySpan<char> s, out DateTime value)
    {
        value = default;
        if (s.Length < 21) return false;
        if (!TryDigits(s.Slice(0, 4), out var y) || !TryDigits(s.Slice(4, 2), out var mo) || !TryDigits(s.Slice(6, 2), out var d) ||
            !TryDigits(s.Slice(9, 2), out var h) || !TryDigits(s.Slice(12, 2), out var mi) || !TryDigits(s.Slice(15, 2), out var sec) ||
            !TryDigits(s.Slice(18, 3), out var ms))
            return false;
        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), mo) || h > 23 || mi > 59 || sec > 59)
            return false;
        value = new DateTime(y, mo, d, h, mi, sec, ms, DateTimeKind.Unspecified);
        return true;
    }

    private static bool TryDigits(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c)) return false;
            value = value * 10 + (c - '0');
        }
        return true;
    }

    private static bool NextToken(ReadOnlySpan<char> s, ref int pos, out ReadOnlySpan<char> token)
    {
        while (pos < s.Length && s[pos] == ' ') pos++;
        var start = pos;
        while (pos < s.Length && s[pos] != ' ') pos++;
        token = s[start..pos];
        return token.Length > 0;
    }

    /// <summary>Strips the leading "+" and a single tab (or space) from a nested line.</summary>
    public static string NestedText(string line)
    {
        if (line.Length <= 1) return "";
        var start = line[1] is '\t' or ' ' ? 2 : 1;
        return start >= line.Length ? "" : line[start..].TrimEnd();
    }
}
