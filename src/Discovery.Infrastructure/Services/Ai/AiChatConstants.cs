namespace Discovery.Infrastructure.Services;

/// <summary>
/// Constantes e defaults do AiChatService.
/// Centralizadas para evitar duplicação e facilitar tuning.
/// </summary>
internal static class AiChatConstants
{
    public const int MaxMessageSizeBytes = 4096; // 4KB — permite colagem de logs e scripts para análise
    public const int SessionExpirationDays = 180;
    // Orçamento de iterações do agent loop (tool call rounds). Default 10:
    // loops curtos demais deixavam respostas cortadas exigindo "continue" manual.
    public const int DefaultMaxToolCallIterations = 10;
    public const int MaxToolCallIterationsLimit = 20;
    // Tentativas da chamada final de síntese (sem tools) quando o loop esgota
    // o orçamento ou o conteúdo vem vazio.
    public const int MaxSynthesisRetries = 2;
    // TTL do registro de round pendente (agent tool call delegada) — se o agent
    // não devolver ToolResults dentro desse prazo, o próximo multi-round injeta
    // nota de expiração e conclui com resposta.
    public static readonly TimeSpan PendingRoundTtl = TimeSpan.FromSeconds(120);
    public const int DefaultMaxHistoryMessages = 20;
    public const int DefaultMaxKbContextTokens = 2000;
    public const int DefaultMaxTokens = 2048; // ~1500 palavras — evita corte em scripts, explicações longas e respostas técnicas
    public const double DefaultTemperature = 0.3; // Determinístico para tool calling, comandos PowerShell e citações precisas

    // Cache de tools registradas por agent (para multi-round com tools do agent)
    // TTL de 4h: balanceia cache contra mudanças de permissão/ferramentas sem necessidade de restart
    public static readonly TimeSpan AgentToolsCacheTtl = TimeSpan.FromHours(4);

    // Cache de contexto RAG por mensagem — evita chamada de embedding redundante para perguntas idênticas.
    public static readonly TimeSpan RagCacheTtl = TimeSpan.FromMinutes(5);
}
