using System.Security.Cryptography;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Destino de escrita que descarta os bytes e calcula o hash do conteúdo.
/// Usado no upload multipart direto: o arquivo é montado pelo storage (não passa
/// pela API), então o SHA-256 é calculado numa passada de leitura pelo loopback.
/// </summary>
internal sealed class HashingWriteStream : Stream
{
    private readonly IncrementalHash _hash;

    public HashingWriteStream(HashAlgorithmName algorithm)
    {
        _hash = IncrementalHash.CreateHash(algorithm);
    }

    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count > 0)
            _hash.AppendData(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!buffer.IsEmpty)
            _hash.AppendData(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!buffer.IsEmpty)
            _hash.AppendData(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }
}
