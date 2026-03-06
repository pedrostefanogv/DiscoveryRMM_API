using Meduza.Core.Enums;

namespace Meduza.Core.ValueObjects;

/// <summary>
/// Configuração completamente resolvida para um site/agent, sem nenhum valor null.
/// Resultado da lógica de herança Server → Client → Site.
/// Consumida por agents via API e pelo frontend para exibir configurações efetivas.
/// </summary>
public class ResolvedConfiguration
{
    // ============ Funcionalidades ============

    public bool RecoveryEnabled { get; set; }
    public bool DiscoveryEnabled { get; set; }
    public bool P2PFilesEnabled { get; set; }
    public bool SupportEnabled { get; set; }
    public bool KnowledgeBaseEnabled { get; set; }

    // ============ Loja de aplicativos ============

    public AppStorePolicyType AppStorePolicy { get; set; }

    // ============ Inventário ============

    public int InventoryIntervalHours { get; set; }

    // ============ Updates automáticos ============

    public AutoUpdateSettings AutoUpdate { get; set; } = new();

    // ============ IA ============

    public AIIntegrationSettings AIIntegration { get; set; } = new();

    // ============ Token / Heartbeat ============

    public int TokenExpirationDays { get; set; }
    public int MaxTokensPerAgent { get; set; }
    public int AgentHeartbeatIntervalSeconds { get; set; }
    public int AgentOfflineThresholdSeconds { get; set; }

    // ============ Metadados da resolução ============

    /// <summary>ID do site para o qual a configuração foi resolvida (null = nível cliente ou servidor)</summary>
    public Guid? SiteId { get; set; }

    /// <summary>ID do cliente para o qual a configuração foi resolvida (null = nível servidor)</summary>
    public Guid? ClientId { get; set; }

    /// <summary>Timestamp da resolução (para cache)</summary>
    public DateTime ResolvedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Origem efetiva de cada campo (0=Block, 2=Global, 3=Client, 4=Site).
    /// Exemplo: { "DiscoveryEnabled": 3 }
    /// </summary>
    public Dictionary<string, int> Inheritance { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Campos bloqueados para sobrescrita em níveis inferiores.
    /// </summary>
    public string[] BlockedFields { get; set; } = [];
}
