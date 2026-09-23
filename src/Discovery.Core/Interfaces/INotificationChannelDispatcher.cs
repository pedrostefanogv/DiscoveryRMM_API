namespace Discovery.Core.Interfaces;

/// <summary>Despacha notificações para canais externos (webhook/e-mail).</summary>
public interface INotificationChannelDispatcher
{
    Task DispatchAsync(NotificationPublishRequest request, CancellationToken ct = default);
}
