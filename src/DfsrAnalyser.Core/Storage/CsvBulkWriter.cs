using System.Globalization;
using System.Text;
using DfsrAnalyser.Core.Parsing;

namespace DfsrAnalyser.Core.Storage;

/// <summary>
/// Writes events to a pair of CSV files (event, event_nested) for loading with
/// <see cref="CaseStore.LoadCsv"/> (DuckDB's native COPY). Needs no database connection,
/// so several files can be prepared in parallel. Column order must match <see cref="Schema.Ddl"/>.
/// </summary>
public sealed class CsvBulkWriter : IEventWriter
{
    private readonly StreamWriter _events, _nested;
    private readonly StringBuilder _sb = new(1024);

    public string EventPath { get; }
    public string NestedPath { get; }
    public int Rows { get; private set; }

    public CsvBulkWriter(string folder)
    {
        Directory.CreateDirectory(folder);
        var stamp = Guid.NewGuid().ToString("N");
        EventPath = Path.Combine(folder, $"event-{stamp}.csv");
        NestedPath = Path.Combine(folder, $"nested-{stamp}.csv");
        _events = new StreamWriter(EventPath, false, new UTF8Encoding(false), 1 << 20);
        _nested = new StreamWriter(NestedPath, false, new UTF8Encoding(false), 1 << 20);
    }

    public void Write(long eventId, int fileId, int serverId, RawEvent ev, DateTime tsUtc, EventFields f)
    {
        var sb = _sb.Clear();
        Num(sb, eventId); Sep(sb);
        Num(sb, fileId); Sep(sb);
        Num(sb, serverId); Sep(sb);
        Num(sb, ev.LineNo); Sep(sb);
        Ts(sb, tsUtc); Sep(sb);
        Ts(sb, ev.LocalTime); Sep(sb);
        Num(sb, ev.Thread); Sep(sb);
        Str(sb, ev.Module); Sep(sb);
        Num(sb, ev.SourceLine); Sep(sb);
        Num(sb, (byte)ev.Severity); Sep(sb);
        Str(sb, ev.ClassMethod); Sep(sb);
        Str(sb, ev.Message); Sep(sb);
        Num(sb, ev.Nested.Count); Sep(sb);
        Sep(sb);                                   // category (phase 2)
        G(sb, f.ConnId); Sep(sb);
        G(sb, f.CsId); Sep(sb);
        Str(sb, f.CsName); Sep(sb);
        G(sb, f.ReplicaSetId); Sep(sb);
        Str(sb, f.RgName); Sep(sb);
        G(sb, f.Uid?.DbGuid); Sep(sb);
        N(sb, f.Uid?.Version); Sep(sb);
        G(sb, f.Gvsn?.DbGuid); Sep(sb);
        N(sb, f.Gvsn?.Version); Sep(sb);
        G(sb, f.Parent?.DbGuid); Sep(sb);
        N(sb, f.Parent?.Version); Sep(sb);
        N(sb, f.Fid); Sep(sb);
        Str(sb, f.FileName); Sep(sb);
        Str(sb, f.Extension); Sep(sb);
        N(sb, f.Attributes); Sep(sb);
        B(sb, f.IsFolder); Sep(sb);
        B(sb, f.Present); Sep(sb);
        B(sb, f.MeetInstallUpdate); Sep(sb);
        Str(sb, f.UsnReason); Sep(sb);
        N(sb, f.ErrorCode); Sep(sb);
        N(sb, f.ErrorCodeInner); Sep(sb);
        N(sb, f.EventLogId); Sep(sb);
        Str(sb, f.TransferType); Sep(sb);
        N(sb, f.StagedSize); Sep(sb);
        N(sb, f.Retries); Sep(sb);
        Str(sb, f.UpdateType);
        sb.Append('\n');
        _events.Write(sb);

        if (ev.Nested.Count > 0)
        {
            sb.Clear();
            Num(sb, eventId); Sep(sb);
            sb.Append('"');
            for (var i = 0; i < ev.Nested.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                AppendEscaped(sb, ev.Nested[i]);
            }
            sb.Append("\"\n");
            _nested.Write(sb);
        }
        Rows++;
    }

    public void Dispose()
    {
        _events.Dispose();
        _nested.Dispose();
    }

    /// <summary>Deletes the CSV files (after loading, or on failure).</summary>
    public void DeleteFiles()
    {
        if (Environment.GetEnvironmentVariable("DFSRA_KEEP_CSV") == "1") return;
        TryDelete(EventPath);
        TryDelete(NestedPath);
        static void TryDelete(string p) { try { File.Delete(p); } catch (IOException) { } }
    }

    // ---------- formatting ----------

    private static void Sep(StringBuilder sb) => sb.Append(',');

    private static void Num(StringBuilder sb, long v) => sb.Append(v);

    private static void N(StringBuilder sb, long? v) { if (v is { } x) sb.Append(x); }

    private static void N(StringBuilder sb, int? v) { if (v is { } x) sb.Append(x); }

    private static void B(StringBuilder sb, bool? v) { if (v is { } x) sb.Append(x ? "true" : "false"); }

    private static void G(StringBuilder sb, Guid? v)
    {
        if (v is not { } g) return;
        Span<char> buf = stackalloc char[36];
        g.TryFormat(buf, out var n, "D");
        sb.Append(buf[..n]);
    }

    private static void Ts(StringBuilder sb, DateTime t)
    {
        Span<char> buf = stackalloc char[23];
        t.TryFormat(buf, out var n, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        sb.Append(buf[..n]);
    }

    /// <summary>Strings are always quoted, so an empty string stays distinct from NULL (unquoted empty).</summary>
    private static void Str(StringBuilder sb, string? s)
    {
        if (s is null) return;
        sb.Append('"');
        AppendEscaped(sb, s);
        sb.Append('"');
    }

    private static void AppendEscaped(StringBuilder sb, string s)
    {
        if (s.AsSpan().IndexOf('"') < 0) { sb.Append(s); return; }
        foreach (var c in s)
        {
            if (c == '"') sb.Append('"');
            sb.Append(c);
        }
    }
}
