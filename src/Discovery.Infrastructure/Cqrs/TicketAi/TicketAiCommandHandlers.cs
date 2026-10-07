using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.TicketAi.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services.Ai;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.TicketAi;

/// <summary>
/// Base dos fluxos de IA de chamado (triagem textual, resumo, resposta sugerida e
/// artigo de KB).
///
/// Centraliza o que antes se repetia em cada handler e causava erros:
/// - site efetivo (chamados sem SiteId usam um site do mesmo cliente);
/// - orçamento de tokens model-aware (capacidade real do modelo + teto do
///   tenant/produto) resolvido automaticamente para todos os fluxos;
/// - truncamento do prompt pelo orçamento (evita estourar a janela de contexto);
/// - uma única leitura do chamado (antes cada handler lia duas vezes).
/// </summary>
public abstract class TicketAiHandlerBase(
    ITicketRepository ticketRepo,
    IAiChatService aiChat,
    DiscoveryDbContext db,
    IAiTokenBudgetResolver budgetResolver,
    ILogger logger)
{
    protected const int DefaultMaxTokens = 1024;
    protected const double DefaultTemperature = 0.3;

    /// <summary>
    /// Contexto do chamado + site efetivo para resolver as configurações de IA.
    /// Retorna null em ambos quando o chamado não existe ou não há site utilizável.
    /// </summary>
    protected async Task<(Ticket? Ticket, Guid? SiteId)> GetTicketContextAsync(
        Guid ticketId, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return (null, null);

        var siteId = await AiSiteScopeResolver.ResolveAsync(db, ticket.SiteId, ticket.ClientId, ct);
        return (ticket, siteId);
    }

    protected static string FormatTicketForPrompt(Ticket ticket)
        => $"Título: {ticket.Title}\nDescrição: {ticket.Description}\nCategoria: {ticket.Category ?? "N/A"}\nPrioridade: {ticket.Priority}";

    protected async Task<Result<T>> ExecutePromptAsync<T>(
        Guid ticketId, string systemPrompt, Func<Ticket, string> buildMessage,
        int maxTokens, double temperature, Func<LlmResponse, T> map, CancellationToken ct)
        where T : notnull
    {
        var (ticket, siteId) = await GetTicketContextAsync(ticketId, ct);
        if (ticket is null)
            return Result<T>.Failure(Error.NotFound($"Ticket {ticketId} not found"));

        if (siteId is null)
            return Result<T>.Failure(Error.Internal("Chamado sem site para resolver a configuração de IA."));

        try
        {
            var budget = await budgetResolver.ResolveForSiteAsync(siteId.Value, maxTokens, null, ct);

            var userMessage = TruncatePrompt(buildMessage(ticket), budget.MaxPromptChars);
            var effectiveMaxTokens = Math.Min(maxTokens, budget.MaxOutputTokens);

            var response = await aiChat.ProcessTicketPromptAsync(
                systemPrompt, userMessage, siteId.Value, effectiveMaxTokens, temperature, null, ct);

            return Result<T>.Success(map(response));
        }
        catch (AiUsageLimitException ex)
        {
            // Rate limit/budget: mensagem específica em vez de erro genérico.
            return Result<T>.Failure(Error.Internal("Limite de uso de IA atingido: " + ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // Antes a ex.Message (interna) era devolvida ao cliente; agora só é logada.
            logger.LogWarning(ex, "Ticket AI: falha ao processar o prompt do chamado {TicketId}.", ticketId);
            return Result<T>.Failure(Error.Internal("Não foi possível processar a solicitação de IA."));
        }
    }

    /// <summary>Trunca o prompt pelo orçamento derivado da janela de contexto do modelo.</summary>
    private static string TruncatePrompt(string message, int maxChars)
    {
        if (maxChars <= 0 || message.Length <= maxChars) return message;
        return message[..maxChars] + "...";
    }
}

public sealed class TicketAiTriageCommandHandler(
    ITicketRepository ticketRepo, IAiChatService aiChat, DiscoveryDbContext db, IAiTokenBudgetResolver budgetResolver,
    ILogger<TicketAiTriageCommandHandler> logger)
    : TicketAiHandlerBase(ticketRepo, aiChat, db, budgetResolver, logger), IRequestHandler<TicketAiTriageCommand, Result<TicketAiTriageResult>>
{
    public async Task<Result<TicketAiTriageResult>> Handle(TicketAiTriageCommand cmd, CancellationToken ct)
    {
        var prompt = "Você é um assistente de triagem de tickets de TI. Analise o ticket e sugira: categoria, prioridade (Low/Medium/High/Critical), departamento apropriado e um breve resumo. Responda em português.";
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketForPrompt,
            DefaultMaxTokens, DefaultTemperature,
            r => new TicketAiTriageResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }
}

public sealed class TicketAiSummarizeCommandHandler(
    ITicketRepository ticketRepo, IAiChatService aiChat, DiscoveryDbContext db, IAiTokenBudgetResolver budgetResolver,
    ILogger<TicketAiSummarizeCommandHandler> logger)
    : TicketAiHandlerBase(ticketRepo, aiChat, db, budgetResolver, logger), IRequestHandler<TicketAiSummarizeCommand, Result<TicketAiSummaryResult>>
{
    public async Task<Result<TicketAiSummaryResult>> Handle(TicketAiSummarizeCommand cmd, CancellationToken ct)
    {
        var prompt = "Você é um assistente de resumo de tickets de TI. Gere um resumo executivo conciso do ticket, destacando: problema, impacto, ações já tomadas e próximos passos. Responda em português.";
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketForPrompt,
            DefaultMaxTokens, DefaultTemperature,
            r => new TicketAiSummaryResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }
}

public sealed class TicketAiSuggestReplyCommandHandler(
    ITicketRepository ticketRepo, IAiChatService aiChat, DiscoveryDbContext db, IAiTokenBudgetResolver budgetResolver,
    ILogger<TicketAiSuggestReplyCommandHandler> logger)
    : TicketAiHandlerBase(ticketRepo, aiChat, db, budgetResolver, logger), IRequestHandler<TicketAiSuggestReplyCommand, Result<TicketAiSuggestedReplyResult>>
{
    public async Task<Result<TicketAiSuggestedReplyResult>> Handle(TicketAiSuggestReplyCommand cmd, CancellationToken ct)
    {
        var prompt = "Você é um assistente técnico respondendo a um chamado de TI. Gere uma resposta profissional e empática para o cliente, abordando o problema relatado. Seja claro sobre prazos e próximos passos. Responda em português.";
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketForPrompt,
            DefaultMaxTokens * 2, 0.5,
            r => new TicketAiSuggestedReplyResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }
}

public sealed class TicketAiDraftKbArticleCommandHandler(
    ITicketRepository ticketRepo, IAiChatService aiChat, DiscoveryDbContext db, IAiTokenBudgetResolver budgetResolver,
    ILogger<TicketAiDraftKbArticleCommandHandler> logger)
    : TicketAiHandlerBase(ticketRepo, aiChat, db, budgetResolver, logger), IRequestHandler<TicketAiDraftKbArticleCommand, Result<TicketAiDraftKbResult>>
{
    public async Task<Result<TicketAiDraftKbResult>> Handle(TicketAiDraftKbArticleCommand cmd, CancellationToken ct)
    {
        var prompt = "Você é um redator técnico criando artigos para base de conhecimento de TI. Com base no ticket, crie um artigo estruturado com: título, sintoma, causa, solução e tags. Use markdown. Responda em português.";
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketForPrompt,
            DefaultMaxTokens * 2, 0.4,
            r => new TicketAiDraftKbResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }
}
