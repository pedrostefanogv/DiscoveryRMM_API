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
    // ── Chamados: workflow ─────────────────────────────────────────────────

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

    /// <summary>Somente relatório: chamado cujo cliente não existe mais.</summary>
    public const string OrphanClient = "ticket-orphan-client";

    /// <summary>Departamento existente, mas de outro cliente.</summary>
    public const string CrossClientDepartment = "ticket-cross-client-department";

    /// <summary>Perfil cujo departamento não é o departamento do chamado.</summary>
    public const string ProfileDepartmentMismatch = "ticket-profile-department-mismatch";

    /// <summary>Campos de SLA incoerentes (FRT sem início, hold em fechado, pausa negativa, datas invertidas).</summary>
    public const string SlaInconsistent = "ticket-sla-inconsistent";

    /// <summary>Responsável/solicitante apontando para usuário inexistente.</summary>
    public const string AssigneeOrphan = "ticket-assignee-orphan";

    /// <summary>Avaliação (rating) fora do intervalo 0..5 ou metadados sem nota.</summary>
    public const string RatingInvalid = "ticket-rating-invalid";

    // ── Workflow (estrutura) ───────────────────────────────────────────────

    /// <summary>Transições apontando para estados que não existem mais.</summary>
    public const string WorkflowTransitionOrphan = "workflow-transition-orphan-state";

    /// <summary>Somente relatório: mais de um estado inicial para o mesmo cliente.</summary>
    public const string MultipleInitialStates = "workflow-multiple-initial-states";

    /// <summary>Somente relatório: departamento ativo sem perfil ativo (chamados não ganham SLA).</summary>
    public const string DepartmentWithoutProfile = "department-without-active-profile";

    // ── Ativos e agentes ───────────────────────────────────────────────────

    /// <summary>Somente relatório: agente vinculado a um site inexistente.</summary>
    public const string AgentOrphanSite = "agent-orphan-site";

    /// <summary>Somente relatório: seguidor de chamado órfão (chamado/usuário inexistente).</summary>
    public const string TicketWatcherOrphan = "ticket-watcher-orphan";

    /// <summary>Somente relatório: anexo apontando para uma entidade-pai inexistente.</summary>
    public const string AttachmentOrphan = "attachment-orphan-parent";

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
        new(CrossClientDepartment, "Chamados com departamento de outro cliente",
            "Limpa o departamento (e o perfil/SLA herdado dele) quando pertence a outro cliente."),
        new(ProfileDepartmentMismatch, "Chamados com perfil de departamento diferente",
            "Limpa o perfil de workflow quando o departamento do perfil não é o departamento do chamado."),
        new(AssigneeOrphan, "Chamados com responsável/solicitante inexistente",
            "Remove a referência a usuários que não existem mais."),
        new(SlaInconsistent, "Campos de SLA incoerentes",
            "Corrige primeira resposta sem início, pausa negativa, hold aberto em chamado fechado e datas invertidas."),
        new(RatingInvalid, "Avaliações (rating) inválidas",
            "Limpa notas fora de 0..5 e metadados de avaliação sem nota."),
        new(StateConsistency, "Coerência entre estado e fechamento",
            "Somente relatório: chamados fechados em estado não-final e abertos em estado final.", ReportOnly: true),
        new(OrphanClient, "Chamados com cliente inexistente",
            "Somente relatório: chamado cujo cliente não existe mais (exige decisão manual de destino).", ReportOnly: true),
        new(WorkflowTransitionOrphan, "Transições de workflow órfãs",
            "Remove transições que apontam para estados de workflow inexistentes."),
        new(MultipleInitialStates, "Mais de um estado inicial por cliente",
            "Somente relatório: clientes com mais de um estado IsInitial (a abertura fica ambígua).", ReportOnly: true),
        new(DepartmentWithoutProfile, "Departamento ativo sem perfil ativo",
            "Somente relatório: departamentos ativos que não têm nenhum perfil ativo (chamados ficam sem SLA).", ReportOnly: true),
        new(AgentOrphanSite, "Agentes vinculados a site inexistente",
            "Somente relatório: agente cujo site não existe mais.", ReportOnly: true),
        new(TicketWatcherOrphan, "Seguidores de chamado órfãos",
            "Somente relatório: seguidores apontando para chamado ou usuário inexistente.", ReportOnly: true),
        new(AttachmentOrphan, "Anexos com entidade-pai inexistente",
            "Somente relatório: anexos cujo registro-pai não existe mais (exige decisão sobre o storage).", ReportOnly: true),
    ];
}

/// <summary>Descrição de uma verificação disponível (consumida pela tela).</summary>
public sealed record SanitizationCheckDescriptor(string Key, string Title, string Description, bool ReportOnly = false);

/// <summary>
/// Resultado de uma verificação.
/// <para><c>Examined</c> = registros avaliados; <c>Found</c> = inconsistências
/// encontradas; <c>Fixed</c> = corrigidas (0 em dry-run); <c>HasMore</c> indica
/// que a varredura foi truncada pelo teto por verificação.</para>
/// </summary>
public sealed record SanitizationCheckResult(
    string Key,
    string Title,
    int Examined,
    int Found,
    int Fixed,
    int Skipped,
    bool HasMore,
    string? Error,
    IReadOnlyList<string> Samples);

/// <summary>Relatório agregado de uma execução de sanitização.</summary>
public sealed record SanitizationReport(
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    bool DryRun,
    int TotalExamined,
    int TotalFound,
    int TotalFixed,
    bool Truncated,
    IReadOnlyList<SanitizationCheckResult> Checks);

/// <summary>
/// Parâmetros de execução. <c>Checks</c> nulo/vazio roda todas as verificações.
/// <c>DryRun</c> é nullable de propósito: ausente = dry-run, para que o endpoint
/// nunca aplique correções só porque o corpo veio vazio ou sem a flag.
/// </summary>
public sealed record SanitizationRequest(bool? DryRun, IReadOnlyList<string>? Checks);

/// <summary>Lançada quando outra execução de sanitização já está em andamento.</summary>
public sealed class DatabaseSanitizationBusyException() :
    InvalidOperationException("Já existe uma sanitização em andamento. Aguarde a conclusão antes de iniciar outra.");

/// <summary>
/// Sanitização do banco: detecta e corrige inconsistências de dados que impedem
/// a operação normal (ex.: chamado sem estado de workflow). Idempotente — só
/// toca registros realmente inconsistentes.
/// </summary>
public interface IDatabaseSanitizationService
{
    /// <summary>Indica se existe uma execução em andamento (guarda contra concorrência).</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Executa as verificações selecionadas. Com <paramref name="dryRun"/> = true
    /// nada é persistido nem auditado (apenas pré-visualização).
    /// </summary>
    Task<SanitizationReport> RunAsync(
        bool dryRun, IReadOnlyList<string>? checks, CancellationToken ct = default);
}
