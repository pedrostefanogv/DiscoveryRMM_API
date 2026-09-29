using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class AutomationTaskDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public AutomationTaskActionType ActionType { get; set; } = AutomationTaskActionType.RunScript;

    // Package actions
    public AppInstallationType? InstallationType { get; set; }
    public string? PackageId { get; set; }

    // Script action
    public Guid? ScriptId { get; set; }

    // Custom action
    public string? CommandPayload { get; set; }

    // Scope resolution
    public AppApprovalScopeType ScopeType { get; set; } = AppApprovalScopeType.Global;
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }

    // Target filters
    public string? IncludeTagsJson { get; set; }
    public string? ExcludeTagsJson { get; set; }

    // Trigger options
    public bool TriggerImmediate { get; set; }
    public bool TriggerRecurring { get; set; }
    public bool TriggerOnUserLogin { get; set; }
    public bool TriggerOnAgentCheckIn { get; set; }
    public string? ScheduleCron { get; set; }

    // Governance
    /// <summary>
    /// Notifica o usuário (prompt Welcome do PSADT, "Continuar"/"Adiar") antes de
    /// executar. NAO e um fluxo de aprovacao: a tarefa executa apos a confirmacao
    /// ou quando o tempo de acao padrao expirar.
    /// </summary>
    public bool RequiresApproval { get; set; }

    /// <summary>Permite que o usuario adie a execucao (botao Adiar do Welcome).</summary>
    public bool AllowDefer { get; set; } = true;

    /// <summary>Processos que devem ser fechados antes de executar (CloseProcesses), em JSON.</summary>
    public string? CloseProcessesJson { get; set; }

    /// <summary>
    /// Tempo em segundos para a acao padrao de continuar quando o usuario nao
    /// responde ao prompt. Default 60s.
    /// </summary>
    public int UserPromptTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Como notificar o usuario: Silent (nada), Prompt (Welcome PSADT com
    /// Continuar/Adiar) ou Toast (aviso informativo sem interacao).
    /// <see cref="RequiresApproval"/> e mantido derivado (= Prompt) para
    /// compatibilidade com agents/payloads antigos.
    /// </summary>
    public AutomationNotificationMode NotificationMode { get; set; } = AutomationNotificationMode.Silent;

    /// <summary>
    /// Momento do toast informativo (Before = antes de executar,
    /// After = apos a conclusao). So se aplica quando NotificationMode = Toast.
    /// </summary>
    public AutomationToastTiming ToastTiming { get; set; } = AutomationToastTiming.After;

    public bool IsActive { get; set; } = true;
    public DateTime? DeletedAt { get; set; }

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
