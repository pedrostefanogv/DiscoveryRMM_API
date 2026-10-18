using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Linha de inventário de Windows Update de um agente (pendente ou instalada).
///
/// <para>
/// <c>GroupKey</c> é a chave estável usada por aprovações e pela visão de frota:
/// a primeira KB quando existe (ex.: <c>kb:KB5034441</c>), senão a própria
/// <c>UpdateKey</c> (guid+revisão). <c>UpdateKey</c> identifica a revisão exata
/// instalável.
/// </para>
/// </summary>
public class AgentWindowsUpdate
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>Chave da revisão: <c>guid:&lt;guid&gt;:&lt;rev&gt;</c> ou <c>kb:KBxxxx</c>.</summary>
    public string UpdateKey { get; set; } = string.Empty;
    /// <summary>Chave de agrupamento/aprovação (KB quando disponível).</summary>
    public string GroupKey { get; set; } = string.Empty;

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

    public DateTime ReportedAt { get; set; }
    public DateTime? InstallStartedAt { get; set; }
    public int? InstallResultCode { get; set; }
    public string? InstallError { get; set; }

    /// <summary>Estado efetivo de aprovação (denormalizado para filtros rápidos).</summary>
    public PatchApprovalState ApprovalState { get; set; } = PatchApprovalState.Unknown;
    /// <summary>Origem do estado: rule, policy, none.</summary>
    public string? ApprovalSource { get; set; }

    public PatchUpdateStatus Status { get; set; } = PatchUpdateStatus.Pending;
    public string? Source { get; set; }

    public string? Raw { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public bool IsPresent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
