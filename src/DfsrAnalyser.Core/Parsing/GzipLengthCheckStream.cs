using System.Buffers.Binary;
using System.IO.Compression;

namespace DfsrAnalyser.Core.Parsing;

/// <summary>
/// Decompresses a .gz file and, on reaching the end, checks the byte count against the length recorded in the
/// gzip trailer. A truncated or damaged copy (an interrupted transfer) otherwise decompresses "successfully" up
/// to the cut and would be imported as a complete, shorter log.
/// </summary>
internal sealed class GzipLengthCheckStream : Stream
{
    private readonly GZipStream _inner;
    private readonly uint _expected;
    private readonly string _path;
    private long _count;
    private bool _checked;

    public GzipLengthCheckStream(FileStream file, string path)
    {
        _path = path;
        Span<byte> trailer = stackalloc byte[4];
        if (file.Length >= 18)
        {
            file.Position = file.Length - 4;
            file.ReadExactly(trailer);
            _expected = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        }
        file.Position = 0;
        _inner = new GZipStream(file, CompressionMode.Decompress);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n;
        try { n = _inner.Read(buffer); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{Path.GetFileName(_path)} is damaged: {ex.Message}", ex); }
        _count += n;
        if (n == 0 && buffer.Length > 0 && !_checked)
        {
            _checked = true;
            if ((uint)_count != _expected)
                throw new InvalidDataException(
                    $"{Path.GetFileName(_path)} is truncated or damaged: it decompresses to {_count:N0} bytes but records {_expected:N0}. Copy it again.");
        }
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _count; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
