using System.Globalization;

namespace DfsrAnalyser.Core.Parsing;

/// <summary>
/// Pulls the analysis dimensions out of an event's message ("key:value" tokens) and its
/// nested lines (ID records "key   value", USN records "Key:   value", error stacks).
/// Values in the message take precedence over nested values.
/// Hand-written span scanning: this runs once per event (tens of millions per case).
/// </summary>
public static class FieldExtractor
{
    private const int MaxTokens = 64;

    public static EventFields Extract(RawEvent ev)
    {
        var f = new EventFields();
        var msg = ev.Message.AsSpan();

        ScanMessage(f, msg);
        ApplyNested(f, ev.Nested);

        // Error codes: outermost first, innermost (most specific) last.
        int? first = null, last = null;
        NoteErrors(msg, ref first, ref last);
        foreach (var line in ev.Nested)
            NoteErrors(line, ref first, ref last);
        f.ErrorCode = first;
        f.ErrorCodeInner = last;

        if (msg.Contains("RAWGET", StringComparison.Ordinal)) f.TransferType = "RAWGET";
        else if (msg.Contains("RDCGET", StringComparison.Ordinal)) f.TransferType = "RDCGET";

        if (f.FileName is { Length: > 0 } name)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1 && name.Length - dot <= 16)
                f.Extension = name[(dot + 1)..].ToLowerInvariant();
        }
        if (f.Attributes is { } a) f.IsFolder = (a & 0x10) != 0;
        return f;
    }

    // ------------------------------------------------------------------ message

    /// <summary>Splits a message into (key, value) pairs. Exposed for tests and diagnostics.</summary>
    public static List<(string Key, string Value)> MessageTokens(string msg)
    {
        var list = new List<(string, string)>();
        Span<int> starts = stackalloc int[MaxTokens];
        Span<int> colons = stackalloc int[MaxTokens];
        var n = FindKeys(msg, starts, colons);
        for (var i = 0; i < n; i++)
        {
            var key = msg.AsSpan(starts[i], colons[i] - starts[i]);
            var value = ValueOf(msg, key, colons[i] + 1, i + 1 < n ? starts[i + 1] : msg.Length);
            if (!value.IsEmpty) list.Add((key.ToString(), value.ToString()));
        }
        return list;
    }

    private static void ScanMessage(EventFields f, ReadOnlySpan<char> msg)
    {
        Span<int> starts = stackalloc int[MaxTokens];
        Span<int> colons = stackalloc int[MaxTokens];
        var n = FindKeys(msg, starts, colons);
        for (var i = 0; i < n; i++)
        {
            var key = msg[starts[i]..colons[i]];
            var value = ValueOf(msg, key, colons[i] + 1, i + 1 < n ? starts[i + 1] : msg.Length);
            if (value.IsEmpty) continue;
            switch (key)
            {
                case "connId": f.ConnId ??= ParseGuid(value); break;
                case "csId": f.CsId ??= ParseGuid(value); break;
                case "replicaSetId": f.ReplicaSetId ??= ParseGuid(value); break;
                case "csName": f.CsName ??= value.ToString(); break;
                case "rgName": f.RgName ??= value.ToString(); break;
                case "uid": f.Uid ??= ParseVersionId(value); break;
                case "gvsn": f.Gvsn ??= ParseVersionId(value); break;
                case "parent": f.Parent ??= ParseVersionId(value); break;
                case "fid": f.Fid ??= ParseHexLong(value); break;
                case "name":
                case "updateName":
                case "fileName":
                    f.FileName ??= value.ToString(); break;
                case "stagedSize": f.StagedSize ??= ParseLong(value); break;
                case "Retries": f.Retries ??= (int?)ParseLong(value); break;
                case "updateType": f.UpdateType ??= value.ToString(); break;
                case "eventId": f.EventLogId ??= (int?)ParseLong(value); break;
            }
        }
    }

    /// <summary>
    /// Finds "key:" tokens: an ASCII letter at the start or after whitespace, then letters/digits, then ':'.
    /// Returns the number found (capped at the span length).
    /// </summary>
    private static int FindKeys(ReadOnlySpan<char> msg, Span<int> starts, Span<int> colons)
    {
        var n = 0;
        var i = 0;
        while (i < msg.Length && n < starts.Length)
        {
            if (char.IsAsciiLetter(msg[i]) && (i == 0 || char.IsWhiteSpace(msg[i - 1])))
            {
                var j = i + 1;
                while (j < msg.Length && char.IsAsciiLetterOrDigit(msg[j])) j++;
                if (j < msg.Length && msg[j] == ':')
                {
                    starts[n] = i;
                    colons[n] = j;
                    n++;
                    i = j + 1;
                    continue;
                }
                i = j;
                continue;
            }
            i++;
        }
        return n;
    }

    /// <summary>Free-text keys (names) run to the next key token; others stop at whitespace.</summary>
    private static ReadOnlySpan<char> ValueOf(ReadOnlySpan<char> msg, ReadOnlySpan<char> key, int start, int nextKeyStart)
    {
        if (start >= msg.Length) return [];
        if (IsFreeText(key)) return msg[start..nextKeyStart].Trim();
        var end = start;
        while (end < msg.Length && !char.IsWhiteSpace(msg[end])) end++;
        return msg[start..end];
    }

    private static bool IsFreeText(ReadOnlySpan<char> key) =>
        key is "name" or "fileName" or "updateName" or "csName" or "rgName" or "newName";

    // ------------------------------------------------------------------ nested

    private static void ApplyNested(EventFields f, List<string> nested)
    {
        foreach (var raw in nested)
        {
            var line = raw.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '[') continue;

            var sep = line.IndexOfAny(' ', '\t', ':');
            if (sep <= 0) continue;
            var key = line[..sep];
            var value = line[(sep + 1)..].TrimStart(" \t:").Trim();
            if (value.IsEmpty) continue;

            switch (key)
            {
                case "uid": f.Uid ??= ParseVersionId(value); break;
                case "gvsn": f.Gvsn ??= ParseVersionId(value); break;
                case "parent": f.Parent ??= ParseVersionId(value); break;
                case "csId": f.CsId ??= ParseGuid(value); break;
                case "fid":
                case "FileRefNumber":
                    f.Fid ??= ParseHexLong(value); break;
                case "name":
                case "FileName":
                    f.FileName ??= value.ToString(); break;
                case "attributes":
                case "FileAttributes":
                    f.Attributes ??= (int?)ParseHexLong(value); break;
                case "present": f.Present ??= ParseBit(value); break;
                case "meetInstallUpdate": f.MeetInstallUpdate ??= ParseBit(value); break;
                case "Reason": f.UsnReason ??= value.ToString(); break;
                case "contentSetName": f.CsName ??= value.ToString(); break;
                case "replicationGroupName": f.RgName ??= value.ToString(); break;
                case "contentSetGuid": f.CsId ??= ParseGuid(value); break;
                case "replicationGroupGuid": f.ReplicaSetId ??= ParseGuid(value); break;
            }
        }
    }

    private static void NoteErrors(ReadOnlySpan<char> s, ref int? first, ref int? last)
    {
        while (true)
        {
            var i = s.IndexOf("Error:", StringComparison.Ordinal);
            if (i < 0) return;
            s = s[(i + 6)..];
            var end = 0;
            while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
            if (end > 0 && int.TryParse(s[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var c))
            {
                first ??= c;
                last = c;
            }
        }
    }

    // ------------------------------------------------------------------ value parsers

    public static VersionId? ParseVersionId(string s) => ParseVersionId(s.AsSpan());

    /// <summary>Parses "{GUID}-v123" (braces optional), ignoring anything after the digits.</summary>
    public static VersionId? ParseVersionId(ReadOnlySpan<char> s)
    {
        if (!TryGuidPrefix(s, out var g, out var rest)) return null;
        if (rest.Length < 3 || rest[0] != '-' || rest[1] != 'v') return null;
        rest = rest[2..];
        var end = 0;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        return end > 0 && long.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? new VersionId(g, v)
            : null;
    }

    public static Guid? ParseGuid(string s) => ParseGuid(s.AsSpan());

    public static Guid? ParseGuid(ReadOnlySpan<char> s) => TryGuidPrefix(s, out var g, out _) ? g : null;

    /// <summary>Reads a GUID at the start of <paramref name="s"/>, with or without braces.</summary>
    private static bool TryGuidPrefix(ReadOnlySpan<char> s, out Guid g, out ReadOnlySpan<char> rest)
    {
        g = default;
        rest = default;
        var start = s.Length > 0 && s[0] == '{' ? 1 : 0;
        if (s.Length < start + 36 || !Guid.TryParseExact(s.Slice(start, 36), "D", out g)) return false;
        var after = start + 36;
        if (start == 1 && after < s.Length && s[after] == '}') after++;
        rest = s[after..];
        return true;
    }

    private static long? ParseHexLong(ReadOnlySpan<char> s)
    {
        s = s.Trim();
        var end = 0;
        while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
        s = s[..end];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : null;
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static long? ParseLong(ReadOnlySpan<char> s)
    {
        s = s.TrimEnd(",;.)]");
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool? ParseBit(ReadOnlySpan<char> s) => s.Length > 0 && s[0] is '0' or '1' ? s[0] == '1' : null;
}
