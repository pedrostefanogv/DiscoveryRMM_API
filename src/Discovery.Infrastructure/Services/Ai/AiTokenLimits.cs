using Discovery.Core.DTOs;

namespace Discovery.Infrastructure.Services.Ai;

/// <summary>
/// Orçamento de tokens derivado da capacidade REAL do modelo. Substitui tetos
/// fixos que subutilizavam os modelos contratados (o produto travava em 8.000
/// tokens de saída enquanto os modelos suportam 16k a 100k+).
///
/// Funções puras (sem I/O) para permitir teste unitário do cálculo.
/// </summary>
public static class AiTokenLimits
{
    /// <summary>Teto padrão de saída da triagem quando o departamento não configura nada.</summary>
    public const int DefaultDepartmentOutputTokens = 1200;

    public const int MinimumOutputTokens = 200;

    /// <summary>Teto de segurança do produto (nunca ultrapassado).</summary>
    public const int MaxOutputTokensCeiling = AiChatConstants.MaxOutputTokensCeiling;

    /// <summary>Teto usado quando o catálogo não conhece o modelo.</summary>
    public const int UnknownModelOutputCap = AiChatConstants.UnknownModelOutputCap;

    /// <summary>Relação aproximada caracteres/token para dimensionar o prompt.</summary>
    public const double CharsPerToken = 3.5;

    /// <summary>Margem de segurança sobre o orçamento de prompt.</summary>
    public const double PromptSafetyMargin = 0.85;

    public const int MinPromptChars = 2000;
    public const int MaxPromptChars = 60000;
    public const int DefaultPromptCharsWhenUnknownContext = 8000;

    /// <summary>
    /// Resolve o teto de SAÍDA: min(pedido, teto do departamento, capacidade do
    /// modelo, teto do produto). <paramref name="source"/> indica de onde veio o
    /// limite, para auditoria (catalog / fallback / unknown).
    /// </summary>
    public static (int Cap, string Source) ResolveOutputCap(
        int requested, int? modelCap, int? departmentCap, string? modelId = null)
    {
        var department = departmentCap is > 0 ? departmentCap.Value : int.MaxValue;
        var product = MaxOutputTokensCeiling;

        int modelLimit;
        string source;
        if (modelCap is > 0)
        {
            modelLimit = modelCap.Value;
            source = "catalog";
        }
        else
        {
            modelLimit = ResolveFallbackOutputCap(modelId);
            source = string.IsNullOrWhiteSpace(modelId) ? "unknown" : "fallback";
        }

        var cap = Math.Min(Math.Min(requested, department), Math.Min(modelLimit, product));
        return (Math.Max(MinimumOutputTokens, cap), source);
    }

    /// <summary>
    /// Capacidade de saída por família de modelos, usada quando o catálogo não
    /// informa max_completion_tokens (OpenAI nativo, alias, id inválido).
    /// </summary>
    public static int ResolveFallbackOutputCap(string? modelId)
    {
        var id = (modelId ?? string.Empty).Trim().ToLowerInvariant();
        if (id.Length == 0) return UnknownModelOutputCap;

        if (id.Contains("glm-5") || id.Contains("mimo") || id.Contains("deepseek")) return 32768;
        if (id.Contains("gemini-2.5") || id.Contains("gemini-3")) return 32768;
        if (id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4")) return 32768;
        if (id.Contains("gpt-4o") || id.Contains("gpt-4.1") || id.Contains("gpt-5")) return 16384;
        if (id.Contains("claude-3.5") || id.Contains("claude-3-5")) return 8192;
        if (id.Contains("claude")) return 16384;

        return UnknownModelOutputCap;
    }

    /// <summary>
    /// Orçamento em CARACTERES para o conteúdo do prompt (descrição do chamado),
    /// derivado da janela de contexto. Contexto desconhecido usa um valor
    /// conservador em vez de arriscar estourar o limite do provedor.
    /// </summary>
    public static int ResolveMaxPromptChars(int? contextLength, int outputTokens)
    {
        if (contextLength is null or <= 0) return DefaultPromptCharsWhenUnknownContext;

        var reservedOutput = Math.Max(outputTokens, 0);
        var availableTokens = (int)(contextLength.Value * 0.9) - reservedOutput;
        if (availableTokens <= 0) return MinPromptChars;

        var chars = (int)(availableTokens * CharsPerToken * PromptSafetyMargin);
        return Math.Clamp(chars, MinPromptChars, MaxPromptChars);
    }

    /// <summary>Monta o DTO completo a partir da capacidade do modelo.</summary>
    public static AiTokenBudgetDto BuildBudget(
        AiModelInfo? model, int requested, int? departmentCap, string? modelId)
    {
        var (cap, source) = ResolveOutputCap(requested, model?.MaxCompletionTokens, departmentCap, modelId);
        return new AiTokenBudgetDto(
            modelId,
            model?.ContextLength,
            model?.MaxCompletionTokens,
            cap,
            ResolveMaxPromptChars(model?.ContextLength, cap),
            source);
    }
}
