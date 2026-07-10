using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.TicketAi.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.TicketAi;

public abstract class TicketAiHandlerBase(ITicketRepository ticketRepo, IAiChatService aiChat)
{
    protected const int DefaultMaxTokens = 1024;
    protected const double DefaultTemperature = 0.3;

    protected async Task<(Ticket? Ticket, Guid SiteId)> GetTicketContextAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return (null, Guid.Empty);
        return (ticket, ticket.SiteId ?? Guid.Empty);
    }

    protected static string FormatTicketForPrompt(Ticket ticket)
        => $"Título: {ticket.Title}\nDescrição: {ticket.Description}\nCategoria: {ticket.Category ?? "N/A"}\nPrioridade: {ticket.Priority}";

    protected async Task<Result<T>> ExecutePromptAsync<T>(
        Guid ticketId, string systemPrompt, string userMessage,
        int maxTokens, double temperature, Func<LlmResponse, T> map, CancellationToken ct)
    {
        var (ticket, siteId) = await GetTicketContextAsync(ticketId, ct);
        if (ticket is null)
            return Result<T>.Failure(Error.NotFound($"Ticket {ticketId} not found"));

        try
        {
            var response = await aiChat.ProcessTicketPromptAsync(
                systemPrompt, userMessage, siteId, maxTokens, temperature, null, ct);
            return Result<T>.Success(map(response));
        }
        catch (InvalidOperationException ex)
        {
            return Result<T>.Failure(Error.Internal(ex.Message));
        }
    }
}

public sealed class TicketAiTriageCommandHandler(ITicketRepository ticketRepo, IAiChatService aiChat)
    : TicketAiHandlerBase(ticketRepo, aiChat), IRequestHandler<TicketAiTriageCommand, Result<TicketAiTriageResult>>
{
    public async Task<Result<TicketAiTriageResult>> Handle(TicketAiTriageCommand cmd, CancellationToken ct)
    {
        var prompt = "Você é um assistente de triagem de tickets de TI. Analise o ticket e sugira: categoria, prioridade (Low/Medium/High/Critical), departamento apropriado e um breve resumo. Responda em português.";
        var (ticket, _) = await GetTicketContextAsync(cmd.TicketId, ct);
        if (ticket is null) return Result<TicketAiTriageResult>.Failure(Error.NotFound($"Ticket {cmd.TicketId} not found"));
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketForPrompt(ticket),
            DefaultMaxTokens, DefaultTemperature,
            r => new TicketAiTriageResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }
}

public sealed class TicketAiSummarizeCommandHandler(ITicketRepository ticketRepo, IAiChatService aiChat)
    : TicketAiHandlerBase(ticketRepo, aiChat), IRequestHandler<TicketAiSummarizeCommand, Result<TicketAiSummaryResult>>
{
    public async Task<Result<TicketAiSummaryResult>> Handle(TicketAiSummarizeCommand cmd, CancellationToken ct)
    {
        var prompt = "Você é um assistente de resumo de tickets de TI. Gere um resumo executivo conciso do ticket, destacando: problema, impacto, ações já tomadas e próximos passos. Responda em português.";
        var (ticket, _) = await GetTicketContextAsync(cmd.TicketId, ct);
        if (ticket is null) return Result<TicketAiSummaryResult>.Failure(Error.NotFound($"Ticket {cmd.TicketId} not found"));
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketForPrompt(ticket),
            DefaultMaxTokens, DefaultTemperature,
            r => new TicketAiSummaryResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }
}

public sealed class TicketAiSuggestReplyCommandHandler(ITicketRepository ticketRepo, IAiChatService aiChat)
    : TicketAiHandlerBase(ticketRepo, aiChat), IRequestHandler<TicketAiSuggestReplyCommand, Result<TicketAiSuggestedReplyResult>>
{
    public async Task<Result<TicketAiSuggestedReplyResult>> Handle(TicketAiSuggestReplyCommand cmd, CancellationToken ct)
    {
        var prompt = "Você é um assistente técnico respondendo a um chamado de TI. Gere uma resposta profissional e empática para o cliente, abordando o problema relatado. Seja claro sobre prazos e próximos passos. Responda em português.";
        var (ticket, _) = await GetTicketContextAsync(cmd.TicketId, ct);
        if (ticket is null) return Result<TicketAiSuggestedReplyResult>.Failure(Error.NotFound($"Ticket {cmd.TicketId} not found"));
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketForPrompt(ticket),
            DefaultMaxTokens * 2, 0.5,
            r => new TicketAiSuggestedReplyResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }
}

public sealed class TicketAiDraftKbArticleCommandHandler(ITicketRepository ticketRepo, IAiChatService aiChat)
    : TicketAiHandlerBase(ticketRepo, aiChat), IRequestHandler<TicketAiDraftKbArticleCommand, Result<TicketAiDraftKbResult>>
{
    public async Task<Result<TicketAiDraftKbResult>> Handle(TicketAiDraftKbArticleCommand cmd, CancellationToken ct)
    {
        var prompt = "Você é um redator técnico criando artigos para base de conhecimento de TI. Com base no ticket, crie um artigo estruturado com: título, sintoma, causa, solução e tags. Use markdown. Responda em português.";
        var (ticket, _) = await GetTicketContextAsync(cmd.TicketId, ct);
        if (ticket is null) return Result<TicketAiDraftKbResult>.Failure(Error.NotFound($"Ticket {cmd.TicketId} not found"));
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketForPrompt(ticket),
            DefaultMaxTokens * 2, 0.4,
            r => new TicketAiDraftKbResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }
}
