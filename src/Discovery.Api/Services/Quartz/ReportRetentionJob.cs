using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;
using Quartz;
using System.Text.Json;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job that purges old report executions and their generated files.
/// Replaces ReportRetentionBackgroundService.
/// Schedule: daily at 4 AM (0 0 4 * * ?)
/// </summary>
[DisallowConcurrentExecution]
public sealed class ReportRetentionJob : IJob
{
    public static readonly JobKey Key = new("report-retention", "maintenance");
    private const int DefaultRetentionDays = 90;
    private static readonly int[] DefaultAllowedDays = [30, 60, 90];

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var optionsMonitor = context.GetScopedService<IOptionsMonitor<ReportingOptions>>();
        var logger = context.GetLogger<ReportRetentionJob>();
        var ct = context.CancellationToken;

        var fallback = optionsMonitor.CurrentValue;
        await using var scope = scopeFactory.CreateAsyncScope();

        var serverRepo = scope.ServiceProvider.GetRequiredService<IServerConfigurationRepository>();
        var server = await serverRepo.GetOrCreateDefaultAsync();
        var options = ResolveEffectiveOptions(server.ReportingSettingsJson, fallback);
        var dbRetentionDays = ValidateRetentionDays(options.DatabaseRetentionDays, options.AllowedRetentionDays);
        var fileRetentionDays = ValidateRetentionDays(options.FileRetentionDays, options.AllowedRetentionDays);

        var dbCutoff = DateTime.UtcNow.AddDays(-dbRetentionDays);
        var fileCutoff = DateTime.UtcNow.AddDays(-fileRetentionDays);

        var reportRepo = scope.ServiceProvider.GetRequiredService<IReportExecutionRepository>();
        var templateRepo = scope.ServiceProvider.GetRequiredService<IReportTemplateRepository>();
        var storageFactory = scope.ServiceProvider.GetRequiredService<IObjectStorageProviderFactory>();
        var storageValidationErrors = await storageFactory.ValidateConfigurationAsync();
        if (storageValidationErrors.Count > 0)
        {
            logger.LogWarning("Report retention skipped: object storage misconfigured. {Errors}", storageValidationErrors);
            return;
        }

        // Busca UMA vez, com o menor corte, e so depois remove do banco.
        // Antes as linhas eram apagadas primeiro e a segunda consulta (por
        // fileCutoff) voltava vazia quando os dois prazos eram iguais — os
        // arquivos nunca eram removidos do storage (objetos orfaos para sempre).
        var candidateCutoff = fileCutoff <= dbCutoff ? fileCutoff : dbCutoff;
        var candidates = await reportRepo.GetExpiredAsync(candidateCutoff, 1000);
        var storage = storageFactory.CreateObjectStorageService();

        var filesDeleted = 0;
        var filesFailed = 0;
        var idsToDelete = new List<Guid>(candidates.Count);

        foreach (var execution in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var rowExpired = execution.CreatedAt <= dbCutoff;
            var fileExpired = execution.CreatedAt <= fileCutoff;
            var fileRemoved = !fileExpired || string.IsNullOrWhiteSpace(execution.StorageObjectKey);

            if (fileExpired && !string.IsNullOrWhiteSpace(execution.StorageObjectKey))
            {
                try
                {
                    await storage.DeleteAsync(execution.StorageObjectKey, ct);
                    filesDeleted++;
                    fileRemoved = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete report file {Key}", execution.StorageObjectKey);
                    filesFailed++;
                }
            }

            // Mantem a linha quando o arquivo ainda nao foi removido para a
            // proxima execucao tentar de novo (evita orfao sem rastro).
            if (rowExpired && fileRemoved)
                idsToDelete.Add(execution.Id);
        }

        var dbDeleted = idsToDelete.Count > 0 ? await reportRepo.DeleteByIdsAsync(idsToDelete) : 0;

        // Historico de template tambem e limpo: sem isso a tabela cresce para sempre.
        var historyRetentionDays = options.TemplateHistoryRetentionDays > 0 ? options.TemplateHistoryRetentionDays : 365;
        var historyCutoff = DateTime.UtcNow.AddDays(-historyRetentionDays);
        var historyDeleted = await templateRepo.DeleteHistoryOlderThanAsync(historyCutoff);

        logger.LogInformation(
            "Report retention: purged {Count} DB records (>={DbDays}d), deleted {FileCount} files (>={FileDays}d), {Failed} failures, purged {HistoryCount} template history rows (>={HistoryDays}d).",
            dbDeleted, dbRetentionDays, filesDeleted, fileRetentionDays, filesFailed, historyDeleted, historyRetentionDays);
        context.Result = new { dbDeleted, filesDeleted, filesFailed, historyDeleted };
    }

    private static ReportingOptions ResolveEffectiveOptions(string? settingsJson, ReportingOptions fallback)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return fallback;

        try
        {
            var server = JsonSerializer.Deserialize<ReportingOptions>(settingsJson);
            return server ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static int ValidateRetentionDays(int value, int[]? allowedValues)
    {
        var allowed = allowedValues is { Length: > 0 } ? allowedValues : DefaultAllowedDays;
        return allowed.Contains(value) ? value : DefaultRetentionDays;
    }
}
