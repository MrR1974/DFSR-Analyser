using System.Globalization;
using System.Text;

namespace DfsrAnalyser.Core.Query;

/// <summary>
/// RFC 4180 CSV writing for exports: UTF-8 with BOM (so Excel detects the encoding), CRLF rows,
/// fields quoted when they hold a comma, quote or line break. Text cells that a spreadsheet would
/// run as a formula (leading <c>= + - @</c>, tab or CR) get a leading apostrophe; DFSR messages such
/// as <c>-&gt; DONE …</c> would otherwise show as #NAME?.
/// </summary>
public sealed class CsvWriter : IDisposable
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private readonly TextWriter _w;
    private readonly bool _ownsWriter;
    private bool _firstField = true;

    public CsvWriter(TextWriter writer, bool ownsWriter = false)
    {
        _w = writer;
        _ownsWriter = ownsWriter;
    }

    /// <summary>Creates (or overwrites) a UTF-8-with-BOM file.</summary>
    public static CsvWriter Create(string path) =>
        new(new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)), ownsWriter: true);

    public long Rows { get; private set; }

    /// <summary>A text cell (formula-guarded).</summary>
    public CsvWriter Text(string? value) => Raw(Guard(value));

    /// <summary>A number or other value written as-is (invariant culture).</summary>
    public CsvWriter Value(object? value) => Raw(value switch
    {
        null => "",
        DateTime t => t.ToString(TimeFormat, CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    });

    public CsvWriter Time(DateTime? value) => Raw(value?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? "");

    public void Header(params string[] names)
    {
        foreach (var n in names) Raw(n);
        EndRow();
    }

    public void EndRow()
    {
        _w.Write("\r\n");
        _firstField = true;
        Rows++;
    }

    private CsvWriter Raw(string? s)
    {
        if (!_firstField) _w.Write(',');
        _firstField = false;
        if (string.IsNullOrEmpty(s)) return this;
        if (s.AsSpan().IndexOfAny(",\"\r\n") >= 0)
            _w.Write('"' + s.Replace("\"", "\"\"") + '"');
        else
            _w.Write(s);
        return this;
    }

    /// <summary>Prefixes an apostrophe to text a spreadsheet would treat as a formula.</summary>
    public static string? Guard(string? s) =>
        string.IsNullOrEmpty(s) || s[0] is not ('=' or '+' or '-' or '@' or '\t' or '\r') ? s : "'" + s;

    public void Dispose()
    {
        _w.Flush();
        if (_ownsWriter) _w.Dispose();
    }
}
