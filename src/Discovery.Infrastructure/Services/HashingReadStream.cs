using System.Security.Cryptography;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Wrapper de leitura que calcula o hash do conteúdo conforme ele é consumido
/// pelo provider de storage. Permite gravar o SHA-256 do arquivo durante o
/// upload, sem uma segunda passada de leitura.
/// </summary>
internal sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash;

    public HashingReadStream(Stream inner, HashAlgorithmName algorithm)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hash = IncrementalHash.CreateHash(algorithm);
    }

    /// <summary>Hash em hexadecimal minúsculo. Só deve ser chamado após o upload.</summary>
    public string GetHashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
            _hash.AppendData(buffer, offset, read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
            _hash.AppendData(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = _inner.Seek(offset, origin);

        // Reinício de leitura (ex.: retry do SDK do storage) recomeça o hash,
        // senão os bytes lidos duas vezes seriam contados em dobro.
        if (position == 0)
            _hash.GetHashAndReset();

        return position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }
}
