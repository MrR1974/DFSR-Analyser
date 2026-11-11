using DfsrAnalyser.Core.Import;
using DfsrAnalyser.Core.Parsing;

namespace DfsrAnalyser.Core.Collection;

public sealed record RemoteFile(string Path, string Name, long Size, DateTime LastWriteUtc);

public enum FileDecision { Copy, BeforeRange, AfterRange, AlreadyImported, Unreadable }

public sealed record PlannedFile(RemoteFile File, FileDecision Decision, int? Index, int? Sequence, DateTime? FirstEventUtc, DateTime? EndUtc, string? Note);

/// <summary>
/// Decides which of a server's log files to copy for a time range.
/// <list type="number">
/// <item>Files last written before the range starts are skipped without opening them.</item>
/// <item>The rest are probed (header and first event line only; a .gz is decompressed only that far).</item>
/// <item>Sorted by first event, a file spans from its first event to the next file's first event (the
/// newest to its last write); files overlapping the range are copied.</item>
/// <item>Files the case already holds complete (same server, Index and first line) are skipped: top-up.</item>
/// </list>
/// </summary>
public static class CollectionPlanner
{
    /// <summary>Lists Dfsr*.log and Dfsr*.log.gz in a folder (a UNC path or a local folder).</summary>
    public static List<RemoteFile> ListLogs(string folder)
    {
        var dir = new DirectoryInfo(folder);
        return dir.EnumerateFiles("Dfsr*.log").Concat(dir.EnumerateFiles("Dfsr*.log.gz"))
            .Select(f => new RemoteFile(f.FullName, f.Name, f.Length, f.LastWriteTimeUtc))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <param name="alreadyComplete">(Index, first-line hash) → true when the case holds that file complete.</param>
    public static List<PlannedFile> Plan(IReadOnlyList<RemoteFile> files, DateTime? fromUtc, DateTime? toUtc,
        Func<int, long, bool>? alreadyComplete = null, Func<string, Stream>? open = null)
    {
        open ??= LogFileReader.OpenStream;
        var result = new List<PlannedFile>();
        var probed = new List<(RemoteFile File, int Index, int Sequence, DateTime First, long Hash)>();

        foreach (var f in files)
        {
            if (fromUtc is { } from && f.LastWriteUtc < from)
            {
                result.Add(new PlannedFile(f, FileDecision.BeforeRange, null, null, null, f.LastWriteUtc, "last written before the range"));
                continue;
            }
            try
            {
                using var reader = new LogFileReader(open(f.Path));
                reader.ReadPreamble();
                if (reader.FirstDatedLine is null || !LineParser.TryParseDated(reader.FirstDatedLine, 1, out var first))
                {
                    result.Add(new PlannedFile(f, FileDecision.Unreadable, reader.Header.Index, reader.Header.Sequence, null, null, "no dated line"));
                    continue;
                }
                var firstUtc = new LocalTimeConverter(reader.Header).ToUtc(first!.LocalTime);
                probed.Add((f, reader.Header.Index, reader.Header.Sequence, firstUtc, Importer.Fnv1a64(reader.FirstDatedLine)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                result.Add(new PlannedFile(f, FileDecision.Unreadable, null, null, null, null, CollectionLog.Describe(ex)));
            }
        }

        var ordered = probed.OrderBy(p => p.First).ThenBy(p => p.Index).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var end = i + 1 < ordered.Count ? ordered[i + 1].First : (p.File.LastWriteUtc > p.First ? p.File.LastWriteUtc : p.First);
            FileDecision decision;
            string? note = null;
            if (toUtc is { } to && p.First >= to) { decision = FileDecision.AfterRange; note = "starts after the range"; }
            else if (fromUtc is { } from && end < from && i + 1 < ordered.Count) { decision = FileDecision.BeforeRange; note = "ends before the range"; }
            else if (alreadyComplete?.Invoke(p.Index, p.Hash) == true) { decision = FileDecision.AlreadyImported; note = "already in the case"; }
            else decision = FileDecision.Copy;
            result.Add(new PlannedFile(p.File, decision, p.Index, p.Sequence, p.First, end, note));
        }
        return [.. result.OrderBy(r => r.FirstEventUtc ?? r.File.LastWriteUtc)];
    }
}
