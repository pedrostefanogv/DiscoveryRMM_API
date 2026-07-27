using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Metadados de gravação de uma sessão remota.
/// </summary>
public class RemoteSessionRecording
{
    public Guid Id { get; set; }
    public Guid RemoteSessionId { get; set; }

    /// <summary>Provedor de storage utilizado.</summary>
    public RecordingStorageProvider StorageProvider { get; set; } = RecordingStorageProvider.Local;

    /// <summary>Status da gravação: recording, assembling, completed, failed.</summary>
    public string Status { get; set; } = "recording";

    /// <summary>Codec do stream original capturado.</summary>
    public RemoteCodec SourceCodec { get; set; } = RemoteCodec.Jpeg;

    /// <summary>Formato do container final: webm, mp4.</summary>
    public string ContainerFormat { get; set; } = "webm";

    /// <summary>URL de download (ou chave S3).</summary>
    public string? StorageUrl { get; set; }

    /// <summary>Tamanho total do arquivo em bytes.</summary>
    public long Bytes { get; set; }

    /// <summary>Duração total em segundos.</summary>
    public int DurationSec { get; set; }

    /// <summary>Resolução do vídeo (WxH).</summary>
    public string? Resolution { get; set; }

    /// <summary>FPS médio da gravação.</summary>
    public double? AverageFps { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Usuário que iniciou a gravação.</summary>
    public string? StartedBy { get; set; }

    /// <summary>Data de expiração para auto-delete.</summary>
    public DateTime? RetentionExpiresAt { get; set; }

    public RemoteSession RemoteSession { get; set; } = null!;
}
