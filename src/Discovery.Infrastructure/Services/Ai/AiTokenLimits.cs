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

    /// <summary>
    /// Fração da janela de contexto reservada ao PROMPT de entrada. Documenta a
    /// conta de <see cref="ResolveMaxPromptChars"/>: prompt + saída precisam
    /// caber no contexto, então o teto de prompt é DERIVADO do contexto real do
    /// modelo, não fixo.
    /// </summary>
    public const double PromptContextFraction = 0.9;

    /// <summary>
    /// Teto ABSOLUTO de caracteres do prompt — apenas uma trava de sanidade
    /// contra configuração absurda (contexto gigante × fração), NÃO o limite que
    /// dimensiona o prompt no dia a dia. Era 60.000 FIXO, o que cortava o prompt
    /// de qualquer modelo com contexto grande: num modelo de 1.048.576 tokens o
    /// teto derivado passa de 3 milhões de caracteres, e o valor fixo era o elo
    /// que impedia o prompt de acompanhar o modelo.
    ///
    /// 4.000.000 de caracteres ≈ 1,14M tokens a 3,5 chars/token, acima do maior
    /// contexto medido no catálogo (1.048.576) — ou seja, na prática quem manda
    /// é o contexto do modelo, e este teto só evita um cálculo insano.
    /// </summary>
    public const int MaxPromptChars = 4_000_000;

    /// <summary>Prompt conservador quando o catálogo não informa o contexto.</summary>
    public const int DefaultPromptCharsWhenUnknownContext = 8000;

    /// <summary>
    /// Resolve o teto de SAÍDA: min(pedido, teto do departamento, capacidade do
    /// modelo, teto do produto). <paramref name="source"/> indica de onde veio o
    /// limite, para auditoria (catalog / fallback / unknown).
    ///
    /// <paramref name="requested"/> = 0 significa AUTOMÁTICO ("sem pedido
    /// explícito"): o pedido NÃO restringe nada e quem decide é a capacidade do
    /// modelo / teto do departamento / teto do produto. Por isso vira
    /// <see cref="int.MaxValue"/> no min — passar 0 cru devolveria
    /// <see cref="MinimumOutputTokens"/> (200) e o modo automático ficaria
    /// limitado a 200 tokens.
    ///
    /// <see cref="MinimumOutputTokens"/> é o piso do RESULTADO, não do pedido:
    /// nenhuma combinação devolve menos que 200.
    /// </summary>
    public static (int Cap, string Source) ResolveOutputCap(
        int requested, int? modelCap, int? departmentCap, string? modelId = null)
    {
        var department = departmentCap is > 0 ? departmentCap.Value : int.MaxValue;
        var product = MaxOutputTokensCeiling;

        // 0 (ou negativo) = AUTOMÁTICO: o pedido não restringe. int.MaxValue é o
        // elemento neutro do Math.Min abaixo.
        var requestedLimit = requested > 0 ? requested : int.MaxValue;

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

        var cap = Math.Min(Math.Min(requestedLimit, department), Math.Min(modelLimit, product));
        return (Math.Max(MinimumOutputTokens, cap), source);
    }

    /// <summary>
    /// Capacidade de saída por família de modelos, usada quando o catálogo não
    /// informa max_completion_tokens (OpenAI nativo, alias, id inválido).
    ///
    /// É apenas um FALLBACK: quando o catálogo responde (caminho normal), quem
    /// manda é o max_completion_tokens real do modelo. Os valores abaixo são,
    /// portanto, um PISO conservador — subestimar aqui não trunca o modelo no
    /// caminho normal, mas trunca se o catálogo estiver indisponível.
    ///
    /// O valor de <c>deepseek</c> estava em 32.768 pelo teto antigo do produto e
    /// subestimava o modelo de PRODUÇÃO em ~12x: a medição do catálogo OpenRouter
    /// registra deepseek/deepseek-v4.1-flash-1017 com max_completion_tokens =
    /// 393.216. Alinhado à medição para que uma indisponibilidade do catálogo não
    /// volte a truncar justamente o modelo em uso.
    /// </summary>
    public static int ResolveFallbackOutputCap(string? modelId)
    {
        var id = (modelId ?? string.Empty).Trim().ToLowerInvariant();
        if (id.Length == 0) return UnknownModelOutputCap;

        // Medido no catálogo OpenRouter: 393.216 de saída no modelo de produção.
        if (id.Contains("deepseek")) return 393_216;
        if (id.Contains("glm-5") || id.Contains("mimo")) return 32768;
        if (id.Contains("gemini-2.5") || id.Contains("gemini-3")) return 32768;
        // o1/o3/o4 (OpenAI): mesmo valor do GetOpenAiMaxCompletionTokens do catálogo.
        if (id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4")) return 32768;
        if (id.Contains("gpt-4o") || id.Contains("gpt-4.1") || id.Contains("gpt-5")) return 16384;
        if (id.Contains("claude-3.5") || id.Contains("claude-3-5")) return 8192;
        if (id.Contains("claude")) return 16384;

        return UnknownModelOutputCap;
    }

    /// <summary>
    /// Orçamento em CARACTERES para o conteúdo do prompt, DERIVADO da janela de
    /// contexto real do modelo. Contexto desconhecido usa um valor conservador em
    /// vez de arriscar estourar o limite do provedor.
    ///
    /// A conta (documentada para não virar folclore):
    ///   1. reserva 10% do contexto para overhead do provedor (PromptContextFraction);
    ///   2. subtrai a SAÍDA já reservada (prompt e saída dividem o mesmo contexto);
    ///   3. converte tokens → caracteres por CharsPerToken (3,5);
    ///   4. aplica a margem de segurança PromptSafetyMargin (0,85);
    ///   5. limita a [MinPromptChars, MaxPromptChars].
    ///
    /// Se o teto de saída for grande num modelo de contexto modesto, o passo 2
    /// deixa <c>availableTokens</c> NEGATIVO. Isso é tratado e CORRETO: cai no
    /// <see cref="MinPromptChars"/>, ou seja, o prompt fica mínimo para sobrar
    /// contexto para a resposta — em vez de estourar o provedor. O caso é
    /// visível no código (o if abaixo) e o chamador registra o orçamento.
    /// </summary>
    public static int ResolveMaxPromptChars(int? contextLength, int outputTokens)
    {
        if (contextLength is null or <= 0) return DefaultPromptCharsWhenUnknownContext;

        var reservedOutput = Math.Max(outputTokens, 0);
        var availableTokens = (int)(contextLength.Value * PromptContextFraction) - reservedOutput;
        // Saída maior que o contexto disponível: prompt no mínimo para não estourar.
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
