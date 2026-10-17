using Discovery.Core.Configuration;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>Motivo do descarte de um frame pelo rate limit.</summary>
public static class TerminalRecordingRateLimitReasons
{
    public const string FramesPerSecond = "frames_per_second";
    public const string BytesPerSecond = "bytes_per_second";
    public const string RecordingBytes = "recording_bytes";
}

/// <summary>Decisão do rate limit para um frame.</summary>
public readonly record struct TerminalRecordingRateLimitDecision(bool Accepted, string? Reason)
{
    public static TerminalRecordingRateLimitDecision Accept() => new(true, null);
    public static TerminalRecordingRateLimitDecision Reject(string reason) => new(false, reason);
}

/// <summary>
/// Rate limit por sessão com janela deslizante de 1 segundo para FPS e bytes,
/// mais o teto absoluto da gravação. O excedente é descartado (nunca lança).
/// O relógio é injetável para testes determinísticos.
/// </summary>
public sealed class TerminalRecordingRateLimiter
{
    private readonly RemoteAccessTerminalRecordingOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<(DateTimeOffset At, long Bytes)> _window = new();
    private long _totalBytes;

    public TerminalRecordingRateLimiter(
        RemoteAccessTerminalRecordingOptions options,
        Func<DateTimeOffset>? clock = null)
    {
        _options = options;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public long TotalBytes => _totalBytes;

    /// <summary>
    /// Restaura o total já persistido (retomada de gravação após restart) para
    /// que o teto MaxRecordingBytes continue valendo.
    /// </summary>
    public void RestoreTotalBytes(long bytes)
    {
        if (bytes > 0)
            _totalBytes = bytes;
    }

    /// <param name="payloadBytes">Tamanho que será efetivamente gravado.</param>
    /// <param name="ignoreWindowLimits">
    /// Ignora FPS/bytes por segundo (mas não o teto total). Usado pelos frames de
    /// controle (exit/error): descartá-los deixaria a gravação aberta para sempre,
    /// porque é o exit que a encerra.
    /// </param>
    public TerminalRecordingRateLimitDecision TryAcquire(long payloadBytes, bool ignoreWindowLimits = false)
    {
        if (payloadBytes < 0)
            payloadBytes = 0;

        var now = _clock();
        var windowStart = now.AddSeconds(-1);
        while (_window.Count > 0 && _window.Peek().At <= windowStart)
            _window.Dequeue();

        if (!ignoreWindowLimits)
        {
            var maxFps = _options.MaxFramesPerSecond;
            if (maxFps > 0 && _window.Count >= maxFps)
                return TerminalRecordingRateLimitDecision.Reject(TerminalRecordingRateLimitReasons.FramesPerSecond);

            var maxBytesPerSecond = _options.MaxBytesPerSecond;
            if (maxBytesPerSecond > 0)
            {
                long windowBytes = 0;
                foreach (var entry in _window)
                    windowBytes += entry.Bytes;

                if (windowBytes + payloadBytes > maxBytesPerSecond)
                    return TerminalRecordingRateLimitDecision.Reject(TerminalRecordingRateLimitReasons.BytesPerSecond);
            }
        }

        var maxRecordingBytes = _options.MaxRecordingBytes;
        if (maxRecordingBytes > 0 && _totalBytes + payloadBytes > maxRecordingBytes)
            return TerminalRecordingRateLimitDecision.Reject(TerminalRecordingRateLimitReasons.RecordingBytes);

        _window.Enqueue((now, payloadBytes));
        _totalBytes += payloadBytes;
        return TerminalRecordingRateLimitDecision.Accept();
    }
}
