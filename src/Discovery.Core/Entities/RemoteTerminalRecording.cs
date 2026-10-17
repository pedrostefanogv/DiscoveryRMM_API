using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Metadados da gravação incremental do terminal de uma sessão remota.
/// O conteúdo em si fica em NDJSON no storage local (ver storage_url); esta
/// linha guarda os contadores agregados, o status e a retenção.
/// </summary>
public class RemoteTerminalRecording
{
    public Guid Id { get; set; }

    /// <summary>Sessão remota dona da gravação (1:1).</summary>
    public Guid RemoteSessionId { get; set; }

    public Guid AgentId { get; set; }

    /// <summary>Cliente (tenant) extraído do subject NATS no momento da criação.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Status: recording, completed ou failed.</summary>
    public string Status { get; set; } = RemoteTerminalRecordingStatus.Recording;

    public DateTime StartedAt { get; set; }

    /// <summary>Timestamp do último frame aceito (não conta frames descartados).</summary>
    public DateTime? LastFrameAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Total de frames persistidos no NDJSON.</summary>
    public long Frames { get; set; }

    /// <summary>Total de bytes persistidos no NDJSON.</summary>
    public long Bytes { get; set; }

    /// <summary>Frames descartados por rate limit, fila cheia ou erro de parse/DB.</summary>
    public long DroppedFrames { get; set; }

    /// <summary>Maior taxa de frames observada em uma janela de 1 segundo.</summary>
    public int MaxFps { get; set; }

    public RecordingStorageProvider StorageProvider { get; set; } = RecordingStorageProvider.Local;

    /// <summary>Caminho relativo do arquivo NDJSON dentro do diretório base de gravações de terminal.</summary>
    public string? StorageUrl { get; set; }

    /// <summary>Data em que a gravação pode ser removida pela retenção.</summary>
    public DateTime? RetentionExpiresAt { get; set; }

    /// <summary>Último erro relevante (parse, IO, finalização).</summary>
    public string? LastError { get; set; }

    public RemoteSession RemoteSession { get; set; } = null!;
}

/// <summary>Valores canônicos do campo <see cref="RemoteTerminalRecording.Status"/>.</summary>
public static class RemoteTerminalRecordingStatus
{
    public const string Recording = "recording";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
