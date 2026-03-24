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

    /// <summary>Perfil de permissao padrao para grupos de dispositivo no MeshCentral.</summary>
    public string MeshCentralGroupPolicyProfile { get; set; } = "viewer";

    /// <summary>Chat de IA para suporte operacional</summary>
    public bool ChatAIEnabled { get; set; } = false;

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

    /// <summary>Intervalo esperado de heartbeat do agent (segundos)</summary>
    public int AgentHeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>Janela de tolerancia para considerar o agent online apos ultimo heartbeat (segundos)</summary>
    public int AgentOnlineGraceSeconds { get; set; } = 120;

    // ============ Branding e IA ============

    /// <summary>Configurações de branding da aplicação</summary>
    public string BrandingSettingsJson { get; set; } = string.Empty;

    /// <summary>Configurações de integração com IA</summary>
    public string AIIntegrationSettingsJson { get; set; } = string.Empty;

    // ============ NATS Auth ============

    /// <summary>Habilita autenticacao JWT no NATS para agentes/usuarios.</summary>
    public bool NatsAuthEnabled { get; set; } = false;

    /// <summary>Seed da conta NATS (nkey) para assinar JWTs de usuario.</summary>
    public string NatsAccountSeed { get; set; } = string.Empty;

    /// <summary>TTL em minutos para JWT de agentes.</summary>
    public int NatsAgentJwtTtlMinutes { get; set; } = 15;

    /// <summary>TTL em minutos para JWT de usuarios.</summary>
    public int NatsUserJwtTtlMinutes { get; set; } = 15;

    /// <summary>Usa subjects com tenant/client/site (novo formato).</summary>
    public bool NatsUseScopedSubjects { get; set; } = false;

    /// <summary>Inclui subjects legados (agent.{id}.*) durante migracao.</summary>
    public bool NatsIncludeLegacySubjects { get; set; } = true;

    /// <summary>
    /// Seed da chave xkey (curve25519) para criptografar o payload do auth callout.
    /// Opcional — quando configurado, o NATS server deve ter xkey habilitado com a chave publica correspondente.
    /// </summary>
    public string NatsXKeySeed { get; set; } = string.Empty;

    /// <summary>
    /// Configurações globais de reporting (JSON).
    /// Exemplo: {"enablePdf":true,"processingTimeoutSeconds":300,...}
    /// </summary>
    public string ReportingSettingsJson { get; set; } = "{}";

    /// <summary>
    /// Configurações globais de anexos de tickets (JSON).
    /// Exemplo: {"enabled":true,"maxFileSizeBytes":10485760,"allowedContentTypes":["image/jpeg","image/png","application/pdf"]}
    /// </summary>
    public string TicketAttachmentSettingsJson { get; set; } = "{}";

    // ============ Object Storage (S3-compatível) ============

    /// <summary>Nome do bucket global para armazenamento</summary>
    public string ObjectStorageBucketName { get; set; } = string.Empty;

    /// <summary>Endpoint do provedor S3-compatível (ex: s3.amazonaws.com, api.cloudflare.com)</summary>
    public string ObjectStorageEndpoint { get; set; } = string.Empty;

    /// <summary>Região do provedor (ex: us-east-1, auto)</summary>
    public string ObjectStorageRegion { get; set; } = string.Empty;

    /// <summary>Access Key ID para autenticação</summary>
    public string ObjectStorageAccessKey { get; set; } = string.Empty;

    /// <summary>Secret Key para autenticação (criptografada em repouso)</summary>
    public string ObjectStorageSecretKey { get; set; } = string.Empty;

    /// <summary>TTL padrão para URLs pré-assinadas do download (horas, default 24)</summary>
    public int ObjectStorageUrlTtlHours { get; set; } = 24;

    /// <summary>Usar path-style URLs em vez de virtual-hosted (necessário para alguns provedores S3-compat)</summary>
    public bool ObjectStorageUsePathStyle { get; set; } = false;

    /// <summary>Verificar certificado SSL (false apenas para dev com self-signed)</summary>
    public bool ObjectStorageSslVerify { get; set; } = true;

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
