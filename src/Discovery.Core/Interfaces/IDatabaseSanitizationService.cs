using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Chaves e descritores das verificações de sanitização do banco. As chaves são
/// o contrato estável entre o serviço, o endpoint e a tela de manutenção.
/// </summary>
public static class DatabaseSanitizationChecks
{
    /// <summary>Chamados com perfil de workflow inexistente.</summary>
    public const string OrphanWorkflowProfile = "ticket-orphan-workflow-profile";

    /// <summary>Chamados sem estado (Guid.Empty) ou com estado inválido/de outro cliente.</summary>
    public const string InvalidWorkflowState = "ticket-invalid-workflow-state";

    /// <summary>Chamados com departamento inexistente.</summary>
    public const string OrphanDepartment = "ticket-orphan-department";

    /// <summary>Chamados abertos com departamento e sem perfil de workflow.</summary>
    public const string MissingWorkflowProfile = "ticket-missing-workflow-profile";

    /// <summary>Somente relatório: coerência entre estado (final) e fechamento.</summary>
    public const string StateConsistency = "ticket-state-consistency";

    public static readonly IReadOnlyList<SanitizationCheckDescriptor> All =
    [
        new(InvalidWorkflowState, "Chamados sem estado ou com estado inválido",
            "Aplica o estado inicial do cliente em chamados com estado vazio, inexistente ou pertencente a outro cliente."),
        new(MissingWorkflowProfile, "Chamados abertos sem perfil de workflow",
            "Aplica o perfil/SLA padrão do departamento em chamados abertos que estão sem perfil."),
        new(OrphanWorkflowProfile, "Chamados com perfil de workflow inexistente",
            "Remove a referência a perfis que não existem mais e limpa as expirações de SLA."),
        new(OrphanDepartment, "Chamados com departamento inexistente",
            "Remove a referência a departamentos que não existem mais."),
        new(StateConsistency, "Coerência entre estado e fechamento",
            "Somente relatório: chamados fechados em estado não-final e abertos em estado final."),
    ];
}

/// <summary>Descrição de uma verificação disponível (consumida pela tela).</summary>
public sealed record SanitizationCheckDescriptor(string Key, string Title, string Description);

/// <summary>Resultado de uma verificação. Em dry-run, Fixed fica 0 e Samples lista o que mudaria.</summary>
public sealed record SanitizationCheckResult(
    string Key,
    string Title,
    int Scanned,
    int Fixed,
    int Skipped,
    string? Error,
    IReadOnlyList<string> Samples);

/// <summary>Relatório agregado de uma execução de sanitização.</summary>
public sealed record SanitizationReport(
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    bool DryRun,
    int TotalScanned,
    int TotalFixed,
    IReadOnlyList<SanitizationCheckResult> Checks);

/// <summary>
/// Parâmetros de execução. <c>Checks</c> nulo/vazio roda todas as verificações.
/// </summary>
/// <summary>
/// Parâmetros de execução. <c>Checks</c> nulo/vazio roda todas as verificações.
/// <c>DryRun</c> é nullable de propósito: ausente = dry-run, para que o endpoint
/// nunca aplique correções só porque o corpo veio vazio ou sem a flag.
/// </summary>
public sealed record SanitizationRequest(bool? DryRun, IReadOnlyList<string>? Checks);

/// <summary>
/// Sanitização do banco: detecta e corrige inconsistências de dados que impedem
/// a operação normal (ex.: chamado sem estado de workflow). Idempotente — só
/// toca registros realmente inconsistentes.
/// </summary>
public interface IDatabaseSanitizationService
{
    /// <summary>
    /// Executa as verificações selecionadas. Com <paramref name="dryRun"/> = true
    /// nada é persistido nem auditado (apenas pré-visualização).
    /// </summary>
    Task<SanitizationReport> RunAsync(
        bool dryRun, IReadOnlyList<string>? checks, CancellationToken ct = default);
}
