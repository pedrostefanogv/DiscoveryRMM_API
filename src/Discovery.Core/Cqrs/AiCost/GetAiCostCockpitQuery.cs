using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AiCost;

/// <summary>
/// Painel de custo de IA por cliente. Default: últimos 30 dias.
/// Fonte real de persistência: ai_chat_messages.tokens_used (M044),
/// com escopo por ai_chat_sessions.client_id.
/// </summary>
public sealed record GetAiCostCockpitQuery(
    Guid? ClientId = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null
) : IQuery<Result<AiCostCockpitDto>>;

public sealed record AiCostCockpitTotalsDto(
    int Requests,
    long InputTokens,
    long OutputTokens,
    double CostUsd);

public sealed record AiCostClientDto(
    Guid ClientId,
    string ClientName,
    int Requests,
    long InputTokens,
    long OutputTokens,
    double CostUsd,
    double? BudgetUsdMonthly);

public sealed record AiCostToolDto(
    string Tool,
    int Calls,
    double CostUsd);

public sealed record AiCostCockpitDto(
    DateTime FromUtc,
    DateTime ToUtc,
    AiCostCockpitTotalsDto Totals,
    IReadOnlyList<AiCostClientDto> ByClient,
    IReadOnlyList<AiCostToolDto> TopTools);
