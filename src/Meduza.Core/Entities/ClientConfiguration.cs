using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

/// <summary>
/// Configurações por cliente. Herdam do ServerConfiguration.
/// Podem ser sobrescritas por SiteConfiguration em níveis específicos.
/// </summary>
public class ClientConfiguration
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    
    // ============ Funcionalidades ============
    
    /// <summary>Recovery automática: detecta se o agent foi instalado antes para reutilizar dados</summary>
    public bool? RecoveryEnabled { get; set; } // null = herda de servidor
    
    /// <summary>Discovery automática de rede pelos agents</summary>
    public bool? DiscoveryEnabled { get; set; }
    
    /// <summary>Transferência de arquivos P2P entre agents</summary>
    public bool? P2PFilesEnabled { get; set; }
    
    /// <summary>Suporte habilitado: permite abertura de chamados/tickets</summary>
    public bool? SupportEnabled { get; set; }
    
    // ============ Loja de aplicativos ============

    /// <summary>Política de acesso à loja de aplicativos (null = herda servidor)</summary>
    public AppStorePolicyType? AppStorePolicy { get; set; }

    // ============ IA ============

    /// <summary>Configurações de IA específicas do cliente (null = herda servidor)</summary>
    public string? AIIntegrationSettingsJson { get; set; }

    // ============ Configuração de Inventário e Updates ============

    /// <summary>Intervalo de atualização de inventário (horas)</summary>
    public int? InventoryIntervalHours { get; set; }

    /// <summary>Configurações de atualização automática de software</summary>
    public string? AutoUpdateSettingsJson { get; set; }
    
    // ============ Configurações de Token (herdadas de TenantSettings) ============
    
    /// <summary>Dias de expiração de token de agent</summary>
    public int? TokenExpirationDays { get; set; }
    
    /// <summary>Máximo de tokens simultaneamente válidos por agent</summary>
    public int? MaxTokensPerAgent { get; set; }
    
    /// <summary>Intervalo esperado de heartbeat do agent (segundos)</summary>
    public int? AgentHeartbeatIntervalSeconds { get; set; }
    
    /// <summary>Threshold para considerar agent offline (segundos)</summary>
    public int? AgentOfflineThresholdSeconds { get; set; }

    /// <summary>
    /// Lista de campos bloqueados no nível de cliente (JSON array de nomes de propriedade).
    /// Bloqueia sobrescrita nos sites e agents deste cliente.
    /// </summary>
    public string LockedFieldsJson { get; set; } = "[]";
    
    // ============ Auditoria ============
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public int Version { get; set; } = 1;
}
