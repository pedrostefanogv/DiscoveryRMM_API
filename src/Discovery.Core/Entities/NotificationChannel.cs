using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>Canal externo de notificação (webhook ou e-mail) por tipo de evento.</summary>
public class NotificationChannel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public NotificationChannelType Type { get; set; } = NotificationChannelType.Webhook;
    public bool IsActive { get; set; } = true;
    /// <summary>Lista JSON de eventTypes; ["*"] significa todos.</summary>
    public string EventsJson { get; set; } = "[]";
    /// <summary>Configuração do canal em JSON (url/secret para webhook; smtp para e-mail).</summary>
    public string ConfigJson { get; set; } = "{}";
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
