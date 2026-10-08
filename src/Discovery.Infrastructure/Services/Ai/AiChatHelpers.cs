using System.Text.Json;
using System.Text.Json.Nodes;
using Discovery.Core.DTOs;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Helpers estáticos compartilhados entre os sub-orquestradores do AiChat.
/// </summary>
internal static class AiChatHelpers
{
    public static int ClampHistoryMessages(AIIntegrationSettings settings)
        => settings.MaxHistoryMessages is >= 1 and <= 50 ? settings.MaxHistoryMessages : AiChatConstants.DefaultMaxHistoryMessages;

    public static int ClampMaxTokens(AIIntegrationSettings settings)
        => settings.MaxTokensPerRequest is >= 100 and <= AiChatConstants.MaxOutputTokensCeiling
            ? settings.MaxTokensPerRequest
            : AiChatConstants.DefaultMaxTokens;

    public static double ClampTemperature(AIIntegrationSettings settings)
        => settings.Temperature is >= 0 and <= 2 ? settings.Temperature : AiChatConstants.DefaultTemperature;

    /// <summary>
    /// Resolve o orçamento de iterações do agent loop a partir das configurações.
    /// Único ponto de verdade — evita a lógica duplicada em StreamAsync,
    /// StreamMultiRoundAsync e ProcessSyncAsync.
    /// </summary>
    public static int ResolveMaxToolIterations(AIIntegrationSettings settings)
        => settings.MaxToolCallIterations is >= 1 and <= AiChatConstants.MaxToolCallIterationsLimit
            ? settings.MaxToolCallIterations
            : AiChatConstants.DefaultMaxToolCallIterations;

    // ── Notas de sistema do agent loop ─────────────────────────────────────

    /// <summary>Injetada quando o orçamento de iterações esgota e o LLM ainda queria tools.</summary>
    public const string SynthesisBudgetNote =
        "[SISTEMA] O orçamento de iterações de ferramentas esgotou. Sintetize AGORA uma resposta final, " +
        "completa e útil para o usuário, com base em tudo que já foi coletado. NÃO faça mais chamadas de ferramentas.";

    /// <summary>Injetada quando a base de conhecimento não retorna resultados repetidamente.</summary>
    public const string KbExhaustedNote =
        "[SISTEMA] A base de conhecimento não retornou resultados para as buscas realizadas. " +
        "Responda com seu conhecimento próprio. NÃO repita buscas de conteúdo; " +
        "se o usuário precisar do catálogo de artigos, use knowledge_list.";

    /// <summary>Injetada quando a execução de tool no agent expira (round pendente).</summary>
    public const string AgentRoundExpiredNote =
        "[SISTEMA] A execução da ferramenta no agent expirou (sem resposta no prazo). " +
        "Informe o usuário que a ação não pôde ser concluída no momento, explique o que foi possível apurar " +
        "e sugira alternativas (ex.: tentar novamente mais tarde).";

    /// <summary>
    /// Resultado sintético que fecha as tool calls do agent quando o orçamento
    /// do turno esgota e a ferramenta NÃO é delegada. Necessário porque a
    /// mensagem assistant com tool_calls já entrou em llmMessages; provedores
    /// OpenAI-compatible rejeitam tool_call sem tool message correspondente
    /// (400), e a síntese seguinte roda sem tools.
    /// </summary>
    public const string AgentToolBudgetExhaustedResult =
        "{\"budget_exhausted\":true,\"message\":\"Orçamento de iterações de ferramentas esgotado; a ferramenta não foi executada.\"}";

    /// <summary>Injetada quando o LLM não produziu conteúdo visível.</summary>
    public const string EmptyContentNote =
        "[SISTEMA] Você não forneceu uma resposta visível ao usuário. Forneça uma resposta direta e útil à última pergunta do usuário.";

