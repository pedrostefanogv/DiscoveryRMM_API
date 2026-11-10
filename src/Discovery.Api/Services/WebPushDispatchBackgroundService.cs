using System.Threading.Channels;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Services;

/// <summary>
/// Drena a fila de Web Push em background, resolvendo <see cref="IWebPushSender"/>
/// em um scope proprio (o sender e scoped, pois depende do DbContext).
///
/// Singleton + hosted service, no mesmo padrao de SyncPingDispatchBackgroundService.
/// O canal e limitado com descarte: enfileirar nunca bloqueia o request de origem.
///
/// CFG-A — DUAS camadas de kill switch para o MESMO subsistema, de proposito:
///   1. BOOT: <c>BackgroundJobs:WebPushDispatch:Enabled</c> (appsettings) decide se
///      o WORKER e registrado (ver BackgroundServicesCollectionExtensions). A fila
///      em si e sempre registrada porque NotificationService depende dela.
///      Desligado no boot, este laco nem roda.
///   2. RUNTIME: <c>PushEnabled</c> na configuracao do servidor (editavel na UI) e
///      conferido a cada job em <see cref="DispatchAsync"/>. Efeito IMEDIATO, sem
///      restart, e respeita a heranca (servidor -> cliente -> site) do item.
/// Desligar a camada 1 torna a camada 2 inalcancavel; ligar a 1 com a 2 desligada
/// mantem o worker vivo mas sem entregar nada.
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

    /// <summary>
    /// Le <c>PushEnabled</c> da configuracao do servidor (fonte de verdade).
    ///
    /// Le direto do DbContext em vez de depender do ConfigurationResolver: o
    /// worker e singleton e resolve tudo dentro de um scope por job, entao a
    /// consulta aqui e barata (linha singleton, AsNoTracking) e nao cria
    /// dependencia captive. O servidor e o unico nivel que faz sentido para um
    /// dispatcher GLOBAL (a fila nao carrega client/site do destinatario).
    /// Qualquer falha de leitura cai para TRUE — nunca silencia o push por erro.
    /// </summary>
    private static async Task<bool> IsPushEnabledAsync(IServiceProvider scopedProvider, CancellationToken ct)
    {
        try
        {
            var db = scopedProvider.GetService<DiscoveryDbContext>();
            if (db is null)
                return true;

            var value = await db.ServerConfigurations
                .AsNoTracking()
                .Select(config => (bool?)config.PushEnabled)
                .FirstOrDefaultAsync(ct);

            return value ?? true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fail-safe na direcao do comportamento atual: entrega o push.
            return true;
        }
    }

    private async Task DispatchAsync(WebPushDispatchJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();

            // CFG-A: kill switch de RUNTIME (PushEnabled na configuracao do
            // servidor, editavel na UI). Antes a chave Push:Enabled existia no
            // appsettings e NAO era lida por ninguem: desligar o push pela UI nao
            // tinha efeito nenhum. Default TRUE preserva o comportamento atual.
            if (!await IsPushEnabledAsync(scope.ServiceProvider, stoppingToken))
            {
                _logger.LogDebug(
                    "Web Push desativado por configuracao (pushEnabled=false); notificacao descartada. UserId={UserId}",
                    job.UserId);
                return;
            }

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
