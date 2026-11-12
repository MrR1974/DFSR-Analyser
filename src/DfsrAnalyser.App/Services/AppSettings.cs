using System.Text.Json;

namespace DfsrAnalyser.App.Services;

/// <summary>Per-user settings in %LOCALAPPDATA%\DfsrAnalyser\settings.json. Failures are ignored: settings are a convenience.</summary>
public sealed class AppSettings
{
    public List<string> RecentCases { get; set; } = [];
    public bool UseLocalTime { get; set; }

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DfsrAnalyser", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void AddRecent(string folder)
    {
        RecentCases.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentCases.Insert(0, folder);
        if (RecentCases.Count > 8) RecentCases.RemoveRange(8, RecentCases.Count - 8);
        Save();
    }
}
