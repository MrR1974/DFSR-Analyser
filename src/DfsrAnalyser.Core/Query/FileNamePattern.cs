using System.Text;

namespace DfsrAnalyser.Core.Query;

/// <summary>
/// A wildcard file name filter: patterns separated by <c>;</c>, where <c>*</c> matches any run of characters
/// and <c>?</c> exactly one. Matching is case-insensitive and covers the whole name, so a pattern without
/// wildcards is an exact name (as with <c>dir</c>). A leading <c>!</c> excludes.
/// <para>Include patterns are OR-ed and only match events that carry a file name. Exclude patterns remove
/// matching names; events without a file name are kept unless there is also an include pattern.</para>
/// </summary>
public sealed class FileNamePattern
{
    public IReadOnlyList<string> Includes { get; }
    public IReadOnlyList<string> Excludes { get; }
    public bool IsEmpty => Includes.Count == 0 && Excludes.Count == 0;

    private FileNamePattern(List<string> includes, List<string> excludes)
    {
        Includes = includes;
        Excludes = excludes;
    }

    public static FileNamePattern Parse(string? text)
    {
        var includes = new List<string>();
        var excludes = new List<string>();
        foreach (var raw in (text ?? "").Split(';'))
        {
            var part = raw.Trim();
            var exclude = part.StartsWith('!');
            if (exclude) part = part[1..].Trim();
            if (part.Length == 0) continue;
            (exclude ? excludes : includes).Add(part);
        }
        return new FileNamePattern(includes, excludes);
    }

    /// <summary>True when the text contains a wildcard character.</summary>
    public static bool HasWildcard(string text) => text.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>
    /// Translates one wildcard pattern to a LIKE pattern using <c>\</c> as the escape character:
    /// <c>*</c> → <c>%</c>, <c>?</c> → <c>_</c>, and literal <c>%</c>, <c>_</c>, <c>\</c> escaped.
    /// </summary>
    public static string ToLike(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 4);
        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*': sb.Append('%'); break;
                case '?': sb.Append('_'); break;
                case '%' or '_' or '\\': sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// SQL condition over <paramref name="column"/> (a nullable VARCHAR expression), adding its parameters
    /// to <paramref name="args"/>. Null when the pattern is empty.
    /// </summary>
    public string? ToSql(string column, List<object?> args)
    {
        if (IsEmpty) return null;
        var parts = new List<string>();
        if (Includes.Count > 0)
        {
            parts.Add("(" + string.Join(" OR ", Includes.Select(_ => $"{column} ILIKE ? ESCAPE '\\'")) + ")");
            args.AddRange(Includes.Select(ToLike));
        }
        if (Excludes.Count > 0)
        {
            parts.Add($"({column} IS NULL OR NOT (" + string.Join(" OR ", Excludes.Select(_ => $"{column} ILIKE ? ESCAPE '\\'")) + "))");
            args.AddRange(Excludes.Select(ToLike));
        }
        return string.Join(" AND ", parts);
    }

    public override string ToString() =>
        string.Join(";", Includes.Concat(Excludes.Select(e => "!" + e)));
}
