using System.Text.Json.Serialization;
using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class Agent
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Offline;
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? AgentVersion { get; set; }
    public string? CommitHash { get; set; }
    public string? LastIpAddress { get; set; }
    public string? MacAddress { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool ZeroTouchPending { get; set; }

    // ── Fingerprint de hardware (Recuperação de Dispositivos) ─────────────
    /// <summary>Hash SHA-256 (hex) da chave pública da TPM Endorsement Key (EK).</summary>
    public string? TpmEkHash { get; set; }
    /// <summary>UUID SMBIOS da máquina (normalizado, apenas hex + hífens).</summary>
    public string? SmbiosUuid { get; set; }
    /// <summary>Hash combinado (TPM EK + SMBIOS UUID) usado para busca de recuperação.</summary>
    public string? FingerprintHash { get; set; }

    public bool MaintenanceEnabled { get; set; }
    public string? MaintenanceReason { get; set; }
    public DateTime? MaintenanceChangedAt { get; set; }
    public Guid? MaintenanceChangedByUserId { get; set; }

    [JsonIgnore]
    public AgentStatus EffectiveStatus => MaintenanceEnabled ? AgentStatus.Maintenance : Status;
}
