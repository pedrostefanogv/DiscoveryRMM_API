namespace Discovery.Core.Interfaces;

/// <summary>Trabalho de Web Push desacoplado do request que publicou a notificacao.</summary>
public sealed record WebPushDispatchJob(Guid UserId, WebPushMessage Message);

/// <summary>
/// Fila em memoria para envio de Web Push. Existe para que
/// <c>INotificationService.PublishAsync</c> NAO espere o provedor de push:
/// sem isso, um provedor lento (HttpClient padrao = 100s) bloquearia a
/// criacao de chamados e os jobs de SLA.
///
/// Best-effort: se a fila encher, o job e descartado (a notificacao continua
/// persistida e visivel no sino).
/// </summary>
public interface IWebPushDispatchQueue
{
    ValueTask EnqueueAsync(Guid userId, WebPushMessage message, CancellationToken cancellationToken = default);
}
