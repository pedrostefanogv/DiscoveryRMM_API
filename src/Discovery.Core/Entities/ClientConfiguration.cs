using Discovery.Core.Configuration;
using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

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

    /// <summary>Habilita o bootstrap P2P via cloud para este cliente (null = herda servidor)</summary>
    public bool? CloudBootstrapEnabled { get; set; }

    /// <summary>Suporte habilitado: permite abertura de chamados/tickets</summary>
    public bool? SupportEnabled { get; set; }

    /// <summary>Chat de IA para suporte (null = herda servidor)</summary>
    public bool? ChatAIEnabled { get; set; }

    /// <summary>Base de conhecimento habilitada (null = herda servidor)</summary>
    public bool? KnowledgeBaseEnabled { get; set; }

    /// <summary>
    /// Página inicial do agent para este cliente (null/vazio = herda servidor).
    /// Ids aceitos em <see cref="AgentHomeTabCatalog"/>.
    /// </summary>
    public string? AgentHomeTab { get; set; }

    /// <summary>Zero-touch provisioning habilitado (null = herda servidor)</summary>
    public bool? ZeroTouchEnabled { get; set; }

    // ============ Loja de aplicativos ============

    /// <summary>Política de acesso à loja de aplicativos (null = herda servidor)</summary>
    public AppStorePolicyType? AppStorePolicy { get; set; }

    // ============ IA ============

    /// <summary>
    /// Override de configurações de IA para este cliente (null = herda servidor integralmente).
    /// Armazena apenas campos sobrescritíveis (AIIntegrationSettingsOverride): ChatModel,
    /// Temperature, PromptTemplate, MaxHistoryMessages, etc.
    /// Campos globais (ApiKey, EmbeddingModel, Provider) são sempre herdados do servidor
    /// e removidos automaticamente ao salvar.
    /// </summary>
    public string? AIIntegrationSettingsJson { get; set; }

    /// <summary>
    /// Override da configuração de processamentos em segundo plano (null = herda o
    /// global integralmente). Armazena apenas campos sobrescrevíveis
    /// (BackgroundProcessingSettingsOverride); ausentes herdam o global.
    /// </summary>
    public string? BackgroundProcessingSettingsJson { get; set; }

    // ============ Configuração de Inventário e Updates ============

    /// <summary>Intervalo de atualização de inventário (horas)</summary>
    public int? InventoryIntervalHours { get; set; }

    /// <summary>Configurações de self-update do agent para este cliente</summary>
    public string? AgentUpdatePolicyJson { get; set; }

    // ============ Configurações de conexão de agent ============

    /// <summary>Intervalo esperado de heartbeat do agent (segundos)</summary>
    public int? AgentHeartbeatIntervalSeconds { get; set; }

    /// <summary>Janela de tolerancia para considerar o agent online (segundos)</summary>
    public int? AgentOnlineGraceSeconds { get; set; }

    // ============ Gerenciador de Patches (Windows Update) ============

    /// <summary>Kill switch do módulo de patches neste cliente (null = herda).</summary>
    public bool? WindowsUpdateEnabled { get; set; }

    /// <summary>Intervalo do scan WUA (minutos) neste cliente (null = herda).</summary>
    public int? WindowsUpdateScanIntervalMinutes { get; set; }

    // ============ Autorização de acesso remoto (null = herda servidor) ============

    /// <summary>Modo de consentimento de acesso remoto neste cliente (null = herda).</summary>
    public RemoteConsentMode? RemoteConsentMode { get; set; }

    /// <summary>Timeout do prompt em segundos (null = herda).</summary>
    public int? RemoteConsentTimeoutSeconds { get; set; }

    /// <summary>Ação no timeout do prompt (null = herda).</summary>
    public RemoteConsentTimeoutAction? RemoteConsentTimeoutAction { get; set; }

    /// <summary>Exibe banner local durante a sessão (null = herda).</summary>
    public bool? RemoteConsentShowBanner { get; set; }

    /// <summary>Mensagem do prompt (null = herda).</summary>
    public string? RemoteConsentMessage { get; set; }

    /// <summary>Texto do banner local (null = herda).</summary>
    public string? RemoteConsentBannerText { get; set; }

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

    /// <summary>
    /// Janela de manutenção do agent (reboot/patches): JSON
    /// { enabled, daysOfWeek, startUtc, durationMinutes, rebootGraceSeconds }.
    /// Null/vazio = herda/usa default (desligado).
    /// </summary>
    public string? MaintenanceWindowJson { get; set; } = string.Empty;

    /// <summary>
    /// Política de tools MCP do chat do agent: JSON
    /// { mode: "all"|"allowlist", allow: [nomes] }. Null/vazio = herda/all.
    /// </summary>
    public string? McpToolPolicyJson { get; set; } = string.Empty;
}
