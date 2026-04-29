namespace Discovery.Core.ValueObjects;

/// <summary>
/// Configurações de integração com IA e servidores MSP.
/// </summary>
public class AIIntegrationSettings
{
    /// <summary>Habilita recursos de IA (chat, análise, etc)</summary>
    public bool Enabled { get; set; } = false;
    
    /// <summary>Habilita Chat IA para usuários</summary>
    public bool ChatAIEnabled { get; set; } = false;
    
    /// <summary>Habilita Base de Conhecimento (assistido por IA)</summary>
    public bool KnowledgeBaseEnabled { get; set; } = false;
    
    /// <summary>Lista de servidores MSP para processamento de IA</summary>
    public string[] MSPServers { get; set; } = [];
    
    /// <summary>Timeout para chamadas de IA (milissegundos)</summary>
    public int TimeoutMs { get; set; } = 30000; // 30s
    
    /// <summary>Máximo de tokens por requisição</summary>
    public int MaxTokensPerRequest { get; set; } = 2000;

    /// <summary>Provedor de IA (ex: openai, azure-openai, anthropic)</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>API key do provedor (somente gravação via API; nunca deve ser exposta em respostas)</summary>
    public string? ApiKey { get; set; }

    /// <summary>URL base da API do provedor (opcional)</summary>
    public string? BaseUrl { get; set; } = "https://api.openai.com/v1/";

    /// <summary>Modelo de chat (ex: gpt-4o-mini)</summary>
    public string? ChatModel { get; set; } = "gpt-4-turbo";

    /// <summary>Modelo de embedding (ex: text-embedding-3-small)</summary>
    public string? EmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>Número de dimensões do vetor de embedding. Deve corresponder ao modelo escolhido. Alterar invalida todos os embeddings armazenados.</summary>
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>URL base exclusiva para o endpoint de embeddings. Se nulo, usa BaseUrl. Útil quando chat e embeddings usam provedores diferentes (ex: OpenRouter para chat + OpenAI para embeddings).</summary>
    public string? EmbeddingBaseUrl { get; set; }

    /// <summary>API key exclusiva para o endpoint de embeddings. Se nulo, usa ApiKey.</summary>
    public string? EmbeddingApiKey { get; set; }

    /// <summary>Prompt base configurável para o assistente</summary>
    public string? PromptTemplate { get; set; }

    /// <summary>Temperatura de geração para respostas</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>Habilita pipeline de embeddings</summary>
    public bool EmbeddingEnabled { get; set; } = true;

    /// <summary>Habilita embeddings da base de artigos</summary>
    public bool EmbeddingArticlesEnabled { get; set; } = true;

    /// <summary>Máximo de mensagens de histórico enviadas ao LLM</summary>
    public int MaxHistoryMessages { get; set; } = 10;

    /// <summary>Máximo de tokens usados como contexto da KB no prompt</summary>
    public int MaxKbContextTokens { get; set; } = 2000;

    /// <summary>Limite de requests por minuto para controle de custo</summary>
    public int RateLimitPerMinute { get; set; } = 60;

    /// <summary>Orçamento diário de tokens por escopo</summary>
    public int TokenBudgetDaily { get; set; } = 200000;

    /// <summary>Habilita enforcement de controles de custo</summary>
    public bool CostControlEnabled { get; set; } = false;

    /// <summary>Score mínimo de similaridade (0.0–1.0) para incluir chunk no contexto RAG. Chunks abaixo são descartados.</summary>
    public double MinSimilarityScore { get; set; } = 0.65;

    /// <summary>Número máximo de chunks da KB injetados no system prompt via RAG (1–10)</summary>
    public int MaxKbChunks { get; set; } = 3;

    // --- OpenRouter ---

    /// <summary>Header HTTP-Referer para OpenRouter (URL do site/app)</summary>
    public string? OpenRouterReferer { get; set; }

    /// <summary>Header X-Title para OpenRouter (nome do app)</summary>
    public string? OpenRouterTitle { get; set; }

    /// <summary>Header X-Categories para OpenRouter (categorias separadas por vírgula, ex: "rmm,monitoring")</summary>
    public string? OpenRouterCategories { get; set; }

    /// <summary>TTL em minutos do cache de catálogo de modelos (0 = desabilitado, padrão 60)</summary>
    public int ModelCatalogCacheMinutes { get; set; } = 60;

    /// <summary>Permite fallback automático entre providers configurados</summary>
    public bool AllowProviderFallbacks { get; set; } = false;

    // --- Constantes de provider ---

    public const string ProviderOpenAi = "openai";
    public const string ProviderOpenRouter = "openrouter";
    public const string ProviderOpenAiCompatible = "openai-compatible";

    public const string OpenRouterDefaultBaseUrl = "https://openrouter.ai/api/v1/";
    public const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1/";

    /// <summary>Retorna a BaseUrl padrão conforme o provider configurado</summary>
    public string? ResolveDefaultBaseUrl() => Provider?.ToLowerInvariant() switch
    {
        ProviderOpenRouter => OpenRouterDefaultBaseUrl,
        ProviderOpenAiCompatible => null, // genérico: sem default fixo, usuário deve informar
        _ => OpenAiDefaultBaseUrl
    };

    /// <summary>Indica se o provider atual é OpenRouter</summary>
    public bool IsOpenRouter() => string.Equals(Provider, ProviderOpenRouter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Indica se o provider atual é compatível com OpenAI (inclui OpenAI direto, OpenRouter e genérico)</summary>
    public bool IsOpenAiCompatible() => Provider?.ToLowerInvariant() switch
    {
        ProviderOpenAi or ProviderOpenRouter or ProviderOpenAiCompatible => true,
        _ => false
    };
}
