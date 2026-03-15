using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

/// <summary>
/// Configurações por site. Herdam de ClientConfiguration e ServerConfiguration.
/// Permitem sobrescrever configurações em nível granular de site.
/// </summary>
public class SiteConfiguration
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }
    
    // ============ Funcionalidades (opcionais para sobrescrever cliente) ============
    
    /// <summary>Recovery automática: null = herda cliente/servidor</summary>
    public bool? RecoveryEnabled { get; set; }

    /// <summary>Recuperacao de dispositivo com reaproveitamento de identidade do agent</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool? DeviceRecoveryEnabled
    {
        get => RecoveryEnabled;
        set => RecoveryEnabled = value;
    }
    
    /// <summary>Discovery automática: null = herda cliente/servidor</summary>
    public bool? DiscoveryEnabled { get; set; }

    /// <summary>Descoberta de agents via rede para auto configuracao</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool? AgentNetworkDiscoveryEnabled
    {
        get => DiscoveryEnabled;
        set => DiscoveryEnabled = value;
    }
    
    /// <summary>P2P Files: null = herda cliente/servidor</summary>
    public bool? P2PFilesEnabled { get; set; }

    /// <summary>Transferencia P2P de arquivos entre agents na mesma rede</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool? P2PTransferEnabled
    {
        get => P2PFilesEnabled;
        set => P2PFilesEnabled = value;
    }
    
    /// <summary>Suporte: null = herda cliente/servidor</summary>
    public bool? SupportEnabled { get; set; }

    /// <summary>Suporte remoto via agent integrado ao MeshCentral</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool? RemoteSupportMeshCentralEnabled
    {
        get => SupportEnabled;
        set => SupportEnabled = value;
    }

    /// <summary>Chat de IA para suporte (null = herda cliente/servidor)</summary>
    public bool? ChatAIEnabled { get; set; }

    /// <summary>Base de conhecimento habilitada (null = herda cliente/servidor)</summary>
    public bool? KnowledgeBaseEnabled { get; set; }
    
    // ============ Loja de aplicativos ============

    /// <summary>Política de acesso à loja de aplicativos (null = herda cliente/servidor)</summary>
    public AppStorePolicyType? AppStorePolicy { get; set; }

    // ============ IA ============

    /// <summary>Configurações de IA específicas do site (null = herda cliente/servidor)</summary>
    public string? AIIntegrationSettingsJson { get; set; }

    // ============ Configuração de Inventário ============

    /// <summary>Intervalo de inventário específico do site (horas)</summary>
    public int? InventoryIntervalHours { get; set; }

    /// <summary>Configurações de atualização automática específicas do site</summary>
    public string? AutoUpdateSettingsJson { get; set; }
    
    // ============ Informações do Site ============
    
    /// <summary>Timezone do site (ex: "America/New_York")</summary>
    public string? Timezone { get; set; }
    
    /// <summary>Localização geográfica do site (ex: "São Paulo, Brasil")</summary>
    public string? Location { get; set; }
    
    /// <summary>Contato ou gerente responsável do site</summary>
    public string? ContactPerson { get; set; }
    
    /// <summary>Email de contato do site</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Nome do grupo/dispositivo no MeshCentral para este site.</summary>
    public string? MeshCentralGroupName { get; set; }

    /// <summary>Mesh ID persistido para reconciliacao/sync com o MeshCentral.</summary>
    public string? MeshCentralMeshId { get; set; }

    /// <summary>
    /// Lista de campos bloqueados no nível de site (JSON array de nomes de propriedade).
    /// Bloqueia sobrescrita nos agents deste site (futuro).
    /// </summary>
    public string LockedFieldsJson { get; set; } = "[]";
    
    // ============ Auditoria ============
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public int Version { get; set; } = 1;
}
