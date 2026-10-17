using System.Text;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Writer NDJSON append-only com buffer e flush periódico. Cada linha é um
/// frame JSON completo; a ordem de escrita é a ordem de chegada no worker.
/// </summary>
public sealed class TerminalRecordingNdjsonWriter : IAsyncDisposable
{
    private readonly string _fullPath;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _stream;
    private StreamWriter? _writer;
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private bool _disposed;

    public TerminalRecordingNdjsonWriter(string fullPath, TimeSpan flushInterval, ILogger? logger = null)
    {
        _fullPath = fullPath;
        _flushInterval = flushInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : flushInterval;
        _logger = logger;
    }

    public long LinesWritten { get; private set; }
    public long BytesWritten { get; private set; }

    public async Task AppendAsync(string line, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            EnsureOpen();

            var payload = line + "\n";
            await _writer!.WriteAsync(payload.AsMemory(), ct);

            LinesWritten++;
            BytesWritten += Encoding.UTF8.GetByteCount(payload);

            if (DateTime.UtcNow - _lastFlushUtc >= _flushInterval)
                await FlushCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_writer is not null)
                await FlushCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Flush + dispose. Idempotente.</summary>
    public async Task FinalizeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed)
                return;

            if (_writer is not null)
            {
                try
                {
                    await FlushCoreAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Falha ao dar flush no NDJSON {Path}", _fullPath);
                }
            }

            await DisposeCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FinalizeAsync(CancellationToken.None);
    }

    private void EnsureOpen()
    {
        if (_writer is not null)
            return;

        var directory = Path.GetDirectoryName(_fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _stream = new FileStream(
            _fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _writer = new StreamWriter(_stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024);
        _lastFlushUtc = DateTime.UtcNow;
    }

    private async Task FlushCoreAsync(CancellationToken ct)
    {
        if (_writer is null)
            return;

        await _writer.FlushAsync(ct);
        if (_stream is not null)
            await _stream.FlushAsync(ct);
        _lastFlushUtc = DateTime.UtcNow;
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_writer is not null)
        {
            await _writer.DisposeAsync();
            _writer = null;
            _stream = null;
        }
        else if (_stream is not null)
        {
            await _stream.DisposeAsync();
            _stream = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TerminalRecordingNdjsonWriter));
    }
}
