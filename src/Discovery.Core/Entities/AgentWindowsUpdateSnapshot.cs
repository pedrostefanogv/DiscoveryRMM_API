using Discovery.Core.Enums;

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

    // ── Diagnóstico do scan (M239) ──────────────────────────────────────────
    // Campos do bloco "scan" que antes morriam no parser: código estável do
    // erro, disparo/origem e o que o scan descartou. Ausente = preserva.

    /// <summary>Código estável do erro (ex.: service_disabled) — distinto de ScanError.</summary>
    public string? ScanErrorCode { get; set; }
    /// <summary>periodic | change | command | install.</summary>
    public string? ScanTrigger { get; set; }
    /// <summary>wua | qfe.</summary>
    public string? ScanSource { get; set; }
    /// <summary>Resultados descartados pelo scan (drivers, sem identidade, ilegíveis).</summary>
    public int SkippedCount { get; set; }
    /// <summary>Motivo → quantidade em JSONB: {"driver":2,"no_identity":1}.</summary>
    public string? SkippedReasons { get; set; }

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
    /// <summary>Servidor WSUS configurado na máquina (até 500 chars).</summary>
    public string? WsusServer { get; set; }
    /// <summary>AUOptions da política de Windows Update (0–5).</summary>
    public int? AuOptions { get; set; }
    /// <summary>NoAutoUpdate=1: atualização automática desligada por política.</summary>
    public bool? NoAutoUpdate { get; set; }
    /// <summary>Deferral de quality/feature updates em dias (0–365).</summary>
    public int? DeferralDays { get; set; }
    /// <summary>Início da janela de horas ativas (0–23).</summary>
    public int? ActiveHoursStart { get; set; }
    /// <summary>Fim da janela de horas ativas (0–23).</summary>
    public int? ActiveHoursEnd { get; set; }
    /// <summary>KBs instaladas contadas no registro (CBS) pela sonda.</summary>
    public int? QfeInstalledCount { get; set; }
    /// <summary>Percentual do download em andamento (0–100).</summary>
    public int? DownloadPercent { get; set; }

    // ── Ação da política de reboot (M245, decisão D02) ─────────────────────
    // Nunca reiniciamos automaticamente: a política vira sinal operacional.

    /// <summary>Ação decidida pela política: None | NotifyOperator | NotifyWithWindowHint.</summary>
    public PatchRebootAction? RebootAction { get; set; }
    /// <summary>Motivo legível da decisão (ex.: "política Notify: avisar o operador").</summary>
    public string? RebootActionReason { get; set; }
    /// <summary>Quando a decisão foi calculada.</summary>
    public DateTime? RebootActionAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}