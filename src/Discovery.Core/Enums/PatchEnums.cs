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

/// <summary>Tipo de mudança registrada na auditoria de aprovações de patch.</summary>
public enum PatchApprovalAuditChangeType
{
    Created = 0,
    Updated = 1,
    Deleted = 2
}
