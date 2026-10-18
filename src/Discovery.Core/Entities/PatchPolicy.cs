using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Política de patches por escopo (Global/Client/Site). Uma linha por escopo.
/// Ausência = defaults conservadores (Monitor, sem auto-instalação, reboot Never).
/// </summary>
public class PatchPolicy
{
    public Guid Id { get; set; }
    public PatchApprovalScopeType ScopeType { get; set; } = PatchApprovalScopeType.Global;
    public Guid? ScopeId { get; set; }
    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }

    public PatchEnforcementMode EnforcementMode { get; set; } = PatchEnforcementMode.Monitor;

    /// <summary>Classificações auto-aprovadas (JSON array de strings).</summary>
    public string? AutoApproveClassifications { get; set; }
    /// <summary>Severidades auto-aprovadas (JSON array de strings).</summary>
    public string? AutoApproveSeverities { get; set; }

    /// <summary>Habilita instalação automática dentro da janela de manutenção.</summary>
    public bool AutoInstallEnabled { get; set; }
    /// <summary>Janela: {"days":[1..7],"start":"22:00","end":"05:00","timezone":"..."}.</summary>
    public string? MaintenanceWindowJson { get; set; }

    public PatchRebootPolicy RebootPolicy { get; set; } = PatchRebootPolicy.Never;
    /// <summary>Só autoriza reboot na janela quando explicitamente habilitado.</summary>
    public bool RebootAllowed { get; set; }

    public int MaxInstallsPerRun { get; set; } = 50;
    /// <summary>Oculta KBs recusados no WU (só em OsEnforced).</summary>
    public bool HideDeniedUpdates { get; set; }

    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
