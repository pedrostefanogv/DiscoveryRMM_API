using System.Diagnostics;

namespace Discovery.Infrastructure.Services.ObjectStorage;

/// <summary>Contadores de trafego observados no processo (desde o start).</summary>
public sealed record ObjectStorageTelemetrySnapshot(
    DateTime SinceUtc,
    long Uploads,
    long Downloads,
    long Presigns,
    long Heads,
    long Deletes,
    long BytesIn,
    long BytesOut,
    long Operations,
    long Failures,
    double ErrorRate,
    double P95LatencyMs,
    double MaxLatencyMs,
    int InFlight,
    DateTime? LastSuccessAtUtc,
    DateTime? LastFailureAtUtc,
    string? LastError);

/// <summary>
/// Metricas PASSIVAS de object storage: contam apenas operacoes que a aplicacao
/// ja executou normalmente. Nao geram nenhuma chamada adicional ao S3 — e o que
/// permite mostrar uso/saude sem sobrecarregar um endpoint externo.
///
/// Singleton. Contadores cumulativos desde o start do processo; a latencia p95
/// usa uma amostra deslizante limitada em memoria.
/// </summary>
public sealed class ObjectStorageTelemetry
{
    private const int MaxLatencySamples = 2048;
    private const int MaxErrorLength = 300;

    private readonly object _sync = new();
    private readonly Queue<double> _latencySamplesMs = new(MaxLatencySamples);
    private readonly DateTime _sinceUtc = DateTime.UtcNow;

    private long _uploads;
    private long _downloads;
    private long _presigns;
    private long _heads;
    private long _deletes;
    private long _bytesIn;
    private long _bytesOut;
    private long _operations;
    private long _failures;
    private int _inFlight;
    private DateTime? _lastSuccessAtUtc;
    private DateTime? _lastFailureAtUtc;
    private string? _lastError;

    public void OperationStarted() => Interlocked.Increment(ref _inFlight);

    public void OperationFinished() => Interlocked.Decrement(ref _inFlight);

    public void Record(string operation, long bytes, TimeSpan elapsed, bool success, string? error = null)
    {
        Interlocked.Increment(ref _operations);

        switch (operation)
        {
            case "upload":
                Interlocked.Increment(ref _uploads);
                Interlocked.Add(ref _bytesIn, Math.Max(0, bytes));
                break;
            case "download":
                Interlocked.Increment(ref _downloads);
                Interlocked.Add(ref _bytesOut, Math.Max(0, bytes));
                break;
            case "presign":
                Interlocked.Increment(ref _presigns);
                break;
            case "head":
                Interlocked.Increment(ref _heads);
                break;
            case "delete":
                Interlocked.Increment(ref _deletes);
                break;
        }

        if (!success)
            Interlocked.Increment(ref _failures);

        var now = DateTime.UtcNow;
        lock (_sync)
        {
            _latencySamplesMs.Enqueue(elapsed.TotalMilliseconds);
            while (_latencySamplesMs.Count > MaxLatencySamples)
                _latencySamplesMs.Dequeue();

            if (success)
            {
                _lastSuccessAtUtc = now;
            }
            else
            {
                _lastFailureAtUtc = now;
                _lastError = Truncate(error);
            }
        }
    }

