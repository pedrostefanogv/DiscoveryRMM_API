using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoints de IA aplicada a tickets: triagem, resumo e sugestão de resposta.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/{id:guid}/ai")]
public class TicketAiController : ControllerBase
{
    private readonly ITicketRepository _ticketRepo;
    private readonly ILlmProvider _llmProvider;
    private readonly IConfigurationResolver _configResolver;
    private readonly IActivityLogService _activityLogService;
    private readonly IKnowledgeArticleRepository _knowledgeRepo;

    public TicketAiController(
        ITicketRepository ticketRepo,
        ILlmProvider llmProvider,
        IConfigurationResolver configResolver,
        IActivityLogService activityLogService,
        IKnowledgeArticleRepository knowledgeRepo)
    {
        _ticketRepo = ticketRepo;
        _llmProvider = llmProvider;
        _configResolver = configResolver;
        _activityLogService = activityLogService;
        _knowledgeRepo = knowledgeRepo;
    }

    /// <summary>
    /// Sugere categoria, prioridade e departamento para o ticket.
    /// </summary>
    [HttpPost("triage")]
    public async Task<IActionResult> Triage(Guid id, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var aiSettings = await _configResolver.GetAISettingsAsync();
        if (!aiSettings.Enabled || string.IsNullOrWhiteSpace(aiSettings.ApiKey))
            return StatusCode(503, new { error = "IA não configurada." });

        var systemPrompt =
            "Você é um assistente de triagem de suporte técnico. " +
            "Analise o ticket e responda APENAS com JSON no formato: " +
            "{\"category\": string, \"priority\": \"Low|Medium|High|Critical\", \"department\": string, \"reasoning\": string}. " +
            "Não inclua nenhum texto fora do JSON.";

        var userMessage = $"Título: {ticket.Title}\n\nDescrição: {ticket.Description}";

        var llmOptions = BuildLlmOptions(aiSettings, maxTokens: 400, temperature: 0.2);
        var response = await _llmProvider.CompleteAsync(
            systemPrompt,
            [new("user", userMessage)],
            llmOptions,
            ct);

        await _activityLogService.LogActivityAsync(
            id, TicketActivityType.DescriptionUpdated, null, null, "ai:triage",
            "Triagem automática gerada por IA");

        return Ok(new
        {
            ticketId = id,
            suggestion = response.Content,
            tokensUsed = response.TokensUsed,
            model = response.ModelVersion
        });
    }

    /// <summary>
    /// Gera um resumo executivo do ticket com base no histórico de comentários.
    /// </summary>
    [HttpPost("summarize")]
    public async Task<IActionResult> Summarize(Guid id, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var aiSettings = await _configResolver.GetAISettingsAsync();
        if (!aiSettings.Enabled || string.IsNullOrWhiteSpace(aiSettings.ApiKey))
            return StatusCode(503, new { error = "IA não configurada." });

        var comments = await _ticketRepo.GetCommentsAsync(id);
        var commentText = string.Join("\n---\n", comments
            .OrderBy(c => c.CreatedAt)
            .Select(c => $"[{c.CreatedAt:dd/MM/yyyy HH:mm}] {c.Author}: {c.Content}"));

        var systemPrompt =
            "Você é um assistente de suporte técnico. " +
            "Gere um resumo executivo conciso (máximo 3 parágrafos) do ticket de suporte abaixo, " +
            "incluindo: problema relatado, ações tomadas e situação atual. Responda em português.";

        var userMessage =
            $"Título: {ticket.Title}\n\n" +
            $"Descrição: {ticket.Description}\n\n" +
            $"Histórico de comentários:\n{(string.IsNullOrWhiteSpace(commentText) ? "(nenhum comentário)" : commentText)}";

        var llmOptions = BuildLlmOptions(aiSettings, maxTokens: 600, temperature: 0.4);
        var response = await _llmProvider.CompleteAsync(
            systemPrompt,
            [new("user", userMessage)],
            llmOptions,
            ct);

        return Ok(new
        {
            ticketId = id,
            summary = response.Content,
            tokensUsed = response.TokensUsed,
            model = response.ModelVersion
        });
    }

