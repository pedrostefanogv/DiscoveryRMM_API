using Discovery.Core.Configuration;
using Discovery.Core.Enums;

namespace Discovery.Core.ValueObjects;

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

    public bool CloudBootstrapEnabled { get; set; }

    public bool SupportEnabled { get; set; }

    public bool ChatAIEnabled { get; set; }
    public bool KnowledgeBaseEnabled { get; set; }

    /// <summary>
    /// Kill switch da aba "Atualizações" do painel do agent já resolvido
    /// (servidor → cliente → site). Default global = true.
    /// </summary>
    public bool UpdatesTabEnabled { get; set; }

    public bool ZeroTouchEnabled { get; set; }

    /// <summary>
    /// Job de embeddings da base de conhecimento já resolvido (servidor → cliente → site).
    /// Default global = true; false = o <c>KnowledgeEmbeddingJob</c> não é agendado no Quartz.
    /// </summary>
    public bool KnowledgeEmbeddingEnabled { get; set; }

    // ============ Kill switches de feature (CFG-A) ============

    /// <summary>Modo observação da autorização por departamento (true = só observa).</summary>
    public bool DepartmentAuthShadowMode { get; set; }

    /// <summary>Notificações push habilitadas.</summary>
    public bool PushEnabled { get; set; }

    /// <summary>Sync do catálogo winget habilitado.</summary>
    public bool WingetCatalogEnabled { get; set; }

    /// <summary>Acesso remoto habilitado.</summary>
    public bool RemoteAccessEnabled { get; set; }

    /// <summary>Gravação de sessões remotas habilitada.</summary>
    public bool RemoteAccessRecordingEnabled { get; set; }

    /// <summary>Remoção automática de gravações expiradas.</summary>
    public bool RemoteAccessRecordingAutoDeleteExpired { get; set; }

    /// <summary>Log automático habilitado.</summary>
    public bool AutomaticLoggingEnabled { get; set; }

    /// <summary>Telemetria OpenTelemetry (servidor inteiro; não herda).</summary>
    public bool OpenTelemetryEnabled { get; set; }

    /// <summary>Migrations no boot (servidor inteiro; não herda). ⚠️ desligar dessincroniza o schema.</summary>
    public bool MigrationsRunOnStartup { get; set; }

    /// <summary>Modo do contrato de realtime: "auto" | "strict" | "legacy" (não herda).</summary>
    public string RealtimeContractMode { get; set; } = RealtimeContractModes.Auto;

    /// <summary>
    /// Aba inicial do agent já resolvida (servidor → cliente → site). Sempre um id
    /// válido de <see cref="AgentHomeTabCatalog"/>; o agent cai para "status" se a
    /// aba estiver desabilitada para ele.
    /// </summary>
    public string AgentHomeTab { get; set; } = AgentHomeTabCatalog.DefaultTab;

    // ============ Loja de aplicativos ============

    public AppStorePolicyType AppStorePolicy { get; set; }

    // ============ Inventário ============

    public int InventoryIntervalHours { get; set; }

    // ============ Updates automáticos ============

    public AgentUpdatePolicy AgentUpdate { get; set; } = new();

    // ============ IA ============

    public AIIntegrationSettings AIIntegration { get; set; } = new();

    /// <summary>
    /// Processamentos em segundo plano (métricas de atendente e triagem por IA)
    /// já mesclados: global + override do cliente.
    /// </summary>
    public BackgroundProcessingSettings BackgroundProcessing { get; set; } = new();

    // ============ Token / Heartbeat ============

    public int AgentHeartbeatIntervalSeconds { get; set; }
    public int AgentOnlineGraceSeconds { get; set; }

    // ============ Autorização de acesso remoto (desktop) ============

    public RemoteConsentMode RemoteConsentMode { get; set; }
    public int RemoteConsentTimeoutSeconds { get; set; }
    public RemoteConsentTimeoutAction RemoteConsentTimeoutAction { get; set; }
    public bool RemoteConsentShowBanner { get; set; }
    public string RemoteConsentMessage { get; set; } = string.Empty;
    public string RemoteConsentBannerText { get; set; } = string.Empty;

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
