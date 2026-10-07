using System.Threading.Channels;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Drena a fila de Web Push em background, resolvendo <see cref="IWebPushSender"/>
/// em um scope proprio (o sender e scoped, pois depende do DbContext).
///
/// Singleton + hosted service, no mesmo padrao de SyncPingDispatchBackgroundService.
/// O canal e limitado com descarte: enfileirar nunca bloqueia o request de origem.
/// </summary>
public class WebPushDispatchBackgroundService : BackgroundService, IWebPushDispatchQueue
{
    private const int QueueCapacity = 4096;

    private readonly Channel<WebPushDispatchJob> _queue =
        Channel.CreateBounded<WebPushDispatchJob>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebPushDispatchBackgroundService> _logger;

    public WebPushDispatchBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<WebPushDispatchBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(Guid userId, WebPushMessage message, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return ValueTask.CompletedTask;

        // TryWrite nao bloqueia: com DropWrite ele so falha se o canal estiver cheio
        // ou encerrado. Nao propaga cancelamento do request para nao perder a entrega.
        if (!_queue.Writer.TryWrite(new WebPushDispatchJob(userId, message)))
        {
            _logger.LogWarning(
                "Fila de Web Push cheia ou encerrada; notificacao nao enviada ao navegador. UserId={UserId}",
                userId);
        }

        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await DispatchAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal do host.
        }
    }

    private async Task DispatchAsync(WebPushDispatchJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<IWebPushSender>();
            await sender.SendToUserAsync(job.UserId, job.Message, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: nao loga como falha.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao despachar Web Push em background. UserId={UserId}", job.UserId);
        }
    }
}
