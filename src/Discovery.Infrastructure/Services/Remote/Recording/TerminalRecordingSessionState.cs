using Discovery.Core.Enums;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Estado em memória de uma gravação de terminal ativa. O acesso ao writer,
/// ao rate limit e aos contadores é serializado por <see cref="Gate"/>.
/// </summary>
public sealed class TerminalRecordingSessionState
{
    public TerminalRecordingSessionState(
        Guid recordingId,
        Guid sessionId,
        Guid agentId,
        Guid clientId,
        DateTime startedAtUtc,
        string fullPath,
        TerminalRecordingNdjsonWriter writer,
        TerminalRecordingRateLimiter rateLimiter,
        TerminalRecordingMetrics metrics)
    {
        RecordingId = recordingId;
        SessionId = sessionId;
        AgentId = agentId;
        ClientId = clientId;
        StartedAtUtc = startedAtUtc;
        FullPath = fullPath;
        Writer = writer;
        RateLimiter = rateLimiter;
        Metrics = metrics;
    }

    public Guid RecordingId { get; }
    public Guid SessionId { get; }
    public Guid AgentId { get; }
    public Guid ClientId { get; }
    public DateTime StartedAtUtc { get; }
    public string FullPath { get; }
    public TerminalRecordingNdjsonWriter Writer { get; }
    public TerminalRecordingRateLimiter RateLimiter { get; }
    public TerminalRecordingMetrics Metrics { get; }
    public TerminalRecordingFinalizeGuard FinalizeGuard { get; } = new();
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public bool Dirty { get; set; }

    /// <summary>
    /// Storage da gravação. Sempre Local durante a gravação (o writer escreve em
    /// disco) e só vira S3 quando o upload do finalize conclui com sucesso.
    /// </summary>
    public RecordingStorageProvider StorageProvider { get; set; } = RecordingStorageProvider.Local;
    public string? LastError { get; set; }
    public string? LastRateLimitReason { get; set; }

    private long _pendingDroppedFrames;

    /// <summary>
    /// Contador atômico para descartes que chegam fora do gate (fila cheia,
    /// parse inválido). Evita bloquear o loop do NATS esperando o writer.
    /// </summary>
    public void IncrementDroppedFrames() => Interlocked.Increment(ref _pendingDroppedFrames);

    public long DrainDroppedFrames() => Interlocked.Exchange(ref _pendingDroppedFrames, 0);
}
