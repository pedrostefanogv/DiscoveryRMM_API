using Meduza.Core.Enums;
using Meduza.Core.ValueObjects;

namespace Meduza.Core.Entities;

/// <summary>
/// Configurações globais do servidor. Aplicam-se a todos os clientes e sites.
/// É uma entidade singleton (apenas uma instância deve existir).
/// </summary>
public class ServerConfiguration
{
    public Guid Id { get; set; }

    // ============ Funcionalidades globais ============

    /// <summary>Recovery automática: detecta se o agent foi instalado antes para reutilizar dados/histórico</summary>
    public bool RecoveryEnabled { get; set; } = false;

    /// <summary>Discovery automática: permite que os agents façam discovery na rede para configurar o servidor</summary>
    public bool DiscoveryEnabled { get; set; } = false;

    /// <summary>Transferência de arquivos P2P entre agents</summary>
    public bool P2PFilesEnabled { get; set; } = false;

    /// <summary>Suporte habilitado (para todos os clientes)</summary>
    public bool SupportEnabled { get; set; } = false;

    /// <summary>Base de conhecimento habilitada</summary>
    public bool KnowledgeBaseEnabled { get; set; } = false;

    // ============ Loja de aplicativos ============

    /// <summary>Política de acesso à loja de aplicativos</summary>
    public AppStorePolicyType AppStorePolicy { get; set; } = AppStorePolicyType.PreApproved;

    // ============ Inventário e updates ============

    /// <summary>Intervalo padrão de inventário (horas)</summary>
    public int InventoryIntervalHours { get; set; } = 24;

    /// <summary>Configurações padrão de atualização automática</summary>
    public string AutoUpdateSettingsJson { get; set; } = string.Empty;

    // ============ Configurações de token e agent ============

    /// <summary>Dias de expiração de token de agent</summary>
    public int TokenExpirationDays { get; set; } = 365;

    /// <summary>Máximo de tokens simultaneamente válidos por agent</summary>
    public int MaxTokensPerAgent { get; set; } = 3;

    /// <summary>Intervalo esperado de heartbeat do agent (segundos)</summary>
    public int AgentHeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>Threshold para considerar agent offline (segundos)</summary>
    public int AgentOfflineThresholdSeconds { get; set; } = 300;

    // ============ Branding e IA ============

    /// <summary>Configurações de branding da aplicação</summary>
    public string BrandingSettingsJson { get; set; } = string.Empty;

    /// <summary>Configurações de integração com IA</summary>
    public string AIIntegrationSettingsJson { get; set; } = string.Empty;

    /// <summary>
    /// Lista de campos bloqueados para sobrescrita em níveis inferiores (JSON array de nomes de propriedade).
    /// Exemplo: ["DiscoveryEnabled", "InventoryIntervalHours"]
    /// </summary>
    public string LockedFieldsJson { get; set; } = "[]";

    // ============ Auditoria ============

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Versão para rastreamento de mudanças</summary>
    public int Version { get; set; } = 1;
}