    /// <summary>
    /// Sugere a próxima resposta ao usuário com base no histórico do ticket.
    /// </summary>
    [HttpPost("suggest-reply")]
    public async Task<IActionResult> SuggestReply(Guid id, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var aiSettings = await _configResolver.GetAISettingsAsync();
        if (!aiSettings.Enabled || string.IsNullOrWhiteSpace(aiSettings.ApiKey))
            return StatusCode(503, new { error = "IA não configurada." });

        var comments = await _ticketRepo.GetCommentsAsync(id);
        var commentText = string.Join("\n---\n", comments
            .OrderBy(c => c.CreatedAt)
            .Select(c => $"[{c.Author}]: {c.Content}"));

        var systemPrompt =
            "Você é um agente de suporte técnico experiente. " +
            "Com base no ticket e no histórico de interações, escreva uma resposta profissional e empática " +
            "para o próximo passo no atendimento. Responda em português, de forma direta e objetiva.";

        var userMessage =
            $"Título: {ticket.Title}\n\n" +
            $"Descrição: {ticket.Description}\n\n" +
            $"Histórico:\n{(string.IsNullOrWhiteSpace(commentText) ? "(sem interações anteriores)" : commentText)}";

        var llmOptions = BuildLlmOptions(aiSettings, maxTokens: 500, temperature: 0.6);
        var response = await _llmProvider.CompleteAsync(
            systemPrompt,
            [new("user", userMessage)],
            llmOptions,
            ct);

        return Ok(new
        {
            ticketId = id,
            suggestedReply = response.Content,
            tokensUsed = response.TokensUsed,
            model = response.ModelVersion
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Gera um rascunho de artigo de KB a partir de um ticket resolvido.
    /// O artigo é criado como rascunho (IsPublished=false) e o vínculo é registrado.
    /// </summary>
    [HttpPost("draft-kb-article")]
    public async Task<IActionResult> DraftKbArticle(Guid id, [FromBody] DraftKbArticleRequest? req, CancellationToken ct)
    {
        var ticket = await _ticketRepo.GetByIdAsync(id);
        if (ticket is null) return NotFound();

        var aiSettings = await _configResolver.GetAISettingsAsync();
        if (!aiSettings.Enabled || string.IsNullOrWhiteSpace(aiSettings.ApiKey))
            return StatusCode(503, new { error = "IA não configurada." });

        var comments = await _ticketRepo.GetCommentsAsync(id);
        var commentText = string.Join("\n---\n", comments
            .OrderBy(c => c.CreatedAt)
            .Select(c => $"[{c.Author}] {c.Content}"));

        var systemPrompt =
            "Você é um especialista em documentação técnica. " +
            "Com base no ticket de suporte e seu histórico, gere um artigo de base de conhecimento em Markdown. " +
            "Responda APENAS com JSON no formato: " +
            "{\"title\": string, \"summary\": string, \"content\": string, \"tags\": [string]}. " +
            "O campo 'content' deve ser Markdown completo com seções: Problema, Causa, Solução, Observações. " +
            "Não inclua nenhum texto fora do JSON.";

        var userMessage =
            $"Título do ticket: {ticket.Title}\n\n" +
            $"Descrição: {ticket.Description}\n\n" +
            $"Histórico:\n{(string.IsNullOrWhiteSpace(commentText) ? "(sem comentários)" : commentText)}";

        var llmOptions = BuildLlmOptions(aiSettings, maxTokens: 1200, temperature: 0.3);
        var response = await _llmProvider.CompleteAsync(
            systemPrompt,
            [new("user", userMessage)],
            llmOptions,
            ct);

        // Se solicitado, persiste como rascunho de artigo
        KnowledgeArticle? createdArticle = null;
        if (req?.PersistAsDraft == true && !string.IsNullOrWhiteSpace(response.Content))
        {
            createdArticle = await _knowledgeRepo.CreateAsync(new KnowledgeArticle
            {
                Id = Guid.NewGuid(),
                Title = ticket.Title,
                Content = response.Content,
                Author = "ai-draft",
                ClientId = ticket.ClientId,
                SiteId = ticket.SiteId,
                IsPublished = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }, ct);

            await _knowledgeRepo.LinkToTicketAsync(id, createdArticle.Id, "ai-draft-kb-article", null, ct);
        }

        await _activityLogService.LogActivityAsync(
            id, TicketActivityType.DescriptionUpdated, null, null, "ai:draft-kb-article",
            createdArticle is null
                ? "Rascunho de artigo KB gerado por IA (não salvo)"
                : $"Rascunho de artigo KB criado: {createdArticle.Id}");

        return Ok(new
        {
            ticketId = id,
            aiContent = response.Content,
            articleId = createdArticle?.Id,
            persisted = createdArticle is not null,
            tokensUsed = response.TokensUsed,
            model = response.ModelVersion
        });
    }

    private static LlmOptions BuildLlmOptions(
        Discovery.Core.ValueObjects.AIIntegrationSettings ai,
        int maxTokens,
        double temperature)
    {
        return new LlmOptions(
            MaxTokens: maxTokens,
            Temperature: temperature,
            Model: string.IsNullOrWhiteSpace(ai.ChatModel) ? null : ai.ChatModel,
            BaseUrl: string.IsNullOrWhiteSpace(ai.BaseUrl) ? null : ai.BaseUrl,
            ApiKey: string.IsNullOrWhiteSpace(ai.ApiKey) ? null : ai.ApiKey);
    }
}
