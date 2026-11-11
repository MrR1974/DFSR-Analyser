using System.Globalization;

namespace DfsrAnalyser.App.Services;

/// <summary>Formats UTC timestamps for display, either as UTC or in the analyst's local time zone.</summary>
public static class TimeDisplay
{
    public static bool UseLocal { get; set; }

    public static string ZoneLabel => UseLocal ? "local" : "UTC";

    public static DateTime ToDisplay(DateTime utc) =>
        UseLocal ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local) : utc;

    public static DateTime FromDisplay(DateTime display) =>
        UseLocal ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(display, DateTimeKind.Unspecified), TimeZoneInfo.Local) : DateTime.SpecifyKind(display, DateTimeKind.Utc);

    public static string Full(DateTime utc) => ToDisplay(utc).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static string Short(DateTime utc) => ToDisplay(utc).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Span(TimeSpan t) => t.TotalDays >= 1 ? $"{t.TotalDays:0.#} d"
        : t.TotalHours >= 1 ? $"{t.TotalHours:0.#} h"
        : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#} min"
        : $"{t.TotalSeconds:0.#} s";
}
