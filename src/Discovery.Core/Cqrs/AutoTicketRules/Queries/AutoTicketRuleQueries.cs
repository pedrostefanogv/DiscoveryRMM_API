using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutoTicketRules.Commands;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AutoTicketRules.Queries;

public sealed record ListAutoTicketRulesQuery(string? ScopeLevel, Guid? ScopeId, bool? IsEnabled) : IQuery<Result<IReadOnlyList<AutoTicketRuleDto>>>;
public sealed record GetAutoTicketRuleByIdQuery(Guid Id) : IQuery<Result<AutoTicketRuleDto>>;

/// <summary>
/// Simulação (dry-run) que NÃO grava nada: resolve a política, roda o motor de
/// regras e o avaliador de sinal, e devolve o que aconteceria.
/// Quando <see cref="RuleId"/> é nulo, avalia contra todas as regras (simulate).
/// </summary>
public sealed record AutoTicketRuleDryRunQuery(
    Guid? RuleId,
    AutoTicketRuleDryRunRequest Request) : IQuery<Result<AutoTicketRuleDryRunResponse>>;

/// <summary>
/// Estatísticas de decisão de uma regra em uma janela (dias). Reaproveita
/// GetRuleStatsAsync, que já existia no repositório sem exposição.
/// </summary>
public sealed record AutoTicketRuleStatsQuery(
    Guid RuleId,
    int PeriodHours) : IQuery<Result<AutoTicketRuleStatsResponse>>;

/// <summary>
/// Semeia um conjunto conservador de regras padrão para um cliente.
/// Idempotente: rodar duas vezes não duplica.
/// </summary>
public sealed record SeedDefaultAutoTicketRulesCommand(
    SeedDefaultAutoTicketRulesRequest Request) : ICommand<Result<SeedDefaultAutoTicketRulesResponse>>;

/// <summary>
/// Lê a política de auto-ticket de um nível (blob cru) e a política EFETIVA
/// resolvida para o escopo, com a proveniência de cada campo.
/// </summary>
public sealed record GetAutoTicketPolicyQuery(
    Guid? ClientId,
    Guid? SiteId) : IQuery<Result<AutoTicketPolicyDto>>;

/// <summary>
/// Grava a política de um nível específico (Server/Client/Site).
/// </summary>
public sealed record SaveAutoTicketPolicyCommand(
    string Level,
    Guid? TargetId,
    Discovery.Core.Configuration.AutoTicketPolicy Policy) : ICommand<Result<AutoTicketPolicyDto>>;