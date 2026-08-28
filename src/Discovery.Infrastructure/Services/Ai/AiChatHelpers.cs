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
