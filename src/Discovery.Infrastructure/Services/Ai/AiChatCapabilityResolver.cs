using System.Text;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class AiChatCapabilityResolver
{
    private readonly IMcpToolExecutor _mcpToolExecutor;
    private readonly IAiModelCatalogService _modelCatalog;
    private readonly IAiTokenBudgetResolver _tokenBudgetResolver;
    private readonly ILlmProvider _llmProvider;
    private readonly ILogger<AiChatService> _logger;

    public AiChatCapabilityResolver(
        IMcpToolExecutor mcpToolExecutor,
        IAiModelCatalogService modelCatalog,
        IAiTokenBudgetResolver tokenBudgetResolver,
        ILlmProvider llmProvider,
        ILogger<AiChatService> logger)
    {
        _mcpToolExecutor = mcpToolExecutor;
        _modelCatalog = modelCatalog;
        _tokenBudgetResolver = tokenBudgetResolver;
        _llmProvider = llmProvider;
        _logger = logger;
    }

    /// <summary>
    /// Resolve se a capacidade A2UI está habilitada para o escopo (governança de
    /// MCP tools, com herança). Ausência de política = HABILITADA, para não
    /// desligar a interface em bases que ainda não têm a linha.
    /// </summary>
    internal async Task<bool> IsA2uiEnabledAsync(
        Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct)
    {
        try
        {
            return await _mcpToolExecutor.IsToolEnabledAsync(
                McpToolCatalogMetadata.A2uiCapability, clientId, siteId, agentId, ct);
        }
        catch (Exception ex)
        {
            // Fail-open consciente: o gate é de RENDERIZAÇÃO (não de segurança) e
            // uma falha de leitura da política não pode derrubar o chat.
            _logger.LogWarning(ex, "Falha ao ler a politica da capacidade A2UI; assumindo HABILITADA");
            return true;
        }
    }

    /// <summary>
    /// Verifica se o modelo configurado aceita imagens. O catálogo (cache de
    /// 60 min) só é consultado quando existe imagem para enviar; qualquer falha
    /// mantém o comportamento anterior (envia) — nunca bloquear a captura por
    /// indisponibilidade do catálogo.
    /// </summary>
    internal async Task<bool> ShouldSendScreenshotImagesAsync(
        AIIntegrationSettings settings, Guid clientId, Guid siteId, CancellationToken ct)
    {
        if (!settings.SendScreenshotImages) return false;
        var model = settings.ChatModel;
        if (string.IsNullOrWhiteSpace(model)) return true;
        try
        {
            var info = await _modelCatalog.GetModelAsync(
                clientId == Guid.Empty ? null : clientId,
                siteId == Guid.Empty ? null : siteId,
                model, ct);
            return AiChatHelpers.ResolveScreenshotImagesAllowed(true, info);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao resolver capacidade de visão do modelo {Model}; mantendo envio de imagens", model);
            return true;
        }
    }

    /// <summary>
    /// Verifica se o modelo configurado aceita function calling (FIX-3). Espelha
    /// ShouldSendScreenshotImagesAsync: o catálogo (cache de 60 min) só é
    /// consultado quando há tools para oferecer, e qualquer falha mantém o
    /// comportamento anterior (oferece) — nunca derrubar o chat por
    /// indisponibilidade do catálogo.
    /// </summary>
    internal async Task<bool> ShouldSendToolsAsync(
        AIIntegrationSettings settings, Guid clientId, Guid siteId, CancellationToken ct)
    {
        var model = settings.ChatModel;
        if (string.IsNullOrWhiteSpace(model)) return true;
        try
        {
            var info = await _modelCatalog.GetModelAsync(
                clientId == Guid.Empty ? null : clientId,
                siteId == Guid.Empty ? null : siteId,
                model, ct);
            return AiChatHelpers.ResolveToolsAllowed(info);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao resolver capacidade de tools do modelo {Model}; mantendo envio de tools", model);
            return true;
        }
    }

    /// <summary>
    /// Teto de tokens efetivo do chat: valor configurado no tenant limitado pela
    /// capacidade REAL do modelo e pelo teto do produto — o mesmo orçamento usado
    /// nos fluxos de IA de ticket. Sem isso, o chat aceitava a configuração do
    /// tenant mesmo quando o modelo não suporta aquele volume de saída.
    /// Falha do resolvedor não interrompe o chat: mantém o teto do tenant.
    /// </summary>
    internal async Task<int> ResolveChatMaxTokensAsync(
        Guid siteId, AIIntegrationSettings aiSettings, CancellationToken ct)
    {
        // 0 = AUTOMÁTICO (sem pedido explícito): o resolvedor deriva o teto da
        // capacidade real do modelo. NÃO trocar o 0 por um default aqui.
        var tenantMaxTokens = AiChatHelpers.ClampMaxTokens(aiSettings);

        try
        {
            var budget = await _tokenBudgetResolver.ResolveForSiteAsync(siteId, tenantMaxTokens, null, ct);
            return budget.MaxOutputTokens;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao resolver o orçamento de tokens do chat; usando o teto do tenant.");
            // O tenant pode estar em AUTOMÁTICO (0), que não é um teto utilizável
            // como valor de max_tokens: devolver 0 enviaria "max_tokens: 0" ao
            // provedor. Nesse caso cai no default do produto — o resolvedor é o
            // único que sabe converter 0 em capacidade de modelo.
            return tenantMaxTokens > 0 ? tenantMaxTokens : AiChatConstants.DefaultMaxTokens;
        }
    }

    /// <summary>
    /// M2 (revisão 2026-10-08): a resposta criou a surface A2UI mas não enviou a
    /// definição (updateComponents com "root") — sem ela o renderer fica preso em
    /// "Loading surface...". Pede UMA reemissão ao LLM, sem ferramentas e SEM
    /// streamar os tokens (o texto do primeiro passe já foi exibido; duplicar a
    /// prosa confundiria o usuário). Devolve o conteúdo bruto da reemissão, ou
    /// vazio quando o provider falhou.
    /// </summary>
    internal async Task<(string Content, int TokensUsed)> TryReemitA2uiDefinitionAsync(
        string systemPrompt,
        List<LlmMessage> llmMessages,
        AIIntegrationSettings aiSettings,
        int maxTokens,
        Guid sessionId,
        string previousOutput,
        IReadOnlyCollection<string> missingSurfaces,
        CancellationToken ct)
    {
        try
        {
            // Sem devolver a própria saída ao modelo, ele não teria como saber o
            // surfaceId nem os componentes que acabou de emitir: o assistant do
            // stream direto NÃO entra em llmMessages (só é persistido no fim do
            // turno). A reemissão viraria uma interface nova "no escuro".
            if (!string.IsNullOrWhiteSpace(previousOutput))
                llmMessages.Add(new LlmMessage("assistant", previousOutput));
            llmMessages.Add(new LlmMessage("system", AiChatHelpers.BuildA2uiIncompleteDefinitionNote(missingSurfaces)));

            var builder = new StringBuilder();
            var tokensUsed = 0;
            // StreamWithToolsAsync (SEM tools) em vez de StreamAsync: o evento
            // "done" traz TokensUsed, então o custo da reemissão entra na
            // telemetria do turno — o StreamAsync simples não reporta usage.
            await foreach (var evt in _llmProvider.StreamWithToolsAsync(
                systemPrompt, llmMessages, BuildNoToolsOptions(aiSettings, maxTokens, sessionId), ct))
            {
                if (string.Equals(evt.Type, "done", StringComparison.OrdinalIgnoreCase))
                {
                    tokensUsed = evt.TokensUsed ?? 0;
                    continue;
                }
                if (string.Equals(evt.Type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[A2UI] Reemissão da definição retornou erro do provider: {Error}", evt.Content);
                    return (string.Empty, tokensUsed);
                }
                if (!string.IsNullOrEmpty(evt.Content))
                    builder.Append(evt.Content);
            }
            return (builder.ToString(), tokensUsed);
        }
        catch (Exception ex)
        {
            // A reemissão é best-effort: falhar aqui NÃO pode derrubar o turno
            // (o texto do card que anunciava a interface já foi entregue).
            _logger.LogWarning(ex, "[A2UI] Falha ao pedir a reemissão da definição da interface");
            return (string.Empty, 0);
        }
    }

    /// <summary>
    /// Constrói a mensagem do usuário com partes multimodais quando há imagens
    /// anexadas (prints do chat). Sem imagens, comportamento anterior (string).
    /// </summary>
    internal static LlmMessage BuildUserMessageWithImages(
        string message, List<string>? images, ILogger? logger, out int omittedImages,
        int maxImageBase64Chars = 0)
    {
        // Sem teto de QUANTIDADE: todas as imagens anexadas viram partes
        // multimodais, respeitando o teto por imagem (configuração de anexos) e
        // o orçamento total do turno. omittedImages alimenta a nota de sistema.
        var imageParts = AiChatHelpers.BuildImagePartsFromDataUrlsWithBudget(
            images, logger, AiChatHelpers.MaxTotalImageBase64Chars, out omittedImages,
            maxImageBase64Chars);
        if (imageParts is null)
        {
            return new LlmMessage("user", message);
        }
        var parts = new List<LlmContentPart> { new("text", Text: message) };
        parts.AddRange(imageParts);
        return new LlmMessage("user", message, ContentParts: parts);
    }

    /// <summary>Trunca um texto para log sem depender de helpers externos.</summary>
    internal static string Shorten(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max] + "...");

    /// <summary>
    /// Opções de LLM sem ferramentas, usadas nas chamadas de reparo/síntese fora
    /// do loop de tools (mesmos parâmetros de amostragem da conversa).
    /// </summary>
    internal static LlmOptions BuildNoToolsOptions(AIIntegrationSettings aiSettings, int maxTokens, Guid sessionId) =>
        new(
            maxTokens, AiChatHelpers.ClampTemperature(aiSettings),
            string.IsNullOrWhiteSpace(aiSettings.ChatModel) ? null : aiSettings.ChatModel,
            string.IsNullOrWhiteSpace(aiSettings.BaseUrl) ? null : aiSettings.BaseUrl,
            string.IsNullOrWhiteSpace(aiSettings.ApiKey) ? null : aiSettings.ApiKey,
            false, null, aiSettings.Provider,
            aiSettings.OpenRouterReferer, aiSettings.OpenRouterTitle, aiSettings.OpenRouterCategories,
            SessionId: sessionId.ToString("D"),
            TimeoutMs: AiChatHelpers.ClampAiTimeoutMs(aiSettings),
            TopP: AiChatHelpers.ClampTopP(aiSettings),
            FrequencyPenalty: AiChatHelpers.ClampPenalty(aiSettings.FrequencyPenalty),
            PresencePenalty: AiChatHelpers.ClampPenalty(aiSettings.PresencePenalty),
            Seed: aiSettings.Seed,
            ResponseFormat: aiSettings.ResponseFormat,
            ReasoningEnabled: aiSettings.ReasoningEnabled, ReasoningEffort: aiSettings.ReasoningEffort);
}