    public ObjectStorageTelemetrySnapshot Snapshot()
    {
        double p95;
        double max;
        lock (_sync)
        {
            if (_latencySamplesMs.Count == 0)
            {
                p95 = 0;
                max = 0;
            }
            else
            {
                var sorted = _latencySamplesMs.OrderBy(value => value).ToArray();
                max = sorted[^1];
                var index = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
                p95 = sorted[Math.Max(0, Math.Min(index, sorted.Length - 1))];
            }
        }

        var operations = Interlocked.Read(ref _operations);
        var failures = Interlocked.Read(ref _failures);

        return new ObjectStorageTelemetrySnapshot(
            SinceUtc: _sinceUtc,
            Uploads: Interlocked.Read(ref _uploads),
            Downloads: Interlocked.Read(ref _downloads),
            Presigns: Interlocked.Read(ref _presigns),
            Heads: Interlocked.Read(ref _heads),
            Deletes: Interlocked.Read(ref _deletes),
            BytesIn: Interlocked.Read(ref _bytesIn),
            BytesOut: Interlocked.Read(ref _bytesOut),
            Operations: operations,
            Failures: failures,
            ErrorRate: operations > 0 ? Math.Round(failures * 100d / operations, 2) : 0,
            P95LatencyMs: Math.Round(p95, 1),
            MaxLatencyMs: Math.Round(max, 1),
            InFlight: Volatile.Read(ref _inFlight),
            LastSuccessAtUtc: _lastSuccessAtUtc,
            LastFailureAtUtc: _lastFailureAtUtc,
            LastError: _lastError);
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Length <= MaxErrorLength ? value : value[..MaxErrorLength] + "...";
    }
}

/// <summary>
/// Decorator de IObjectStorageService que publica metricas passivas.
/// Nao altera o comportamento das operacoes: apenas mede e repassa.
/// </summary>
public sealed class InstrumentedObjectStorageProvider(
    Discovery.Core.Interfaces.IObjectStorageService inner,
    ObjectStorageTelemetry telemetry) : Discovery.Core.Interfaces.IObjectStorageService
{
    public Task<Discovery.Core.ValueObjects.StorageObject> UploadAsync(
        string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        => MeasureAsync("upload",
            () => inner.UploadAsync(objectKey, content, contentType, cancellationToken));

    public Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default)
        => MeasureAsync("download", () => inner.DownloadAsync(objectKey, cancellationToken));

    public Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default)
        => MeasureAsync("download", () => inner.DownloadToAsync(objectKey, destination, cancellationToken));

    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
        => MeasureAsync("head", () => inner.ExistsAsync(objectKey, cancellationToken));

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
        => MeasureAsync("delete", () => inner.DeleteAsync(objectKey, cancellationToken));

    public Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
        => MeasureAsync("delete", () => inner.DeleteByPrefixAsync(prefix, cancellationToken));

    public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int ttlHours, CancellationToken cancellationToken = default)
        => MeasureAsync("presign", () => inner.GetPresignedDownloadUrlAsync(objectKey, ttlHours, cancellationToken));

    public Task<string> GetPresignedUploadUrlAsync(string objectKey, int ttlMinutes, string contentType, CancellationToken cancellationToken = default)
        => MeasureAsync("presign", () => inner.GetPresignedUploadUrlAsync(objectKey, ttlMinutes, contentType, cancellationToken));

    public Task<Discovery.Core.ValueObjects.StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default)
        => MeasureAsync("head", () => inner.GetMetadataAsync(objectKey, cancellationToken));

    public Task<Discovery.Core.ValueObjects.ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
        => MeasureAsync("head", () => inner.TestConnectionAsync(cancellationToken));

    private async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action)
    {
        telemetry.OperationStarted();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await action().ConfigureAwait(false);
            sw.Stop();
            telemetry.Record(operation, ResolveBytes(result), sw.Elapsed, true);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            telemetry.Record(operation, 0, sw.Elapsed, false, ex.Message);
            throw;
        }
        finally
        {
            telemetry.OperationFinished();
        }
    }

    private async Task MeasureAsync(string operation, Func<Task> action)
    {
        telemetry.OperationStarted();
        var sw = Stopwatch.StartNew();
        try
        {
            await action().ConfigureAwait(false);
            sw.Stop();
            telemetry.Record(operation, 0, sw.Elapsed, true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            telemetry.Record(operation, 0, sw.Elapsed, false, ex.Message);
            throw;
        }
        finally
        {
            telemetry.OperationFinished();
        }
    }

    private static long ResolveBytes<T>(T result) => result switch
    {
        Discovery.Core.ValueObjects.StorageObject storage => storage.SizeBytes,
        Stream stream when stream.CanSeek => stream.Length,
        _ => 0
    };
}
