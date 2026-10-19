namespace Discovery.Core.Entities;

/// <summary>Metadados do último scan de Windows Update de um agente.</summary>
public class AgentWindowsUpdateSnapshot
{
    public Guid AgentId { get; set; }
    public DateTime? CollectedAt { get; set; }
    public string ScanStatus { get; set; } = "ok";
    public int? ScanDurationMs { get; set; }
    public string? ScanError { get; set; }
    public int PendingCount { get; set; }
    public int InstalledCount { get; set; }
    public int DeniedCount { get; set; }
    public bool PendingReboot { get; set; }
    public string? PendingRebootReasons { get; set; }
    public DateTime? RebootCheckedAt { get; set; }

    // ── Sonda de estado do serviço Windows Update (bloco opcional "state") ──
    // Colunas próprias (migration M238) e não um blob JSON: o suporte precisa
    // filtrar por estado de serviço e a UI lê campos tipados.

    /// <summary>Estado do serviço wuauserv: running | stopped | disabled | unknown.</summary>
    public string? ServiceState { get; set; }
    /// <summary>O último scan WUA conseguiu falar com o Windows Update Online?</summary>
    public bool? WuOnline { get; set; }
    /// <summary>GPO/WSUS gerenciando o Windows Update da máquina.</summary>
    public bool? PolicyManaged { get; set; }
    /// <summary>Results\Install\LastSuccessTime (última instalação bem-sucedida).</summary>
    public DateTime? LastInstallSuccessAt { get; set; }
    /// <summary>Download em andamento segundo a sonda leve do agente.</summary>
    public bool? DownloadInProgress { get; set; }
    /// <summary>Hash/assinatura da sonda no momento do scan (mudou = novo scan).</summary>
    public string? StateHash { get; set; }
    /// <summary>Quando a assinatura da sonda mudou pela última vez.</summary>
    public DateTime? StateChangedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}