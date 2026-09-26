using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Quartz job dedicado aos embeddings das respostas do questionário (busca
/// semântica em chamados). Segue o padrão dos jobs de sincronização do catálogo
/// Winget: periodicidade configurável via appsettings, atraso de startup para
/// catch-up pós restart/deploy e <see cref="DisallowConcurrentExecutionAttribute"/>.
///
/// Configuração (appsettings):
/// - BackgroundJobs:TicketAnswerEmbedding:Enabled (default: true)
/// - BackgroundJobs:TicketAnswerEmbedding:IntervalSeconds (default: 60, piso 15)
/// - BackgroundJobs:TicketAnswerEmbedding:StartupDelaySeconds (default: 20)
///
/// O trabalho real só acontece quando a IA está com
/// <c>AIIntegrationSettings.EmbeddingTicketAnswersEnabled</c> ligado; caso
/// contrário o ciclo é inerte (uma consulta indexada, sem chamada ao provedor).
///
/// A DIMENSÃO do vetor é responsabilidade do job da KB (AutoSync + reset): este
/// job apenas gera/grava embeddings e não dispara ALTER TABLE.
/// </summary>
[DisallowConcurrentExecution]
public sealed class TicketAnswerEmbeddingJob : IJob
{
    public static readonly JobKey Key = new("ticket-answer-embedding", "tickets");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var config = context.GetScopedService<IConfiguration>();
        var logger = context.GetLogger<TicketAnswerEmbeddingJob>();
        var ct = context.CancellationToken;

        if (!(config.GetValue<bool?>("BackgroundJobs:TicketAnswerEmbedding:Enabled") ?? true))
        {
            logger.LogDebug("TicketAnswerEmbedding desabilitado por configuração.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IConfigurationResolver>();
        var processor = scope.ServiceProvider.GetRequiredService<ITicketAnswerEmbeddingProcessor>();

        var settings = await resolver.GetAISettingsAsync();
        if (!settings.EmbeddingEnabled || !settings.EmbeddingTicketAnswersEnabled)
        {
            logger.LogDebug("Embeddings das respostas desativados nas configurações de IA.");
            return;
        }

        try
        {
            var processed = await processor.ProcessAsync(settings, ct);
            context.Result = processed;

            if (processed > 0)
                logger.LogInformation("Embeddings das respostas do questionário: {Count} processadas.", processed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao processar embeddings das respostas do questionário.");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }
}
