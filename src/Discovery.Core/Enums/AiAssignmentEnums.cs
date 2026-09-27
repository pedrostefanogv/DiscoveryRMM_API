namespace Discovery.Core.Enums;

/// <summary>Modo de operação da triagem por IA do departamento.</summary>
public enum AiAssignmentMode
{
    /// <summary>A IA sugere o responsável; um humano confirma a atribuição.</summary>
    Suggest = 0,

    /// <summary>A IA atribui o responsável automaticamente.</summary>
    AutoAssign = 1
}

/// <summary>
/// Modo de aprendizado do departamento para competências e recalibração de pesos.
/// </summary>
public enum AiLearningMode
{
    /// <summary>Não calcula nem sugere nada.</summary>
    Off = 0,

    /// <summary>Gera sugestões que o gestor aprova ou descarta (default).</summary>
    Suggest = 1,

    /// <summary>Aplica automaticamente, respeitando os limites configurados.</summary>
    Auto = 2
}

/// <summary>Origem da decisão registrada em ticket_assignment_decisions.strategy_source.</summary>
public static class AiAssignmentDecisionSource
{
    /// <summary>A IA escolheu e a escolha foi validada.</summary>
    public const string Ai = "ai";

    /// <summary>A IA respondeu algo inválido/ambíguo; o topo do score determinístico foi usado.</summary>
    public const string FallbackScore = "fallback_score";

    /// <summary>IA indisponível/erro; usada a estratégia determinística de fallback.</summary>
    public const string FallbackStrategy = "fallback_strategy";

    /// <summary>Integração de IA desabilitada ou sem credencial.</summary>
    public const string AiUnavailable = "ai_unavailable";

    /// <summary>Departamento sem candidatos elegíveis.</summary>
    public const string NoCandidates = "no_candidates";

    /// <summary>Falha inesperada durante a triagem.</summary>
    public const string Error = "error";

    /// <summary>
    /// Rate limit ou budget diário de IA atingido. Não é falha do provedor: a
    /// triagem degrada para o fallback e o motivo fica auditável.
    /// </summary>
    public const string AiBudgetExceeded = "ai_budget_exceeded";
}

/// <summary>Estados da fila de triagem por IA.</summary>
public static class AiAssignmentQueueStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}
