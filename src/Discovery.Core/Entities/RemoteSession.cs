using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Sessão de acesso remoto nativo a um agent (screen, terminal, files, proxy).
/// Substitui TicketRemoteSession (MeshCentral).
/// </summary>
public class RemoteSession
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>Tipo de sessão remota.</summary>
    public RemoteSessionKind Kind { get; set; } = RemoteSessionKind.Screen;

    /// <summary>Transporte em uso (Webrtc, Nats, Http).</summary>
    public RemoteTransport Transport { get; set; } = RemoteTransport.Webrtc;

    /// <summary>Perfil de qualidade do stream.</summary>
    public QualityProfile QualityProfile { get; set; } = QualityProfile.High;

    /// <summary>Codec de compressão preferencial.</summary>
    public RemoteCodec Codec { get; set; } = RemoteCodec.Jpeg;

    /// <summary>Status atual da sessão.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Subject NATS base para stream bidirecional.</summary>
    public string? NatsSubject { get; set; }

    /// <summary>Session ID do WebRTC (Pion peer connection).</summary>
    public string? WebrtcSessionId { get; set; }

    /// <summary>Se gravação está habilitada nesta sessão.</summary>
    public bool RecordingEnabled { get; set; }

    /// <summary>ID da gravação associada, se existir.</summary>
    public Guid? RecordingId { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    /// <summary>Duração total em segundos (preenchido ao encerrar).</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Total de frames enviados.</summary>
    public long FramesSent { get; set; }

    /// <summary>Total de bytes enviados.</summary>
    public long BytesSent { get; set; }

    /// <summary>Nota livre sobre o que foi feito na sessão.</summary>
    public string? Note { get; set; }

    public Agent Agent { get; set; } = null!;
    public RemoteSessionRecording? Recording { get; set; }
    public ICollection<RemoteSessionAudit> Audits { get; set; } = [];
}
