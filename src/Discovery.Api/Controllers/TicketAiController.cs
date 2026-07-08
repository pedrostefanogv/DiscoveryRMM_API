using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/tickets/{id:guid}/ai")]
public class TicketAiController(
    IAiChatService aiChat,
    ITicketRepository ticketRepo
) : ControllerBase
{
    private const int DefaultMaxTokens = 1024;
    private const double DefaultTemperature = 0.3;

    /// <summary>Sugestão de triagem: categoria, prioridade e departamento.</summary>
    [HttpPost("triage")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Triage(Guid id, CancellationToken ct)
    {
        var (ticket, siteId) = await GetTicketContextAsync(id, ct);
        if (ticket is null) return NotFound();

        var systemPrompt = "Você é um assistente de triagem de tickets de TI. Analise o ticket e sugira: categoria, prioridade (Low/Medium/High/Critical), departamento apropriado e um breve resumo. Responda em português.";
        var userMessage = FormatTicketForPrompt(ticket);

        return await ExecutePromptAsync(id, siteId, systemPrompt, userMessage, DefaultMaxTokens, DefaultTemperature,
            r => new TicketAiTriageResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }

    /// <summary>Resumo executivo do ticket.</summary>
    [HttpPost("summarize")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Summarize(Guid id, CancellationToken ct)
    {
        var (ticket, siteId) = await GetTicketContextAsync(id, ct);
        if (ticket is null) return NotFound();

        var systemPrompt = "Você é um assistente de resumo de tickets de TI. Gere um resumo executivo conciso do ticket, destacando: problema, impacto, ações já tomadas e próximos passos. Responda em português.";
        var userMessage = FormatTicketForPrompt(ticket);

        return await ExecutePromptAsync(id, siteId, systemPrompt, userMessage, DefaultMaxTokens, DefaultTemperature,
            r => new TicketAiSummaryResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }

    /// <summary>Sugestão de resposta ao cliente.</summary>
    [HttpPost("suggest-reply")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> SuggestReply(Guid id, CancellationToken ct)
    {
        var (ticket, siteId) = await GetTicketContextAsync(id, ct);
        if (ticket is null) return NotFound();

        var systemPrompt = "Você é um assistente técnico respondendo a um chamado de TI. Gere uma resposta profissional e empática para o cliente, abordando o problema relatado. Seja claro sobre prazos e próximos passos. Responda em português.";
        var userMessage = FormatTicketForPrompt(ticket);

        return await ExecutePromptAsync(id, siteId, systemPrompt, userMessage, DefaultMaxTokens * 2, 0.5,
            r => new TicketAiSuggestedReplyResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }

    /// <summary>Rascunho de artigo para base de conhecimento.</summary>
    [HttpPost("draft-kb-article")]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> DraftKbArticle(Guid id, [FromQuery] bool persist = false, CancellationToken ct = default)
    {
        var (ticket, siteId) = await GetTicketContextAsync(id, ct);
        if (ticket is null) return NotFound();

        var systemPrompt = "Você é um redator técnico criando artigos para base de conhecimento de TI. Com base no ticket, crie um artigo estruturado com: título, sintoma, causa, solução e tags. Use markdown. Responda em português.";
        var userMessage = FormatTicketForPrompt(ticket);

        return await ExecutePromptAsync(id, siteId, systemPrompt, userMessage, DefaultMaxTokens * 2, 0.4,
            r => new TicketAiDraftKbResult(r.Content, r.TokensUsed, r.ModelVersion), ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<(Ticket? Ticket, Guid SiteId)> GetTicketContextAsync(Guid ticketId, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return (null, Guid.Empty);

        var siteId = ticket.SiteId ?? Guid.Empty;
        return (ticket, siteId);
    }

    private static string FormatTicketForPrompt(Ticket ticket)
        => $"Título: {ticket.Title}\nDescrição: {ticket.Description}\nCategoria: {ticket.Category ?? "N/A"}\nPrioridade: {ticket.Priority}";

    private async Task<IActionResult> ExecutePromptAsync<T>(
        Guid ticketId, Guid siteId, string systemPrompt, string userMessage,
        int maxTokens, double temperature, Func<LlmResponse, T> map, CancellationToken ct)
    {
        try
        {
            var response = await aiChat.ProcessTicketPromptAsync(
                systemPrompt, userMessage, siteId, maxTokens, temperature, null, ct);
            return Ok(map(response));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = ex.Message });
        }
    }
}

// ── Response DTOs ───────────────────────────────────────────────────────

public record TicketAiBaseResult(string Content, int TokensUsed, string? Model);
public record TicketAiTriageResult(string Suggestion, int TokensUsed, string? Model);
public record TicketAiSummaryResult(string Summary, int TokensUsed, string? Model);
public record TicketAiSuggestedReplyResult(string SuggestedReply, int TokensUsed, string? Model);
public record TicketAiDraftKbResult(string Content, int TokensUsed, string? Model);
