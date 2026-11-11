using System.Windows.Media;

namespace DfsrAnalyser.App.Services;

/// <summary>
/// Stable series colours: a value keeps the colour it was first given for the session, so a
/// category does not change colour when the filter changes. "Other" is always grey.
/// </summary>
public sealed class Palette
{
    private static readonly string[] Hex =
    [
        "#2E6FD8", "#E8833A", "#3AA655", "#D64545", "#8E5CC9", "#1FA3A3",
        "#C9A227", "#D45C9E", "#6B8E23", "#7A6A58", "#4A90C2", "#B5523B",
        "#5C6BC0", "#26A69A", "#AB47BC", "#8D6E63",
    ];

    public const string OtherHex = "#9AA0A6";
    private readonly Dictionary<string, string> _assigned = new(StringComparer.Ordinal);

    public string HexFor(string key)
    {
        if (key == "Other") return OtherHex;
        if (!_assigned.TryGetValue(key, out var hex))
        {
            hex = Hex[_assigned.Count % Hex.Length];
            _assigned[key] = hex;
        }
        return hex;
    }

    public bool TryGet(string key, out string hex) =>
        key == "Other" ? (hex = OtherHex) is not null : _assigned.TryGetValue(key, out hex!);

    public static Brush BrushFrom(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
