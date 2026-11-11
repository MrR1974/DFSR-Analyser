using System.Collections.Concurrent;

namespace DfsrAnalyser.Core.Parsing;

/// <summary>
/// Converts server-local log timestamps to UTC using the header's Windows time zone ID
/// (DST-aware). Falls back to the header's fixed GMT offset, then to treating times as UTC.
/// </summary>
public sealed class LocalTimeConverter
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeZoneInfo? _zone;
    private readonly TimeSpan? _fixedOffset;

    public LocalTimeConverter(LogHeader header)
    {
        _zone = string.IsNullOrWhiteSpace(header.TzId) ? null : Cache.GetOrAdd(header.TzId, Find);
        _fixedOffset = header.GmtOffset;
    }

    public enum Basis { TimeZoneId, FixedOffset, AssumedUtc }

    public Basis ConversionBasis => _zone is not null ? Basis.TimeZoneId : _fixedOffset is not null ? Basis.FixedOffset : Basis.AssumedUtc;

    public DateTime ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // GetUtcOffset never throws: invalid (spring-forward) times get the base offset,
        // ambiguous (fall-back) times get the standard offset.
        var offset = _zone?.GetUtcOffset(unspecified) ?? _fixedOffset ?? TimeSpan.Zero;
        return DateTime.SpecifyKind(unspecified - offset, DateTimeKind.Utc);
    }

    private static TimeZoneInfo? Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
}
