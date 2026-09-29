using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Agents.Automation.Commands;

/// <summary>
/// Operações de automação aplicadas a um ESCOPO (cliente ou site inteiro):
/// dispara um comando por agente elegível e registra uma execução individual no
/// histórico de operações. Exatamente um entre <c>ClientId</c> e <c>SiteId</c>
/// deve ser informado.
///
/// Elegibilidade: agentes não excluídos e fora de manutenção. Agentes ONLINE
/// recebem o comando imediatamente; agentes OFFLINE têm o comando PERSISTIDO e
/// são contabilizados em <c>Queued</c> — a reentrega periódica do servidor
/// (PendingCommandRedeliveryService) entrega quando eles reconectam, e o agente
/// deduplica por CommandId para nunca executar duas vezes.
///
/// Se a reentrega estiver desativada por configuração, agentes offline voltam a
/// ser contabilizados em <c>SkippedOffline</c> (não criamos comando órfão).
/// </summary>
public sealed record RunAutomationTaskForScopeCommand(
    Guid TaskId,
    Guid? ClientId,
    Guid? SiteId,
    string? CorrelationId = null) : ICommand<Result<AutomationScopeDispatchResultDto>>;

public sealed record RunAutomationScriptForScopeCommand(
    Guid ScriptId,
    Guid? ClientId,
    Guid? SiteId,
    string? CorrelationId = null) : ICommand<Result<AutomationScopeDispatchResultDto>>;

public sealed record ForceAutomationSyncForScopeCommand(
    Guid? ClientId,
    Guid? SiteId,
    bool? Policies = null,
    bool? Inventory = null,
    bool? Software = null,
    bool? AppStore = null,
    string? CorrelationId = null) : ICommand<Result<AutomationScopeDispatchResultDto>>;

/// <summary>Valores de <c>Status</c> por agente no resultado de um lote.</summary>
public static class AutomationScopeDispatchStatus
{
    public const string Dispatched = "dispatched";
    /// <summary>Comando persistido para agente offline; entrega na reconexão.</summary>
    public const string Queued = "queued";
    public const string Failed = "failed";
    public const string SkippedOffline = "skipped-offline";
    public const string SkippedMaintenance = "skipped-maintenance";
}

public sealed record AutomationScopeDispatchItemDto(
    Guid AgentId,
    string Hostname,
    string Status,
    string? Error);

/// <summary>
/// Resultado do disparo em massa: tamanho do escopo, quantos comandos saíram,
/// quantos ficaram na fila para reconexão e por que os demais foram ignorados
/// ou falharam.
/// </summary>
public sealed record AutomationScopeDispatchResultDto(
    string Scope,
    Guid ScopeId,
    string CorrelationId,
    int TotalAgents,
    int EligibleAgents,
    int Dispatched,
    int Queued,
    int Failed,
    int SkippedOffline,
    int SkippedMaintenance,
    IReadOnlyList<AutomationScopeDispatchItemDto> Items);
