using System.Text.Json;
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
        => settings.MaxTokensPerRequest is >= 100 and <= 8000 ? settings.MaxTokensPerRequest : AiChatConstants.DefaultMaxTokens;

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
        "Responda com seu conhecimento próprio. NÃO faça novas buscas na base de conhecimento.";

    /// <summary>Injetada quando a execução de tool no agent expira (round pendente).</summary>
    public const string AgentRoundExpiredNote =
        "[SISTEMA] A execução da ferramenta no agent expirou (sem resposta no prazo). " +
        "Informe o usuário que a ação não pôde ser concluída no momento, explique o que foi possível apurar " +
        "e sugira alternativas (ex.: tentar novamente mais tarde).";

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
        if (ms <= 0) return 0;
        return Math.Max(ms, 60_000);
    }
}
