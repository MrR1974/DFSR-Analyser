using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DfsrAnalyser.Core.Taxonomy;

/// <summary>A display category. Several rules may map to one category.</summary>
public sealed class CategoryDef
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>Hidden by default in views (companion lines, plumbing, routine detail).</summary>
    public bool Hidden { get; set; }
    /// <summary>Shown in event detail: what the entry means, and when it misleads.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// One match rule. Every condition given must hold; omitted conditions match anything.
/// Rules are evaluated in file order and the first match wins.
/// </summary>
public sealed class Rule
{
    public string Category { get; set; } = "";
    /// <summary>Module code(s), e.g. "MEET".</summary>
    public List<string>? Module { get; set; }
    /// <summary>Class::Method value(s); a value containing '*' is a wildcard ("Meet::*").</summary>
    public List<string>? ClassMethod { get; set; }
    /// <summary>RE2 regular expression matched (unanchored) against the message.</summary>
    public string? Message { get; set; }
    /// <summary>"error", "warn" or "info".</summary>
    public string? Severity { get; set; }
    /// <summary>RE2 regular expression matched against the USN reason.</summary>
    public string? UsnReason { get; set; }
    /// <summary>"RAWGET" or "RDCGET".</summary>
    public string? TransferType { get; set; }
    public List<int>? EventLogId { get; set; }
    /// <summary>Matches the outermost or innermost error code of the entry.</summary>
    public List<int>? ErrorCode { get; set; }
    /// <summary>Matches only the innermost (most specific) error code, looking through wrappers such as 9027.</summary>
    public List<int>? InnerErrorCode { get; set; }
    /// <summary>Free text for people editing the file; ignored by the engine.</summary>
    public string? Comment { get; set; }

    public bool HasCondition =>
        Module is { Count: > 0 } || ClassMethod is { Count: > 0 } || Message is not null || Severity is not null ||
        UsnReason is not null || TransferType is not null || EventLogId is { Count: > 0 } || ErrorCode is { Count: > 0 } ||
        InnerErrorCode is { Count: > 0 };
}

public sealed class RuleSet
{
    public const string PlumbingGroup = "Plumbing";
    public const string OtherGroup = "Other";
    public const string DefaultResourceName = "DfsrAnalyser.Core.Taxonomy.default-rules.json";

    public int Version { get; set; } = 1;
    public List<CategoryDef> Categories { get; set; } = [];
    public List<Rule> Rules { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SingleOrListConverter<string>(), new SingleOrListConverter<int>() },
    };

    public static RuleSet Parse(string json) =>
        JsonSerializer.Deserialize<RuleSet>(json, JsonOptions) ?? throw new RuleSetException("The rules file is empty.");

    public static RuleSet Load(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch (JsonException ex) { throw new RuleSetException($"{Path.GetFileName(path)}: {ex.Message}", ex); }
    }

    public static string DefaultJson()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultResourceName)
                      ?? throw new InvalidOperationException("Default rules resource missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static RuleSet Default() => Parse(DefaultJson());

    /// <summary>Structural checks. Regular expressions are checked by the database (RE2) when applied.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Categories)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) errors.Add("A category has no name.");
            else if (!names.Add(c.Name)) errors.Add($"Category '{c.Name}' is defined twice.");
            if (string.IsNullOrWhiteSpace(c.Group)) errors.Add($"Category '{c.Name}' has no group.");
            if (c.Name.StartsWith("Other (", StringComparison.Ordinal)) errors.Add($"Category '{c.Name}': names starting 'Other (' are reserved for the fallback.");
        }
        for (var i = 0; i < Rules.Count; i++)
        {
            var r = Rules[i];
            if (!names.Contains(r.Category)) errors.Add($"Rule {i + 1}: unknown category '{r.Category}'.");
            if (!r.HasCondition) errors.Add($"Rule {i + 1} ({r.Category}): has no conditions and would match everything.");
            if (r.Severity is not (null or "error" or "warn" or "info")) errors.Add($"Rule {i + 1}: severity must be error, warn or info.");
        }
        if (errors.Count > 0) throw new RuleSetException(string.Join(Environment.NewLine, errors));
    }
}

public sealed class RuleSetException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Lets a JSON property be either a single value or an array of values.</summary>
internal sealed class SingleOrListConverter<T> : JsonConverter<List<T>>
{
    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<T>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                list.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);
            return list;
        }
        return [JsonSerializer.Deserialize<T>(ref reader, options)!];
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
    {
        if (value.Count == 1) { JsonSerializer.Serialize(writer, value[0], options); return; }
        writer.WriteStartArray();
        foreach (var v in value) JsonSerializer.Serialize(writer, v, options);
        writer.WriteEndArray();
    }
}
