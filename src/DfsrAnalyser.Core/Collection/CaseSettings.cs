using System.Text.Json;
using System.Text.Json.Serialization;

namespace DfsrAnalyser.Core.Collection;

/// <summary>A saved collection: which servers, how to reach them, and when it last ran (for top-up).</summary>
public sealed class CollectionProfile
{
    public string Name { get; set; } = "";
    public string? Domain { get; set; }
    public List<ServerTarget> Servers { get; set; } = [];
    public bool IncludeEventLog { get; set; }
    /// <summary>End of the range last collected (UTC); a top-up starts a little before it.</summary>
    public DateTime? LastCollectedUtc { get; set; }

    /// <summary>Overlap between runs; already-imported files are skipped and the active log resumes, so overlap costs little.</summary>
    public static readonly TimeSpan TopUpOverlap = TimeSpan.FromMinutes(10);
}

/// <summary>
/// <c>case.json</c>: per-case settings that are not event data. Credentials are never stored.
/// </summary>
public sealed class CaseSettings
{
    public const string FileName = "case.json";

    public List<CollectionProfile> Profiles { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CaseSettings Load(string caseFolder)
    {
        var path = Path.Combine(caseFolder, FileName);
        if (!File.Exists(path)) return new CaseSettings();
        return JsonSerializer.Deserialize<CaseSettings>(File.ReadAllText(path), Json) ?? new CaseSettings();
    }

    public void Save(string caseFolder) =>
        File.WriteAllText(Path.Combine(caseFolder, FileName), JsonSerializer.Serialize(this, Json));

    public void Upsert(CollectionProfile profile)
    {
        Profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        Profiles.Add(profile);
    }
}
