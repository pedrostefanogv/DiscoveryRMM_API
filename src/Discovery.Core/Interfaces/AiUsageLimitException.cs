namespace Discovery.Core.Interfaces;

/// <summary>Motivo do bloqueio de uso de IA.</summary>
public static class AiUsageLimitReason
{
    public const string RateLimit = "rate_limit";
    public const string Budget = "budget";

    /// <summary>
    /// Bloqueio reportado pelo cost control sem distinguir a causa: o serviço
    /// devolve apenas permitido/negado (rate limit OU budget diário).
    /// </summary>
    public const string RateOrBudget = "rate_limit_or_budget";
}

/// <summary>
/// Lançada quando o rate limit ou o budget diário de IA bloqueia a chamada.
/// É distinta de falha do provedor: os chamadores devem degradar para o
/// fallback determinístico e registrar o motivo (não é erro 500).
/// </summary>
public sealed class AiUsageLimitException(string reason, string message) : InvalidOperationException(message)
{
    public string Reason { get; } = reason;
}
