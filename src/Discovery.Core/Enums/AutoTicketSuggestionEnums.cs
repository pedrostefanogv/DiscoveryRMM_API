namespace Discovery.Core.Enums;

/// <summary>Campos do chamado que a IA pode sugerir.</summary>
public enum AutoTicketSuggestionField
{
    Title = 0,
    Description = 1,
    Priority = 2,
    Department = 3,
    Category = 4
}

/// <summary>
/// Origem da sugestão, para auditoria — espelha AiAssignmentDecisionSource, que
/// o produto já usa na triagem de atribuição. Não inventamos um segundo
/// vocabulário.
/// </summary>
public static class AutoTicketSuggestionSource
{
    /// <summary>A IA respondeu e a resposta passou na validação.</summary>
    public const string Ai = "ai";

    /// <summary>A IA respondeu algo inválido/ambíguo; usado o determinístico.</summary>
    public const string Fallback = "fallback";

    /// <summary>IA indisponível, desligada ou sem credencial.</summary>
    public const string AiUnavailable = "ai_unavailable";

    /// <summary>Timeout da chamada ao provedor.</summary>
    public const string Timeout = "timeout";

    /// <summary>Circuito aberto após falhas consecutivas.</summary>
    public const string CircuitOpen = "circuit_open";

    /// <summary>Orçamento de tokens esgotado.</summary>
    public const string BudgetExceeded = "ai_budget_exceeded";
}
