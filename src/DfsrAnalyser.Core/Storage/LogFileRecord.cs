namespace DfsrAnalyser.Core.Storage;

public sealed class LogFileRecord
{
    public int FileId { get; set; }
    public int ServerId { get; set; }
    public string FileName { get; set; } = "";
    public string? SourcePath { get; set; }
    public long? SizeBytes { get; set; }
    public string? HeaderComputer { get; set; }
    public string? TzId { get; set; }
    public string? TzName { get; set; }
    public string? TzBasis { get; set; }
    public int? LogLevel { get; set; }
    public int? MaxEntryCount { get; set; }
    public int? MaxFileCount { get; set; }
    public int? Sequence { get; set; }
    public int Index { get; set; }
    public long FirstLineHash { get; set; }
    public DateTime? FirstTsUtc { get; set; }
    public DateTime? LastTsUtc { get; set; }
    public int? LineCount { get; set; }
    public int? EventCount { get; set; }
    public int? OrphanLines { get; set; }
    public int? Unrecognised { get; set; }
    public int? LastEventLine { get; set; }
    public long? LastEventId { get; set; }
    public bool IsComplete { get; set; }
    public DateTime? ImportedAt { get; set; }
}
