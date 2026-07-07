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
    public string? ChatModel { get; set; } = "gpt-4o-mini";

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

    // ── Sampling Parameters (OpenRouter / OpenAI-compatible) ─────────────────

    /// <summary>Top P — nucleus sampling (0.0–1.0). Default: 1.0</summary>
    public double TopP { get; set; } = 1.0;

    /// <summary>Frequency penalty (-2.0–2.0). Reduz repetição baseada em frequência.</summary>
    public double FrequencyPenalty { get; set; } = 0.0;

    /// <summary>Presence penalty (-2.0–2.0). Reduz repetição de tokens já usados.</summary>
    public double PresencePenalty { get; set; } = 0.0;

    /// <summary>Seed para amostragem determinística (opcional). Mesmo seed + parâmetros = mesma resposta.</summary>
    public int? Seed { get; set; }

    // ── OpenRouter-specific features ──────────────────────────────────────────

    /// <summary>Habilita reasoning/thinking tokens em modelos compatíveis (ex: o3-mini, gemini-2.5-pro).</summary>
    public bool ReasoningEnabled { get; set; } = false;

    /// <summary>Esforço de reasoning: low, medium, high. Só aplicável se ReasoningEnabled=true.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Habilita web search nativa para modelos compatíveis (via OpenRouter).</summary>
    public bool WebSearchEnabled { get; set; } = false;

    /// <summary>Formato de resposta: null (texto livre) ou "json_object" (JSON mode).</summary>
    public string? ResponseFormat { get; set; }

    // ── Rerank (Fase 4) ──────────────────────────────────────────────────────

    /// <summary>Habilita reranking com cross-encoder (ex: cohere/rerank-v3.5) para melhorar precisão da busca.</summary>
    public bool RerankEnabled { get; set; } = false;

    /// <summary>Modelo de rerank. Default: cohere/rerank-v3.5 (via OpenRouter).</summary>
    public string? RerankModel { get; set; } = "cohere/rerank-v3.5";

    /// <summary>Quantos candidatos manter após rerank (1–10).</summary>
    public int RerankTopN { get; set; } = 3;

    // ── Chunking (Fase 5) ────────────────────────────────────────────────────

    /// <summary>Estratégia de chunking: "semantic" (headings), "paragraph", "fixed".</summary>
    public string ChunkingStrategy { get; set; } = "semantic";

    /// <summary>Tamanho alvo do chunk em tokens (200–1000).</summary>
    public int ChunkSizeTokens { get; set; } = 300;

    /// <summary>Overlap entre chunks adjacentes em tokens (0–100).</summary>
    public int ChunkOverlapTokens { get; set; } = 50;

    // ── Citações (Fase 6) ────────────────────────────────────────────────────

    /// <summary>Inclui metadados (título, categoria, tags) nos chunks enviados ao LLM.</summary>
    public bool CitationsEnabled { get; set; } = true;

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

    /// <summary>Providers de fallback em ordem de prioridade (ex: ["openrouter", "openai"]).</summary>
    public string[]? FallbackProviders { get; set; }

    /// <summary>Máximo de iterações de tool call (1-10). Default: 3.</summary>
    public int MaxToolCallIterations { get; set; } = 3;

    /// <summary>Habilita detecção de PII/secrets na saída do LLM (guardrails).</summary>
    public bool OutputGuardrailsEnabled { get; set; } = true;

    // --- Constantes de provider ---

    public const string ProviderOpenAi = "openai";
    public const string ProviderOpenRouter = "openrouter";
    public const string ProviderOpenAiCompatible = "openai-compatible";
    public const string ProviderOllama = "ollama";

    public const string OpenRouterDefaultBaseUrl = "https://openrouter.ai/api/v1/";
    public const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1/";
    public const string OllamaDefaultBaseUrl = "http://localhost:11434/v1/";

    /// <summary>Retorna a BaseUrl padrão conforme o provider configurado</summary>
    public string? ResolveDefaultBaseUrl() => Provider?.ToLowerInvariant() switch
    {
        ProviderOpenRouter => OpenRouterDefaultBaseUrl,
        ProviderOllama => OllamaDefaultBaseUrl,
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

    // ── Catálogo de modelos recomendados ────────────────────────────────────

    /// <summary>Modelos de chat recomendados por provider (provider → lista de model IDs).</summary>
    public static readonly Dictionary<string, string[]> RecommendedChatModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ollama"] = ["llama3.2", "mistral", "phi4", "gemma3", "deepseek-r1", "qwen2.5"],
        [ProviderOpenAi] = ["gpt-4o-mini", "gpt-4o", "gpt-4-turbo", "o4-mini", "o3-mini"],
        [ProviderOpenRouter] = [
            "openai/gpt-4o-mini",
            "openai/gpt-4o",
            "openai/gpt-4.1-nano",
            "anthropic/claude-3.5-haiku",
            "anthropic/claude-3.5-sonnet",
            "google/gemini-2.5-flash",
            "google/gemini-2.5-pro",
            "google/gemma-3-4b-it",
            "deepseek/deepseek-chat-v3-0324",
            "meta-llama/llama-4-maverick",
        ],
        ["anthropic"] = ["claude-3-5-haiku-20241022", "claude-3-5-sonnet-20241022"],
        ["azure-openai"] = [], // deployments customizados pelo usuário
        [ProviderOpenAiCompatible] = [], // genérico — usuário informa
    };

    /// <summary>Modelos de embedding recomendados por provider (provider → lista de model IDs).</summary>
    public static readonly Dictionary<string, string[]> RecommendedEmbeddingModels = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderOpenAi] = ["text-embedding-3-small", "text-embedding-3-large", "text-embedding-ada-002"],
        [ProviderOpenRouter] = [
            "openai/text-embedding-3-small",
            "google/text-embedding-004",
            "perplexity/pplx-embed-v1-0.6b",
        ],
        ["anthropic"] = [], // Anthropic não oferece API de embedding pública
        ["azure-openai"] = [], // deployments customizados
        [ProviderOpenAiCompatible] = [], // genérico
    };

    /// <summary>Dimensões padrão por modelo de embedding.</summary>
    public static readonly Dictionary<string, int> EmbeddingDimensionsMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text-embedding-3-small"] = 1536,
        ["text-embedding-3-large"] = 3072,
        ["text-embedding-ada-002"] = 1536,
        ["openai/text-embedding-3-small"] = 1536,
        ["google/text-embedding-004"] = 768,
        ["perplexity/pplx-embed-v1-0.6b"] = 1024,
    };
}
