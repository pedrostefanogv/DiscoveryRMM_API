using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>Linha de inventário de patch no formato aceito pelo upload do agente.</summary>
public class AgentWindowsUpdateEntry
{
    public string UpdateKey { get; set; } = string.Empty;
    public string? KbIds { get; set; }
    public string? UpdateId { get; set; }
    public int? Revision { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Classification { get; set; }
    public string? Categories { get; set; }
    public string? Severity { get; set; }
    public bool IsPending { get; set; }
    public bool IsInstalled { get; set; }
    public bool IsHidden { get; set; }
    public bool IsDownloaded { get; set; }
    public bool IsMandatory { get; set; }
    public bool RequiresReboot { get; set; }
    public long? MaxDownloadSize { get; set; }
    public string? SupportUrl { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime? LastDeployedAt { get; set; }
    public DateTime? InstalledAt { get; set; }
    public string? Source { get; set; }
    public string? Raw { get; set; }
}

/// <summary>
/// Motivos de descarte de um update no scan (contrato estável com o agente).
/// "unreadable" é o único que indica leitura incompleta: a lista pode não
/// representar a remoção de um item, então ele nunca vira "Superseded".
/// </summary>
public static class PatchScanSkipReasons
{
    public const string Driver = "driver";
    public const string NoIdentity = "no_identity";
    public const string Unreadable = "unreadable";

    public static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        Driver, NoIdentity, Unreadable
    };
}

/// <summary>Metadados do scan reportados pelo agente.</summary>
public class PatchScanMeta
{
    public string Status { get; set; } = "ok";
    public int? DurationMs { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime? StartedAt { get; set; }

    // ── Diagnóstico do scan (bloco opcional) ────────────────────────────────
    // Campos ausentes em agentes antigos: null = não informado, e o valor
    // conhecido no snapshot é preservado (nunca zerado por omissão).

    /// <summary>Resultados descartados pelo scan (drivers, sem identidade, ilegíveis).</summary>
    public int? SkippedCount { get; set; }
    /// <summary>Motivo → quantidade: driver, no_identity, unreadable.</summary>
    public IReadOnlyDictionary<string, int>? SkippedReasons { get; set; }
    /// <summary>periodic | change | command | install.</summary>
    public string? Trigger { get; set; }
    /// <summary>wua | qfe.</summary>
    public string? Source { get; set; }
}

/// <summary>Sonda de reinício pendente reportada pelo agente.</summary>
public class PatchPendingRebootMeta
{
    public bool Pending { get; set; }
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public DateTime? CheckedAt { get; set; }
}

/// <summary>
/// Motivos de reinício pendente que acionam o badge "Reinício pendente" em
/// /agents: apenas os ligados a atualizações a instalar (Windows Update, CBS
/// e servicing). Motivos operacionais residuais — <c>file_rename</c> (arquivos
/// em uso de qualquer instalador, ex.: OneDrive) e <c>computer_rename</c> —
/// continuam gravados para o painel de detalhes, mas NÃO acendem o badge:
/// empurrar o operador a reiniciar por causa deles é falso positivo.
/// </summary>
public static class PatchPendingRebootReasons
{
    public static readonly HashSet<string> Primary = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows_update", "cbs", "servicing"
    };

