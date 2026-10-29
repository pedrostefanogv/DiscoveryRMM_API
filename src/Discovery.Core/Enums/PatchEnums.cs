namespace Discovery.Core.Enums;

/// <summary>Escopo de uma aprovação/política de patch (do mais amplo ao mais específico).</summary>
public enum PatchApprovalScopeType
{
    Global = 0,
    Client = 1,
    Site = 2,
    Agent = 3
}

/// <summary>Ação de uma aprovação explícita de patch.</summary>
public enum PatchApprovalActionType
{
    Approve = 0,
    Deny = 1
}

/// <summary>Estado efetivo de aprovação denormalizado na linha de inventário.</summary>
public enum PatchApprovalState
{
    Unknown = 0,
    Approved = 1,
    Denied = 2,
    AutoApproved = 3
}

/// <summary>Status de uma atualização no inventário do agente.</summary>
public enum PatchUpdateStatus
{
    Pending = 0,
    Installed = 1,
    Approved = 2,
    Denied = 3,
    Installing = 4,
    Failed = 5,
    Superseded = 6,
    NotApplicable = 7
}

/// <summary>Nível de enforcement do gerenciador de patches.</summary>
public enum PatchEnforcementMode
{
    /// <summary>Só inventário; o agente não instala nada automaticamente.</summary>
    Monitor = 0,
    /// <summary>O agente instala apenas o que foi aprovado (bloqueio lógico).</summary>
    AgentEnforced = 1,
    /// <summary>Além do RMM, oculta KBs recusados e aplica política de WU (best-effort, opt-in).</summary>
    OsEnforced = 2
}

/// <summary>Política de reinício após instalação de patches.</summary>
public enum PatchRebootPolicy
{
    /// <summary>Nunca reinicia automaticamente (default).</summary>
    Never = 0,
    /// <summary>Avisa o usuário e aguarda ação do operador.</summary>
    Notify = 1,
    /// <summary>Só reinicia dentro da janela de manutenção, se habilitado.</summary>
    MaintenanceWindow = 2
}

/// <summary>
/// Ação que a POLÍTICA de reboot produz para um agente com reinício pendente.
///
/// Decisão D02 (2026-10-29): o produto NÃO reinicia máquinas automaticamente.
/// A política existe para transformar "reinício pendente" em sinal operacional
/// (notificar/escalar), nunca em reboot autônomo. Reiniciar continua sendo ação
/// explícita do operador (postAction do comando de instalação).
/// </summary>
public enum PatchRebootAction
{
    /// <summary>Nada a fazer (sem reinício pendente, ou política Never).</summary>
    None = 0,
    /// <summary>Notificar o operador: há reinício pendente que a política manda tratar.</summary>
    NotifyOperator = 1,
    /// <summary>Notificar indicando a janela de manutenção (aberta ou a próxima).</summary>
    NotifyWithWindowHint = 2
}

/// <summary>Tipo de mudança registrada na auditoria de aprovações de patch.</summary>
public enum PatchApprovalAuditChangeType
{
    Created = 0,
    Updated = 1,
    Deleted = 2
}
