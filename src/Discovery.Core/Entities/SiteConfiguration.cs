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

    /// <summary>Perfil de permissao para grupos MeshCentral (null = herda cliente/servidor).</summary>
    public string? MeshCentralGroupPolicyProfile { get; set; }

    /// <summary>Chat de IA para suporte (null = herda cliente/servidor)</summary>
    public bool? ChatAIEnabled { get; set; }

    /// <summary>Base de conhecimento habilitada (null = herda cliente/servidor)</summary>
    public bool? KnowledgeBaseEnabled { get; set; }
    
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

    /// <summary>Configurações de atualização automática específicas do site</summary>
    public string? AutoUpdateSettingsJson { get; set; }

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

    /// <summary>Nome do grupo/dispositivo no MeshCentral para este site.</summary>
    public string? MeshCentralGroupName { get; set; }

    /// <summary>Mesh ID persistido para reconciliacao/sync com o MeshCentral.</summary>
    public string? MeshCentralMeshId { get; set; }

    /// <summary>Snapshot do perfil aplicado no grupo MeshCentral durante o provisionamento.</summary>
    public string? MeshCentralAppliedGroupPolicyProfile { get; set; }

    /// <summary>Timestamp UTC do ultimo snapshot de policy aplicado no grupo MeshCentral.</summary>
    public DateTime? MeshCentralAppliedGroupPolicyAt { get; set; }

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
