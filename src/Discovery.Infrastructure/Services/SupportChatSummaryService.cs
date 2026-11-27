using System.Text;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Gera um resumo estruturado por IA ao encerrar uma sessão de chat vinculada a chamado,
/// registrando o resultado na timeline do chamado.
/// </summary>
public sealed class SupportChatSummaryService(
    IServiceScopeFactory scopeFactory,
    ILogger<SupportChatSummaryService> logger) : ISupportChatSummaryService
{
    private const int MinNonSystemMessages = 2;
    private const int MaxTranscriptMessages = 80;
    private const int MaxTranscriptChars = 12_000;
    private const int SummaryMaxTokens = 450;
    private const double SummaryTemperature = 0.2;
    private const int SummaryTimeoutMs = 20_000;

    public void ScheduleSessionSummary(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await GenerateAndRecordSummaryAsync(sessionId, cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha não tratada no resumo automático de IA para a sessão {SessionId}", sessionId);
            }
        });
    }

    public async Task GenerateAndRecordSummaryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var chatRepository = scope.ServiceProvider.GetRequiredService<ITicketChatRepository>();
        var configurationResolver = scope.ServiceProvider.GetRequiredService<IConfigurationResolver>();
        var llmProvider = scope.ServiceProvider.GetRequiredService<ILlmProvider>();
        var activityLog = scope.ServiceProvider.GetRequiredService<IActivityLogService>();

        var session = await chatRepository.GetByIdAsync(sessionId, cancellationToken);
        if (session is null || session.TicketId is not { } ticketId)
            return;

        var messages = await chatRepository.GetMessagesAsync(sessionId, afterSequence: 0, limit: MaxTranscriptMessages, cancellationToken);
        var conversation = messages
            .Where(m => m.AuthorKind != TicketChatAuthorKind.System && !string.IsNullOrWhiteSpace(m.Body))
            .ToList();

        if (conversation.Count < MinNonSystemMessages)
            return;

        var settings = await configurationResolver.GetAISettingsAsync();
        if (settings is null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            logger.LogDebug("IA não configurada/habilitada; resumo automático do chat {SessionId} ignorado.", sessionId);
            return;
        }

        var transcript = BuildTranscript(conversation);
        if (string.IsNullOrWhiteSpace(transcript))
            return;

        const string systemPrompt =
            "Você é um assistente de suporte técnico de TI (RMM). " +
            "Resuma a transcrição do chat abaixo em português do Brasil de forma objetiva e concisa. " +
            "Use exatamente os tópicos:\n" +
            "- **Problema relatado:** ...\n" +
            "- **Ações realizadas:** ...\n" +
            "- **Desfecho:** ...";

        var llmOptions = new LlmOptions(
            MaxTokens: SummaryMaxTokens,
            Temperature: SummaryTemperature,
            Model: settings.ChatModel,
            BaseUrl: settings.BaseUrl,
            ApiKey: settings.ApiKey,
            EnableTools: false,
            Provider: settings.Provider,
            OpenRouterReferer: settings.OpenRouterReferer,
            OpenRouterTitle: settings.OpenRouterTitle,
            OpenRouterCategories: settings.OpenRouterCategories,
            TimeoutMs: settings.TimeoutMs > 0 ? Math.Min(settings.TimeoutMs, SummaryTimeoutMs) : SummaryTimeoutMs,
            ReasoningEnabled: false);

        try
        {
            var response = await llmProvider.CompleteAsync(
                systemPrompt,
                [new LlmMessage("user", transcript)],
                llmOptions,
                cancellationToken);

            var summary = (response?.Content ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(summary))
                return;

            await activityLog.LogActivityAsync(
                ticketId,
                TicketActivityType.Commented,
                null,
                comment: $"[Resumo automático IA — Chat de suporte]\n{summary}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível gerar o resumo automático por IA da sessão {SessionId}", sessionId);
        }
    }

    private static string BuildTranscript(IReadOnlyList<Core.Entities.TicketChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var author = msg.AuthorKind == TicketChatAuthorKind.Support
                ? (string.IsNullOrWhiteSpace(msg.AuthorName) ? "Suporte" : $"Suporte ({msg.AuthorName})")
                : (string.IsNullOrWhiteSpace(msg.AuthorName) ? "Usuário" : $"Usuário ({msg.AuthorName})");

            var line = $"[{msg.CreatedAt:HH:mm}] {author}: {msg.Body.Trim()}\n";
            if (sb.Length + line.Length > MaxTranscriptChars)
                break;

            sb.Append(line);
        }

        return sb.ToString().Trim();
    }
}