    public static string ExtractKbQuery(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("query", out var qProp) && qProp.ValueKind == JsonValueKind.String)
                return qProp.GetString() ?? string.Empty;
        }
        catch { }
        return argumentsJson;
    }

    /// <summary>
    /// Resolve o timeout HTTP para chamadas LLM a partir das configurações.
    /// Streaming de LLM pode demorar (reasoning, tool chains longas), então usa
    /// piso de 60s para não cortar gerações que o HttpClient "AiChat" (antigo
    /// 60s fixo) já permitia. Valores configurados acima do piso são honrados.
    /// Retorna 0 quando não configurado, deixando o HttpClient usar seu default.
    /// </summary>
    public static int ClampAiTimeoutMs(AIIntegrationSettings settings)
    {
        var ms = settings.TimeoutMs;
        // B9/B16: piso de 120s — modelos de raciocínio roteados pelo OpenRouter
        // (ex.: openrouter/auto → DeepSeek) frequentemente levam mais de 60s até o
        // primeiro token visível. O cap de 60s cortava a geração e o cliente
        // recebia um stream vazio.
        if (ms <= 0) return 120_000;
        return Math.Max(ms, 120_000);
    }

    // ── Imagens / captura de tela assistida ────────────────────────────────

    /// <summary>
    /// Decide se imagens de captura de tela podem ser enviadas ao LLM.
    ///
    ///   - setting desligado → nunca envia (privacidade/custo);
    ///   - modelo sem informação no catálogo → mantém o comportamento (envia);
    ///   - modelo com capacidades declaradas e SEM "vision" → não envia
    ///     (um payload image_url geraria 400 no provedor).
    /// </summary>
    public static bool ResolveScreenshotImagesAllowed(bool settingEnabled, AiModelInfo? modelInfo)
    {
        if (!settingEnabled) return false;
        if (modelInfo?.Capabilities is not { Count: > 0 } capabilities) return true;
        return capabilities.Any(c =>
            string.Equals(c, "vision", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c, "image", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Máximo de imagens aceitas em uma única mensagem.</summary>
    public const int MaxImagesPerMessage = 3;

    /// <summary>
    /// Teto do payload base64 de cada imagem (≈3 MB binários). Prints maiores
    /// são descartados pelo agente antes do envio; aqui é a última defesa.
    /// </summary>
    // 6 MiB: as capturas agora saem em PNG lossless até ~2,9 MB (texto legível),
    // o que dá ~3,9 MB em base64. Alinhado ao teto do agent.
    public const int MaxImageBase64Chars = 6 * 1024 * 1024;

    /// <summary>
    /// Extrai partes multimodais do resultado JSON de uma tool de captura de
    /// tela. Contrato do agent: {"image_base64":"...","mime":"image/png",
    /// "note":"..."}. Retorna null quando não há imagem válida (o tool result
    /// textual continua sendo a única informação, compatível com modelos sem
    /// visão).
    /// </summary>
    public static List<Discovery.Core.Interfaces.LlmContentPart>? BuildImagePartsFromToolResult(
        string toolResult, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolResult)) return null;
        if (!toolResult.Contains("image_base64", StringComparison.Ordinal)) return null;
        var trimmed = toolResult.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("image_base64", out var b64) || b64.ValueKind != JsonValueKind.String) return null;
            var data = b64.GetString();
            if (string.IsNullOrEmpty(data) || data.Length > MaxImageBase64Chars) return null;
            var mime = root.TryGetProperty("mime", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : "image/png";
            var note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;
            var text = string.IsNullOrWhiteSpace(note)
                ? $"Imagem capturada pela ferramenta {toolName}."
                : note!;
            return new List<Discovery.Core.Interfaces.LlmContentPart>
            {
                new("text", Text: text),
                new("image_url", ImageUrl: $"data:{mime};base64,{data}")
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Remove o base64 da imagem de um tool result antes de persistir no
    /// histórico do chat: o LLM já recebeu a imagem na mensagem multimodal do
    /// round atual e guardar MBs de base64 em ai_chat_messages infla a tabela e
    /// não agrega contexto nas rodadas seguintes. Metadados (mime, note, window)
    /// são preservados e um marcador documenta a omissão.
    /// </summary>
    public static string CompactToolResultForPersistence(string toolResult)
    {
        if (string.IsNullOrEmpty(toolResult) || !toolResult.Contains("\"image_base64\"", StringComparison.Ordinal))
        {
            return toolResult;
        }
        try
        {
            if (JsonNode.Parse(toolResult) is not JsonObject obj) return toolResult;
            if (obj["image_base64"] is not JsonValue value) return toolResult;
            if (!value.TryGetValue<string>(out var raw)) return toolResult;
            // Sem </>: o encoder padrão do System.Text.Json escaparia os
            // delimitadores e o marcador ficaria ilegível no histórico.
            obj["image_base64"] = $"[omitido: {raw.Length} chars base64]";
            obj["image_omitted"] = true;
            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return toolResult;
        }
    }

    /// <summary>
    /// Converte data URLs anexadas pelo usuário (campo "images" do request de
    /// chat) em partes multimodais. Ignora entradas inválidas/grandes.
    /// </summary>
    public static List<Discovery.Core.Interfaces.LlmContentPart>? BuildImagePartsFromDataUrls(
        IEnumerable<string>? images)
    {
        if (images is null) return null;
        var parts = new List<Discovery.Core.Interfaces.LlmContentPart>();
        foreach (var image in images)
        {
            if (parts.Count >= MaxImagesPerMessage) break;
            var value = image?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > MaxImageBase64Chars) continue;
            if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) continue;
            parts.Add(new Discovery.Core.Interfaces.LlmContentPart("image_url", ImageUrl: value));
        }
        return parts.Count > 0 ? parts : null;
    }
}
