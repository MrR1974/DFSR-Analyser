using System.Globalization;
using System.Text;

namespace DfsrAnalyser.Core.Taxonomy;

/// <summary>
/// Compiles a <see cref="RuleSet"/> into a DuckDB <c>CASE</c> expression over the event columns.
/// The same expression categorises rows as they load and re-categorises a whole case.
/// </summary>
public static class RuleSql
{
    public const string FallbackExpression = "'Other (' || coalesce(module, '?') || ')'";

    public static string CaseExpression(RuleSet rules)
    {
        var sb = new StringBuilder("CASE");
        foreach (var r in rules.Rules)
            sb.Append("\n  WHEN ").Append(Condition(r)).Append(" THEN ").Append(Literal(r.Category));
        sb.Append("\n  ELSE ").Append(FallbackExpression).Append("\nEND");
        return sb.ToString();
    }

    public static string Condition(Rule r)
    {
        var parts = new List<string>();
        if (r.Module is { Count: > 0 } m) parts.Add(InOrEquals("module", m));
        if (r.ClassMethod is { Count: > 0 } cm) parts.Add(ClassMethodCondition(cm));
        if (r.Message is { } msg) parts.Add($"regexp_matches(message, {Literal(msg)})");
        if (r.Severity is { } sev)
            parts.Add($"severity = {sev switch { "error" => 2, "warn" => 1, _ => 0 }}");
        if (r.UsnReason is { } reason) parts.Add($"regexp_matches(coalesce(usn_reason, ''), {Literal(reason)})");
        if (r.TransferType is { } tt) parts.Add($"transfer_type = {Literal(tt)}");
        if (r.EventLogId is { Count: > 0 } ids) parts.Add($"event_log_id IN ({Ints(ids)})");
        if (r.ErrorCode is { Count: > 0 } codes) parts.Add($"(error_code IN ({Ints(codes)}) OR error_code_inner IN ({Ints(codes)}))");
        if (r.InnerErrorCode is { Count: > 0 } inner) parts.Add($"error_code_inner IN ({Ints(inner)})");
        return parts.Count == 0 ? "FALSE" : "(" + string.Join(" AND ", parts) + ")";
    }

    private static string ClassMethodCondition(List<string> values)
    {
        var exact = values.Where(v => !v.Contains('*')).ToList();
        var wild = values.Where(v => v.Contains('*')).ToList();
        var parts = new List<string>();
        if (exact.Count > 0) parts.Add(InOrEquals("class_method", exact));
        parts.AddRange(wild.Select(w => $"class_method LIKE {Literal(EscapeLike(w).Replace('*', '%'))} ESCAPE '\\'"));
        return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
    }

    private static string InOrEquals(string column, List<string> values) =>
        values.Count == 1 ? $"{column} = {Literal(values[0])}" : $"{column} IN ({string.Join(", ", values.Select(Literal))})";

    private static string Ints(List<int> values) => string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
}
