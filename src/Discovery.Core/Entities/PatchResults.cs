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

/// <summary>Metadados do scan reportados pelo agente.</summary>
public class PatchScanMeta
{
    public string Status { get; set; } = "ok";
    public int? DurationMs { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>Sonda de reinício pendente reportada pelo agente.</summary>
public class PatchPendingRebootMeta
{
    public bool Pending { get; set; }
    public IReadOnlyList<string> Reasons { get; set; } = [];
    public DateTime? CheckedAt { get; set; }
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
