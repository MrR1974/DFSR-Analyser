namespace DfsrAnalyser.Core.Parsing;

/// <summary>A DFSR version identifier: "{DB GUID}-v&lt;version&gt;" (used by uid, gvsn, parent).</summary>
public readonly record struct VersionId(Guid DbGuid, long Version)
{
    public override string ToString() => $"{{{DbGuid.ToString().ToUpperInvariant()}}}-v{Version}";
}

/// <summary>
/// Properties extracted from an event's message and nested lines.
/// Everything is optional; a field is null when the event does not carry it.
/// </summary>
public sealed class EventFields
{
    public Guid? ConnId { get; set; }
    public Guid? CsId { get; set; }
    public string? CsName { get; set; }
    public Guid? ReplicaSetId { get; set; }
    public string? RgName { get; set; }

    public VersionId? Uid { get; set; }
    public VersionId? Gvsn { get; set; }
    public VersionId? Parent { get; set; }
    public long? Fid { get; set; }

    public string? FileName { get; set; }
    public string? Extension { get; set; }
    public int? Attributes { get; set; }
    public bool? IsFolder { get; set; }
    public bool? Present { get; set; }
    public bool? MeetInstallUpdate { get; set; }
    public string? UsnReason { get; set; }

    public int? ErrorCode { get; set; }
    public int? ErrorCodeInner { get; set; }
    public int? EventLogId { get; set; }
    public string? TransferType { get; set; }
    public long? StagedSize { get; set; }
    public int? Retries { get; set; }
    public string? UpdateType { get; set; }
}
