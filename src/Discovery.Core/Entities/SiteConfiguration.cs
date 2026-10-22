using Discovery.Core.Configuration;
using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

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

    /// <summary>Discovery automática: null = herda cliente/servidor</summary>
    public bool? DiscoveryEnabled { get; set; }

    /// <summary>P2P Files: null = herda cliente/servidor</summary>
    public bool? P2PFilesEnabled { get; set; }

    /// <summary>Suporte: null = herda cliente/servidor</summary>
    public bool? SupportEnabled { get; set; }

    /// <summary>Chat de IA para suporte (null = herda cliente/servidor)</summary>
    public bool? ChatAIEnabled { get; set; }

    /// <summary>Base de conhecimento habilitada (null = herda cliente/servidor)</summary>
    public bool? KnowledgeBaseEnabled { get; set; }

    /// <summary>
    /// Página inicial do agent para este site (null/vazio = herda cliente/servidor).
    /// Ids aceitos em <see cref="AgentHomeTabCatalog"/>.
    /// </summary>
    public string? AgentHomeTab { get; set; }

    /// <summary>Zero-touch provisioning habilitado (null = herda cliente/servidor)</summary>
    public bool? ZeroTouchEnabled { get; set; }

    // ============ Loja de aplicativos ============

    /// <summary>Política de acesso à loja de aplicativos (null = herda cliente/servidor)</summary>
    public AppStorePolicyType? AppStorePolicy { get; set; }

    // ============ IA ============

    /// <summary>
    /// Override de configurações de IA para este site (null = herda cliente/servidor).
    /// Armazena apenas campos sobrescritíveis (AIIntegrationSettingsOverride): ChatModel,
    /// Temperature, PromptTemplate, MaxHistoryMessages, etc.
    /// Campos globais (ApiKey, EmbeddingModel, Provider) são sempre herdados do servidor
    /// e removidos automaticamente ao salvar.
    /// </summary>
    public string? AIIntegrationSettingsJson { get; set; }

    // ============ Configuração de Inventário ============

    /// <summary>Intervalo de inventário específico do site (horas)</summary>
    public int? InventoryIntervalHours { get; set; }

    /// <summary>Configurações de self-update do agent específicas do site</summary>
    public string? AgentUpdatePolicyJson { get; set; }

    /// <summary>Janela de tolerancia para considerar o agent online (segundos)</summary>
    public int? AgentOnlineGraceSeconds { get; set; }

    // ============ Informações do Site ============

    /// <summary>Timezone do site (ex: "America/New_York")</summary>
    public string? Timezone { get; set; }

    /// <summary>Localização geográfica do site (ex: "São Paulo, Brasil")</summary>
    public string? Location { get; set; }

    /// <summary>Contato ou gerente responsável do site</summary>
    public string? ContactPerson { get; set; }

    /// <summary>Email de contato do site</summary>
    public string? ContactEmail { get; set; }

    // ============ Gerenciador de Patches (Windows Update) ============

    /// <summary>Kill switch do módulo de patches neste site (null = herda).</summary>
    public bool? WindowsUpdateEnabled { get; set; }

    /// <summary>Intervalo do scan WUA (minutos) neste site (null = herda).</summary>
    public int? WindowsUpdateScanIntervalMinutes { get; set; }

    // ============ Autorização de acesso remoto (null = herda cliente/servidor) ============

    /// <summary>Modo de consentimento de acesso remoto neste site (null = herda).</summary>
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

    /// <summary>
    /// Janela de manutenção do agent (reboot/patches): JSON
    /// { enabled, daysOfWeek, startUtc, durationMinutes, rebootGraceSeconds }.
    /// Null/vazio = herda/usa default (desligado).
    /// </summary>
    public string MaintenanceWindowJson { get; set; } = string.Empty;

    /// <summary>
    /// Política de tools MCP do chat do agent: JSON
    /// { mode: "all"|"allowlist", allow: [nomes] }. Null/vazio = herda/all.
    /// </summary>
    public string McpToolPolicyJson { get; set; } = string.Empty;
}