    /// <summary>
    /// Verdadeiro quando ao menos um motivo é "principal" (update). Lista nula
    /// ou vazia devolve VERDADEIRO de propósito: agente antigo ou motivo fora
    /// da allowlist não pode APAGAR um pending=true que antes acendia o badge
    /// (regressão silenciosa). Só motivo residual conhecido filtra.
    /// </summary>
    public static bool HasPrimary(IReadOnlyList<string>? reasons)
        => reasons is null || reasons.Count == 0 ||
           reasons.Any(reason => Primary.Contains(reason));
}

/// <summary>
/// Sonda de estado do serviço Windows Update reportada pelo agente no bloco
/// opcional <c>state</c> do envelope. Explica na UI os casos em que o scan WUA
/// não devolve update nenhum (serviço desabilitado, sem rede, política WSUS).
///
/// Todos os campos são opcionais: um agente antigo simplesmente não envia o
/// bloco e o snapshot mantém o último estado conhecido.
/// </summary>
public class PatchServiceStateMeta
{
    /// <summary>running | stopped | disabled | unknown.</summary>
    public string? ServiceState { get; set; }
    public bool? WuOnline { get; set; }
    public bool? PolicyManaged { get; set; }
    public DateTime? LastInstallSuccessAt { get; set; }
    public bool? DownloadInProgress { get; set; }
    /// <summary>Servidor WSUS configurado (WUServer/UpdateServiceUrlAlternate), até 500 chars.</summary>
    public string? WsusServer { get; set; }
    /// <summary>AUOptions da política (0–5).</summary>
    public int? AuOptions { get; set; }
    /// <summary>NoAutoUpdate=1: atualização automática desligada por política.</summary>
    public bool? NoAutoUpdate { get; set; }
    /// <summary>Deferral de quality/feature updates em dias (0–365).</summary>
    public int? DeferralDays { get; set; }
    /// <summary>Início da janela de horas ativas (0–23).</summary>
    public int? ActiveHoursStart { get; set; }
    /// <summary>Fim da janela de horas ativas (0–23).</summary>
    public int? ActiveHoursEnd { get; set; }
    /// <summary>KBs instaladas contadas no registro (CBS) pela sonda (0–100000).</summary>
    public int? QfeInstalledCount { get; set; }
    /// <summary>Percentual do download em andamento (0–100).</summary>
    public int? DownloadPercent { get; set; }
    /// <summary>
    /// A politica de Windows Update foi LIDA (inclusive "nenhuma politica").
    /// Quando true, campos de politica ausentes no payload CLEARAM o valor
    /// antigo (WSUS removido, deferral desligado); quando false/null o valor
    /// conhecido é preservado (leitura falhou).
    /// </summary>
    public bool? PolicyKnown { get; set; }
    /// <summary>Assinatura/hash da sonda — mudou quando vale a pena novo scan.</summary>
    public string? StateHash { get; set; }
    public DateTime? StateChangedAt { get; set; }

    public bool IsEmpty =>
        ServiceState is null &&
        WuOnline is null &&
        PolicyManaged is null &&
        LastInstallSuccessAt is null &&
        DownloadInProgress is null &&
        WsusServer is null &&
        AuOptions is null &&
        NoAutoUpdate is null &&
        DeferralDays is null &&
        ActiveHoursStart is null &&
        ActiveHoursEnd is null &&
        QfeInstalledCount is null &&
        DownloadPercent is null &&
        PolicyKnown is null &&
        StateHash is null &&
        StateChangedAt is null;
}

/// <summary>Página de updates do agente (paginação por offset).</summary>
public class AgentWindowsUpdatePageResult
{
    public IReadOnlyList<AgentWindowsUpdate> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int PendingCount { get; set; }
    public int InstalledCount { get; set; }
}

/// <summary>Agrupamento de uma atualização na visão de frota.</summary>
public class PatchInventoryGroup
{
    public string GroupKey { get; set; } = string.Empty;
    public string? KbIds { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Classification { get; set; }
    public string? Severity { get; set; }
    public DateTime? PublishedAt { get; set; }
    public int PendingAgents { get; set; }
    public int InstalledAgents { get; set; }
    public int DeniedAgents { get; set; }
    public int ReportingAgents { get; set; }
    public DateTime? FirstSeenAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>Agente dentro do drill-down de uma atualização.</summary>
public class PatchInventoryAgentRow
{
    public Guid AgentId { get; set; }
    public string UpdateKey { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string? AgentDisplayName { get; set; }
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public PatchUpdateStatus Status { get; set; }
    public PatchApprovalState ApprovalState { get; set; }
    public string? Severity { get; set; }
    public bool RequiresReboot { get; set; }
    public DateTime ReportedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

/// <summary>Agente na visão de pendências da frota.</summary>
public class PatchPendingAgentRow
{
    public Guid AgentId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? AgentDisplayName { get; set; }
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int PendingCount { get; set; }
    public int CriticalCount { get; set; }
    public int DeniedCount { get; set; }
    public bool PendingReboot { get; set; }
    public string? ScanStatus { get; set; }
    public DateTime? LastScanAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>Snapshot/conformidade de patches de um escopo.</summary>
public class PatchScopeSnapshot
{
    public int TotalPending { get; set; }
    public int CriticalPending { get; set; }
    public int DeniedPending { get; set; }
    public int ApprovedPending { get; set; }
    public int AgentsWithPending { get; set; }
    public int AgentsTotal { get; set; }
    public int AgentsReporting { get; set; }
    public int RebootPendingAgents { get; set; }
    public int FailedInstallations24h { get; set; }
    public DateTime? LastScanAt { get; set; }
}

/// <summary>Agregados por severidade/classificação do overview.</summary>
public class PatchSeverityCount
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}
