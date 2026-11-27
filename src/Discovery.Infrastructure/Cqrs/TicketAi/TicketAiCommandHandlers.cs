using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.TicketAi.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services.Ai;
using MediatR;
using Microsoft.EntityFrameworkCore;
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
    protected ITicketRepository TicketRepository => ticketRepo;
    protected DiscoveryDbContext Db => db;

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

    protected async Task<string> FormatTicketContextAsync(Ticket ticket, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Título: {ticket.Title}");
        sb.AppendLine($"Descrição: {ticket.Description}");
        sb.AppendLine($"Categoria: {ticket.Category ?? "N/A"}");
        sb.AppendLine($"Prioridade: {ticket.Priority}");
        if (ticket.SlaExpiresAt.HasValue)
            sb.AppendLine($"Prazo SLA (UTC): {ticket.SlaExpiresAt.Value:yyyy-MM-dd HH:mm}");

        try
        {
            var comments = await db.TicketComments.AsNoTracking()
                .Where(c => c.TicketId == ticket.Id)
                .OrderByDescending(c => c.CreatedAt)
                .Take(8)
                .Select(c => new { c.IsInternal, c.Content, c.CreatedAt })
                .ToListAsync(ct);

            if (comments.Count > 0)
            {
                sb.AppendLine("\nHistórico recente de comentários (mais antigos primeiro):");
                foreach (var c in comments.AsEnumerable().Reverse())
                {
                    var scope = c.IsInternal ? "[Nota interna]" : "[Público]";
                    var body = c.Content.Length > 500 ? c.Content[..500] + "..." : c.Content;
                    sb.AppendLine($"- {c.CreatedAt:yyyy-MM-dd HH:mm} {scope}: {body}");
                }
            }
            else
            {
                sb.AppendLine("\nHistórico de comentários: Nenhum comentário registrado até o momento.");
            }
        }
        catch
        {
            // Fallback seguro para DbContexts parciais em testes unitários
        }

        try
        {
            var departments = await db.Departments.AsNoTracking()
                .Where(d => d.IsActive && (d.ClientId == null || d.ClientId == ticket.ClientId))
                .OrderBy(d => d.Name)
                .Take(20)
                .Select(d => d.Name)
                .ToListAsync(ct);

            if (departments.Count > 0)
                sb.AppendLine($"\nDepartamentos válidos: {string.Join(", ", departments)}");
        }
        catch
        {
            // Fallback seguro para DbContexts parciais em testes unitários
        }

        return sb.ToString();
    }

    protected async Task<Result<T>> ExecutePromptAsync<T>(
        Guid ticketId, string systemPrompt, Func<Ticket, CancellationToken, Task<string>> buildMessageAsync,
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

            var rawContext = await buildMessageAsync(ticket, ct);
            var userMessage = TruncatePrompt(rawContext, budget.MaxPromptChars);
            var effectiveMaxTokens = Math.Min(maxTokens, budget.MaxOutputTokens);

            var response = await aiChat.ProcessTicketPromptAsync(
                systemPrompt, userMessage, siteId.Value, effectiveMaxTokens, temperature, null, ct);

            return Result<T>.Success(map(response));
        }
        catch (AiUsageLimitException ex)
        {
            // Rate limit/budget: prefixo específico para devolver HTTP 429.
            return Result<T>.Failure(Error.Internal("AiUsageLimit: Limite de uso de IA atingido: " + ex.Message));
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
        var prompt = "Você é um assistente de triagem de tickets de TI. Analise o ticket (e seus comentários) e sugira: categoria, prioridade (Low/Medium/High/Critical), departamento apropriado (escolha apenas dentre os Departamentos válidos informados) e um breve resumo. Responda em português.";
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketContextAsync,
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
        var prompt = "Você é um assistente de resumo de tickets de TI. Gere um resumo executivo conciso do ticket, destacando: problema, impacto, ações já tomadas (se não houver ações registradas no histórico, declare explicitamente 'Nenhuma ação registrada') e próximos passos. Nunca invente ações não registradas. Responda em português.";
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketContextAsync,
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
        var prompt = "Você é um assistente técnico respondendo a um chamado de TI. Gere uma resposta profissional e empática para o cliente, abordando o problema relatado e o histórico. Nunca invente prazos ou ETAs que não estejam explícitos no contexto (use apenas o Prazo SLA se informado). Responda em português.";
        return await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketContextAsync,
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
        var prompt = "Você é um redator técnico criando artigos para base de conhecimento de TI. Com base no ticket e no histórico, crie um artigo estruturado em Markdown com: título (# Título), sintoma, causa, solução e tags. Remova/anonimize dados pessoais (nomes, IPs privados, senhas). Se a solução ainda não estiver registrada, marque como rascunho pendente de validação. Responda em português.";
        var res = await ExecutePromptAsync(cmd.TicketId, prompt, FormatTicketContextAsync,
            DefaultMaxTokens * 2, 0.4,
            r => new TicketAiDraftKbResult(r.Content, r.TokensUsed, r.ModelVersion), ct);

        if (res.IsSuccess && cmd.Persist)
        {
            var ticket = await TicketRepository.GetByIdAsync(cmd.TicketId);
            if (ticket is not null)
            {
                var draftMarkdown = res.Value!.Content;
                var firstLine = draftMarkdown.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.TrimStart('#', ' ').Trim();
                var title = !string.IsNullOrWhiteSpace(firstLine)
                    ? (firstLine.Length > 180 ? firstLine[..180] : firstLine)
                    : $"KB: {ticket.Title}";

                var article = new KnowledgeArticle
                {
                    Id = Guid.NewGuid(),
                    Title = title,
                    Content = draftMarkdown,
                    Category = ticket.Category ?? "Troubleshooting",
                    Status = ArticleStatus.Draft.ToString(),
                    ClientId = ticket.ClientId,
                    SiteId = ticket.SiteId,
                    DepartmentId = ticket.DepartmentId,
                    AiIndexingEnabled = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                Db.KnowledgeArticles.Add(article);
                await Db.SaveChangesAsync(ct);
            }
        }

        return res;
    }
}
