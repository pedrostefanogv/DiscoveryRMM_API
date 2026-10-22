namespace Discovery.Core.Interfaces;

/// <summary>
/// Custo mensal acumulado de IA por cliente (fonte: tokens persistidos em ai_chat_messages).
/// </summary>
public interface IAiMonthlyCostReader
{
    /// <summary>Custo acumulado no mês corrente (UTC). null = indisponível (fail-open).</summary>
    Task<double?> GetMonthToDateCostUsdAsync(Guid clientId, CancellationToken ct = default);
}
