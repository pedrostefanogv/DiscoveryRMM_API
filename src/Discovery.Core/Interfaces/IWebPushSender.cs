namespace Discovery.Core.Interfaces;

/// <summary>Mensagem exibida pelo Service Worker na notificacao do sistema operacional.</summary>
public sealed record WebPushMessage(
    string Title,
    string Body,
    string? Severity = null,
    string? Topic = null,
    string? EventType = null,
    Guid? NotificationId = null,
    string? Url = null);

/// <summary>Resultado do envio: quantas inscricoes foram tentadas/entregues/removidas.</summary>
public sealed record WebPushDispatchResult(int Attempted, int Delivered, int Failed, int Removed)
{
    public static readonly WebPushDispatchResult Empty = new(0, 0, 0, 0);
}

/// <summary>Envia Web Push para todas as inscricoes de um usuario.</summary>
public interface IWebPushSender
{
    /// <summary>Servidor pronto para enviar (habilitado e com par VAPID resolvido).</summary>
    bool IsConfigured { get; }

    /// <summary>Chave publica VAPID em base64url (string vazia quando nao configurado).</summary>
    string PublicKey { get; }

    Task<WebPushDispatchResult> SendToUserAsync(Guid userId, WebPushMessage message, CancellationToken ct = default);
}
