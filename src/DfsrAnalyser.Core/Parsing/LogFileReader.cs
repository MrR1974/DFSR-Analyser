using System.IO.Compression;
using System.Text;

namespace DfsrAnalyser.Core.Parsing;

/// <summary>
/// Streams one DFSR debug log (.log or .log.gz) as a header followed by events.
/// Accepts CRLF and LF, skips blank lines, and collects any '+' lines that precede
/// the first dated line (the tail of an entry split by a roll) as <see cref="LeadingOrphans"/>.
/// </summary>
public sealed class LogFileReader : IDisposable
{
    private readonly StreamReader _reader;
    private int _lineNo;
    private string? _pending;   // a dated line read ahead while finishing the previous event
    private bool _started;

    public LogHeader Header { get; private set; } = new();
    /// <summary>Nested lines found before the first dated line.</summary>
    public List<string> LeadingOrphans { get; } = [];
    /// <summary>Lines that were neither header, dated, nested nor blank; attached to the previous event as nested text.</summary>
    public int UnrecognisedLines { get; private set; }
    public int BlankLines { get; private set; }
    /// <summary>Physical lines read so far.</summary>
    public int LinesRead => _lineNo;
    /// <summary>First dated line of the file, raw, once seen.</summary>
    public string? FirstDatedLine { get; private set; }

    public LogFileReader(string path) : this(OpenStream(path)) { }

    public LogFileReader(Stream stream)
    {
        // Logs are ASCII in practice; UTF-8 decoding is a safe superset for that.
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
    }

    public static Stream OpenStream(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        Span<byte> magic = stackalloc byte[2];
        var n = fs.Read(magic);
        fs.Position = 0;
        return n == 2 && magic[0] == 0x1F && magic[1] == 0x8B ? new GZipStream(fs, CompressionMode.Decompress) : fs;
    }

    /// <summary>
    /// Reads the header and leading orphans, stopping just before the first event.
    /// Called automatically by <see cref="ReadEvents"/>.
    /// </summary>
    public void ReadPreamble()
    {
        if (_started) return;
        _started = true;
        while (_reader.ReadLine() is { } line)
        {
            _lineNo++;
            if (line.Length == 0) { BlankLines++; continue; }
            switch (line[0])
            {
                case '*':
                    Header = Header.Apply(line);
                    continue;
                case '+':
                    LeadingOrphans.Add(LineParser.NestedText(line));
                    continue;
            }
            if (LineParser.IsDated(line))
            {
                _pending = line;
                FirstDatedLine = line;
                return;
            }
            UnrecognisedLines++;
            LeadingOrphans.Add(line.TrimEnd());
        }
    }

    /// <summary>
    /// Yields events. When <paramref name="fromLine"/> is given, events whose dated line
    /// precedes it are skipped (used to resume a partially imported active log).
    /// </summary>
    public IEnumerable<RawEvent> ReadEvents(int fromLine = 0)
    {
        ReadPreamble();
        RawEvent? current = null;
        if (_pending is not null)
        {
            LineParser.TryParseDated(_pending, _lineNo, out current);
            _pending = null;
        }

        while (_reader.ReadLine() is { } line)
        {
            _lineNo++;
            if (line.Length == 0) { BlankLines++; continue; }

            if (line[0] == '+')
            {
                current?.Nested.Add(LineParser.NestedText(line));
                continue;
            }

            if (LineParser.TryParseDated(line, _lineNo, out var next))
            {
                if (current is not null && current.LineNo >= fromLine) yield return current;
                current = next;
                continue;
            }

            if (line[0] == '*')
            {
                // A header mid-file is unexpected; fold it in rather than lose it.
                Header = Header.Apply(line);
                continue;
            }

            UnrecognisedLines++;
            current?.Nested.Add(line.TrimEnd());
        }

        if (current is not null && current.LineNo >= fromLine) yield return current;
    }

    public void Dispose() => _reader.Dispose();
}
