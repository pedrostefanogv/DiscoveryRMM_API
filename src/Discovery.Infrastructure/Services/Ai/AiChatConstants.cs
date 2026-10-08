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
    // Mínimo configurável (tela › Configuração do servidor › Integração com IA).
    // Abaixo disso o turno quase não executa ferramentas e a IA passa o tempo
    // pedindo autorização para continuar — com 3 o valor já é útil e o
    // off-by-one antigo (que fazia 1 significar "nenhuma execução") deixa de
    // existir.
    public const int MinToolCallIterations = 3;
    // Tentativas da chamada final de síntese (sem tools) quando o loop esgota
    // o orçamento ou o conteúdo vem vazio.
    public const int MaxSynthesisRetries = 2;
    // TTL do registro de round pendente (agent tool call delegada) — se o agent
    // não devolver ToolResults dentro desse prazo, o próximo multi-round injeta
    // nota de expiração e conclui com resposta.
    public static readonly TimeSpan PendingRoundTtl = TimeSpan.FromSeconds(120);
    // TTL do contador de round por TURNO (chave turn_round:{sessionId}).
    // NÃO usar o prazo do round pendente (120s): entre dois requests do mesmo
    // turno o AGENTE executa as tools, e uma cadeia real (winget/choco) passa
    // de 2min com facilidade — com TTL curto a continuação encontrava a chave
    // expirada, voltava para o round 1 e o MaxToolCallIterations era burlado
    // justamente nas cadeias longas. O teto do loop do agente é 10min
    // (maxMultiRoundTotal); 15min cobre o turno inteiro com folga. Um turno
    // NOVO zera a contagem explicitamente (hasToolResults=false), então o
    // prazo longo não contamina o turno seguinte.
    public static readonly TimeSpan TurnRoundTtl = TimeSpan.FromMinutes(15);
    public const int DefaultMaxHistoryMessages = 20;
    public const int DefaultMaxKbContextTokens = 2000;
    public const int DefaultMaxTokens = 2048; // ~1500 palavras — evita corte em scripts, explicações longas e respostas técnicas
    // Teto de segurança do produto para tokens de SAÍDA. Os modelos contratados
    // suportam 16k–100k+ de saída; 8000 era um teto herdado que revertia
    // silenciosamente a configuração do administrador.
    public const int MaxOutputTokensCeiling = 32768;
    // Teto usado quando o catálogo não conhece o modelo (id inválido, alias auto).
    public const int UnknownModelOutputCap = 4096;
    public const double DefaultTemperature = 0.3; // Determinístico para tool calling, comandos PowerShell e citações precisas

    // ── Renovação de orçamento (autorização do usuário) ─────────────────────
    // Quando o orçamento de rounds do turno esgota com uma ação do agent
    // pendente, o orquestrador NÃO sintetiza mais uma resposta final em
    // silêncio: ele pergunta ao usuário se pode continuar e guarda as ações
    // abortadas na chave abaixo. A resposta do usuário abre um turno novo (o
    // contador de rounds volta a 1) e o orçamento é renovado com o MESMO
    // MaxToolCallIterations configurado.
    //
    // TTL generoso: o usuário pode demorar para responder à pergunta no chat.
    public static readonly TimeSpan BudgetRenewalTtl = TimeSpan.FromMinutes(30);
    public const string BudgetRenewalCachePrefix = "budget_renewal:";
    // Teto de ações guardadas para retomada (a nota de sistema não pode virar um
    // despejo de argumentos).
    public const int MaxBudgetRenewalPendingActions = 10;

    public static string BudgetRenewalKey(Guid sessionId) => $"{BudgetRenewalCachePrefix}{sessionId}";

    // Cache de tools registradas por agent (para multi-round com tools do agent)
    // TTL de 4h: balanceia cache contra mudanças de permissão/ferramentas sem necessidade de restart
    public static readonly TimeSpan AgentToolsCacheTtl = TimeSpan.FromHours(4);

    // Cache de contexto RAG por mensagem — evita chamada de embedding redundante para perguntas idênticas.
    public static readonly TimeSpan RagCacheTtl = TimeSpan.FromMinutes(5);
}
