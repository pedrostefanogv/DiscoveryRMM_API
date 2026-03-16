namespace Meduza.Core.ValueObjects;

/// <summary>
/// Campos de <see cref="AIIntegrationSettings"/> que podem ser sobrescritos em nível de Client ou Site.
///
/// Campos globais — definidos exclusivamente no servidor global e herdados sem possibilidade de
/// sobrescrita — estão ausentes nesta classe:
///   - ApiKey, BaseUrl, Provider
///   - EmbeddingModel, EmbeddingEnabled, EmbeddingArticlesEnabled
///     (o espaço vetorial deve ser único; modelos diferentes geram vetores incompatíveis)
///   - MSPServers, TimeoutMs, RateLimitPerMinute, TokenBudgetDaily, CostControlEnabled
///
/// Semântica: null = herdar do nível superior; valor explícito = sobrescrever.
/// Salvo em AIIntegrationSettingsJson de ClientConfiguration e SiteConfiguration.
/// </summary>
public class AIIntegrationSettingsOverride
{
    /// <summary>Habilita recursos de IA neste escopo (null = herda servidor)</summary>
    public bool? Enabled { get; set; }

    /// <summary>Habilita Chat IA para este escopo (null = herda)</summary>
    public bool? ChatAIEnabled { get; set; }

    /// <summary>Habilita Base de Conhecimento neste escopo (null = herda)</summary>
    public bool? KnowledgeBaseEnabled { get; set; }

    /// <summary>Modelo de chat específico para este escopo (null = herda modelo global)</summary>
    public string? ChatModel { get; set; }

    /// <summary>Prompt template customizado para este escopo (null = herda)</summary>
    public string? PromptTemplate { get; set; }

    /// <summary>Temperatura de geração 0.0–2.0 (null = herda)</summary>
    public double? Temperature { get; set; }

    /// <summary>Máximo de tokens por requisição (null = herda)</summary>
    public int? MaxTokensPerRequest { get; set; }

    /// <summary>Máximo de mensagens de histórico enviadas ao LLM (null = herda)</summary>
    public int? MaxHistoryMessages { get; set; }

    /// <summary>Máximo de tokens de contexto da KB no system prompt (null = herda)</summary>
    public int? MaxKbContextTokens { get; set; }

    /// <summary>Número máximo de chunks injetados via RAG, 1–10 (null = herda)</summary>
    public int? MaxKbChunks { get; set; }

    /// <summary>Score mínimo de similaridade para incluir chunk 0.0–1.0 (null = herda)</summary>
    public double? MinSimilarityScore { get; set; }
}
