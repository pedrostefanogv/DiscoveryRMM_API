using Discovery.Api.Services.BackgroundServices;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Limpeza periódica dos uploads multipart DIRETOS de assets da loja.
///
/// Dois vazamentos tratados:
/// 1. Multipart órfão no storage: um envio de vários GiB abandonado (aba fechada,
///    usuário desistiu) deixa as partes ocupando espaço para sempre — o TTL do
///    Redis libera só o JSON da sessão, não as partes. O índice (TTL 26h) guarda
///    a object key justamente para que este serviço aborte o multipart quando a
///    sessão (24h) já expirou.
/// 2. Temporários do caminho antigo de blocos (upload via API) em
///    %TEMP%/discovery-asset-uploads: o dicionário em memória se perde no restart
///    da API e os diretórios ficavam para sempre.
/// </summary>
public sealed class MultipartUploadJanitorService(
    IServiceScopeFactory scopeFactory,
    IRedisService redis,
    BackgroundServiceRegistry registry,
    ILogger<MultipartUploadJanitorService> logger) : BackgroundService
{
    private const string ServiceName = "MultipartUploadJanitor";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    /// <summary>Idade mínima para considerar um temporário do caminho antigo como lixo.</summary>
    private static readonly TimeSpan LegacyTempRetention = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        registry.MarkStarted(ServiceName);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            registry.MarkStopped(ServiceName);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
                registry.Heartbeat(ServiceName);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                registry.MarkStopped(ServiceName);
                return;
            }
            catch (Exception ex)
            {
                // Nunca derruba o host por causa da limpeza.
                logger.LogWarning(ex, "Falha na limpeza de uploads multipart orfaos.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Abre um escopo por varredura (o serviço de assets é scoped).</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var assets = scope.ServiceProvider.GetRequiredService<ICustomPackageAssetService>();
        var packages = scope.ServiceProvider.GetRequiredService<IAppPackageRepository>();

        var aborted = await AbortOrphanMultipartsAsync(assets, cancellationToken);
        var discarded = await DiscardUnlinkedAssetsAsync(assets, packages, cancellationToken);
        var removedDirectories = PruneLegacyTempDirectories();

        if (aborted > 0 || discarded > 0 || removedDirectories > 0)
        {
            logger.LogInformation(
                "Limpeza de uploads: {Aborted} multipart(s) orfao(s) abortado(s), {Discarded} asset(s) nao vinculado(s) descartado(s), {Directories} diretorio(s) temporario(s) removido(s).",
                aborted, discarded, removedDirectories);
        }
    }

    /// <summary>
    /// Descarta os objetos PUBLICADOS que nunca foram vinculados a um pacote (o
    /// operador trocou o arquivo antes de salvar, fechou a aba etc.). Só depois de
    /// <see cref="MultipartUploadSessionStore.UnlinkedAssetTtl"/> e apenas se
    /// nenhum pacote referenciar a object key.
    /// </summary>
    private async Task<int> DiscardUnlinkedAssetsAsync(
        ICustomPackageAssetService assets,
        IAppPackageRepository packages,
        CancellationToken cancellationToken)
    {
        var indexKeys = await redis.GetKeysByPrefixAsync(MultipartUploadSessionStore.IndexKeyPrefix);
        if (indexKeys.Count == 0)
            return 0;

        var discarded = 0;
        foreach (var indexKey in indexKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = MultipartUploadSessionStore.ReadIndexEntry(await redis.GetAsync(indexKey));
            if (entry is null || string.IsNullOrWhiteSpace(entry.ObjectKey) || !entry.Completed)
                continue;

            var completedAt = entry.CompletedAtUtc ?? entry.CreatedAtUtc;
            if (completedAt != default
                && DateTimeOffset.UtcNow - completedAt < MultipartUploadSessionStore.UnlinkedAssetTtl)
            {
                continue;
            }

            if (await packages.IsAssetObjectKeyReferencedAsync(entry.ObjectKey, cancellationToken))
            {
                // Já vinculado ao pacote: o índice perdeu a utilidade.
                await redis.DeleteAsync(indexKey);
                continue;
            }

            try
            {
                await assets.DeleteAsync(entry.ObjectKey, cancellationToken);
                discarded++;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Nao foi possivel descartar o asset nao vinculado {ObjectKey}.", entry.ObjectKey);
                continue;
            }

            await redis.DeleteAsync(indexKey);
        }

        return discarded;
    }

    private async Task<int> AbortOrphanMultipartsAsync(
        ICustomPackageAssetService assets,
        CancellationToken cancellationToken)
    {
        var indexKeys = await redis.GetKeysByPrefixAsync(MultipartUploadSessionStore.IndexKeyPrefix);
        if (indexKeys.Count == 0)
            return 0;

        var aborted = 0;
        foreach (var indexKey in indexKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var uploadId = MultipartUploadSessionStore.ExtractUploadIdFromIndexKey(indexKey);
            if (string.IsNullOrWhiteSpace(uploadId))
                continue;

            // Sessão viva = upload em andamento: não mexer (e limpa o marcador,
            // caso a sessão tenha voltado a existir).
            var session = await MultipartUploadSessionStore.LoadAsync(redis, uploadId);
            if (session is not null)
            {
                await redis.DeleteAsync(MultipartUploadSessionStore.AbortPendingKey(uploadId));
                continue;
            }

            var entry = MultipartUploadSessionStore.ReadIndexEntry(await redis.GetAsync(indexKey));

            if (entry is null || string.IsNullOrWhiteSpace(entry.ObjectKey))
            {
                // Índice corrompido/sem object key: nada a abortar, só limpa.
                await redis.DeleteAsync(indexKey);
                continue;
            }

            // Concluído = não existe mais multipart no storage (quem cuida desse
            // caso é o descarte de assets não vinculados).
            if (entry.Completed)
                continue;

            // Só considera órfão o que passou do TTL da sessão: uma falha do Redis
            // no create (sessão não gravada, índice gravado) não pode abortar um
            // upload em andamento.
            if (entry.CreatedAtUtc != default
                && DateTimeOffset.UtcNow - entry.CreatedAtUtc < MultipartUploadSessionStore.SessionTtl)
            {
                continue;
            }

            // Confirmação em DUAS varreduras (ver AbortPendingKeyPrefix).
            var pendingKey = MultipartUploadSessionStore.AbortPendingKey(uploadId);
            var alreadyPending = !string.IsNullOrWhiteSpace(await redis.GetAsync(pendingKey));
            if (!alreadyPending)
            {
                await redis.SetAsync(pendingKey, entry.ObjectKey, (int)MultipartUploadSessionStore.AbortPendingTtl.TotalSeconds);
                continue;
            }

            try
            {
                await assets.AbortMultipartUploadAsync(entry.ObjectKey, entry.UploadId, cancellationToken);
                aborted++;
            }
            catch (Exception ex)
            {
                // Storage indisponível agora: mantém índice E marcador para a
                // próxima varredura — apagar aqui perderia a única pista do órfão.
                logger.LogDebug(ex, "Nao foi possivel abortar o multipart {UploadId}; nova tentativa na proxima varredura.", uploadId);
                continue;
            }

            await redis.DeleteAsync(pendingKey);
            await redis.DeleteAsync(indexKey);
        }

        return aborted;
    }

    private int PruneLegacyTempDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "discovery-asset-uploads");
        if (!Directory.Exists(root))
            return 0;

        var removed = 0;
        var cutoff = DateTime.UtcNow - LegacyTempRetention;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) > cutoff)
                    continue;

                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Nao foi possivel remover o temporario {Directory}.", directory);
            }
        }

        return removed;
    }
}
