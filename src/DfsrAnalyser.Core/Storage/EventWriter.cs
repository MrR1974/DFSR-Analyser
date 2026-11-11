using DfsrAnalyser.Core.Parsing;
using DuckDB.NET.Data;

namespace DfsrAnalyser.Core.Storage;

/// <summary>Writes parsed events into <c>event</c> and <c>event_nested</c>. Data is committed on dispose.</summary>
public interface IEventWriter : IDisposable
{
    void Write(long eventId, int fileId, int serverId, RawEvent ev, DateTime tsUtc, EventFields f);
}

/// <summary>
/// Writer using DuckDB.NET row appenders. Simple, but ~175 ns per value through the binding;
/// kept for small writes. Bulk imports use <see cref="CsvBulkWriter"/>.
/// Column order must match <see cref="Schema.Ddl"/>.
/// </summary>
public sealed class EventWriter : IEventWriter
{
    private readonly DuckDBAppender _events;
    private readonly DuckDBAppender _nested;

    internal EventWriter(DuckDBConnection connection)
    {
        _events = connection.CreateAppender("event");
        _nested = connection.CreateAppender("event_nested");
    }

    public void Write(long eventId, int fileId, int serverId, RawEvent ev, DateTime tsUtc, EventFields f)
    {
        _events.CreateRow()
            .AppendValue(eventId)
            .AppendValue(fileId)
            .AppendValue(serverId)
            .AppendValue(ev.LineNo)
            .AppendValue(tsUtc)
            .AppendValue(ev.LocalTime)
            .AppendValue(ev.Thread)
            .AppendValue(ev.Module)
            .AppendValue(ev.SourceLine)
            .AppendValue((byte)ev.Severity)
            .AppendValue(ev.ClassMethod)
            .AppendValue(ev.Message)
            .AppendValue(ev.Nested.Count)
            .AppendValue((string?)null)          // category (phase 2)
            .AppendValue(f.ConnId)
            .AppendValue(f.CsId)
            .AppendValue(f.CsName)
            .AppendValue(f.ReplicaSetId)
            .AppendValue(f.RgName)
            .AppendValue(f.Uid?.DbGuid)
            .AppendValue(f.Uid?.Version)
            .AppendValue(f.Gvsn?.DbGuid)
            .AppendValue(f.Gvsn?.Version)
            .AppendValue(f.Parent?.DbGuid)
            .AppendValue(f.Parent?.Version)
            .AppendValue(f.Fid)
            .AppendValue(f.FileName)
            .AppendValue(f.Extension)
            .AppendValue(f.Attributes)
            .AppendValue(f.IsFolder)
            .AppendValue(f.Present)
            .AppendValue(f.MeetInstallUpdate)
            .AppendValue(f.UsnReason)
            .AppendValue(f.ErrorCode)
            .AppendValue(f.ErrorCodeInner)
            .AppendValue(f.EventLogId)
            .AppendValue(f.TransferType)
            .AppendValue(f.StagedSize)
            .AppendValue(f.Retries)
            .AppendValue(f.UpdateType)
            .EndRow();

        if (ev.Nested.Count > 0)
        {
            _nested.CreateRow()
                .AppendValue(eventId)
                .AppendValue(string.Join('\n', ev.Nested))
                .EndRow();
        }
    }

    public void Dispose()
    {
        _events.Dispose();
        _nested.Dispose();
    }
}
