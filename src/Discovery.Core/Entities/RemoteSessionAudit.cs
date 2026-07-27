namespace Discovery.Core.Entities;

/// <summary>
/// Registro de auditoria de uma sessão remota (início, fim, renovação, erro, gravação).
/// </summary>
public class RemoteSessionAudit
{
    public Guid Id { get; set; }
    public Guid RemoteSessionId { get; set; }

    /// <summary>Tipo de evento: started, closed, expired, renewed, recording_started, recording_stopped, error.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Usuário que disparou o evento (username ou userId).</summary>
    public string? ActorUserId { get; set; }

    /// <summary>Detalhes do evento em JSON.</summary>
    public string? Details { get; set; }

    /// <summary>IP de origem do evento.</summary>
    public string? IpAddress { get; set; }

    public DateTime OccurredAt { get; set; }

    public RemoteSession RemoteSession { get; set; } = null!;
}
