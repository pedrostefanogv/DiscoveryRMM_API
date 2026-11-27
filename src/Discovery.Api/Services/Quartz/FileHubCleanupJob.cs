using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Job periódico de limpeza da Central de Arquivos S3:
/// 1. Remove arquivos com <c>expires_at_utc</c> vencido (S3 + soft delete).
/// 2. Aborta sessões multipart abandonadas (&gt; 24h) nos prefixos <c>file-hub/</c> e <c>client-uploads/</c>.
/// </summary>
[DisallowConcurrentExecution]
public sealed class FileHubCleanupJob(
    IServiceScopeFactory scopeFactory,
    ILogger<FileHubCleanupJob> logger) : IJob
{
    public static readonly JobKey Key = new("FileHubCleanupJob", "Maintenance");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken != default ? cancellationToken : context.CancellationToken;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var storageFactory = scope.ServiceProvider.GetRequiredService<IObjectStorageProviderFactory>();

        var now = DateTime.UtcNow;
        var expiredFiles = await db.ManagedFiles
            .Where(f => f.DeletedAt == null && f.ExpiresAtUtc != null && f.ExpiresAtUtc <= now)
            .Take(200)
            .ToListAsync(ct);

        IObjectStorageService? storage = null;
        try
        {
            storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FileHubCleanupJob: object storage indisponível.");
        }

        if (expiredFiles.Count > 0)
        {
            foreach (var file in expiredFiles)
            {
                if (storage is not null)
                {
                    try { await storage.DeleteAsync(file.StorageObjectKey, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Falha ao remover arquivo expirado {ObjectKey} do S3.", file.StorageObjectKey);
                    }
                }
                file.DeletedAt = now;
                file.IsActive = false;
                file.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("FileHubCleanupJob removeu {Count} arquivo(s) expirado(s).", expiredFiles.Count);
        }

        if (storage is not null)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            foreach (var prefix in new[] { "file-hub/", "client-uploads/" })
            {
                try
                {
                    var sessions = await storage.ListMultipartUploadSessionsAsync(prefix, 500, ct);
                    foreach (var s in sessions.Where(x => x.InitiatedUtc <= cutoff))
                    {
                        await storage.AbortMultipartUploadAsync(s.ObjectKey, s.UploadId, ct);
                    }
                }
                catch (NotSupportedException) { break; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "FileHubCleanupJob: falha ao limpar multiparts em {Prefix}.", prefix);
                }
            }
        }
    }
}
