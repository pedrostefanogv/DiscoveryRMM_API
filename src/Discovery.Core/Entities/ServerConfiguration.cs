using Discovery.Core.Configuration;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Discovery.Core.Entities;

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

    /// <summary>Habilita o bootstrap P2P via cloud (agent obtém lista de peers via servidor ao invés de apenas LAN)</summary>
    public bool CloudBootstrapEnabled { get; set; } = false;

    /// <summary>Suporte habilitado (para todos os clientes)</summary>
    public bool SupportEnabled { get; set; } = false;

    /// <summary>Chat de IA para suporte operacional</summary>
    public bool ChatAIEnabled { get; set; } = false;

    /// <summary>Base de conhecimento habilitada</summary>
    public bool KnowledgeBaseEnabled { get; set; } = false;

    /// <summary>
    /// Kill switch da aba "Atualizações" do painel do agent (apps pendentes de
    /// update via winget/chocolatey). Default TRUE: todas as demais abas já têm
    /// seu gate (appStoreEnabled/chatAIEnabled/supportEnabled/knowledgeBaseEnabled)
    /// e a "Atualizações" ficava sempre visível — o default true preserva o
    /// comportamento atual no rollout. Global — pode ser sobrescrita por cliente
    /// e por site (null = herda).
    /// </summary>
    public bool UpdatesTabEnabled { get; set; } = true;

    /// <summary>
    /// Página inicial do agent: id da aba que o painel abre ao iniciar
    /// (ver <see cref="AgentHomeTabCatalog"/>). Global — pode ser sobrescrita por
    /// cliente e por site; quando a aba está desabilitada para o agent, ele cai
    /// para a aba de status.
    /// </summary>
    public string AgentHomeTab { get; set; } = AgentHomeTabCatalog.DefaultTab;

    /// <summary>
    /// Zero-touch provisioning: permite que agents sem credenciais se registrem
    /// sozinhos via P2P onboarding. Depende de DiscoveryEnabled para os agents se
    /// encontrarem na rede. Default true para preservar o comportamento atual.
    /// </summary>
    public bool ZeroTouchEnabled { get; set; } = true;

    /// <summary>
    /// Liga/desliga o <c>KnowledgeEmbeddingJob</c> (re-chunking de artigos,
    /// geração de embeddings em lote e consumo da fila LISTEN/NOTIFY).
    ///
    /// Default TRUE para preservar o comportamento atual: o job era agendado
    /// incondicionalmente a cada 30s no Quartz, então "desligar" na configuração
    /// não tinha efeito nenhum. Com a flag real, false = job NÃO é agendado.
    /// Global — pode ser sobrescrita por cliente e por site (null = herda).
    /// </summary>
    public bool KnowledgeEmbeddingEnabled { get; set; } = true;

    // ============ Kill switches de feature (CFG-A) ============
    // Todos com DEFAULT IDÊNTICO ao valor que já estava no appsettings.json, para
    // o rollout não mudar comportamento nenhum. As chaves do appsettings passam a
    // ser apenas bootstrap/fallback; a fonte de verdade é esta configuração.

    /// <summary>
    /// Modo OBSERVAÇÃO da autorização por departamento. true = as negações de
    /// tratar/fechar são registradas em log mas o acesso é concedido; false = a
    /// regra BLOQUEIA de fato (só o time do departamento trata). Default true:
    /// papéis existentes (Operator/Support) têm Tickets.Edit amplo e seriam
    /// bloqueados de imediato. Equivale a Tickets:DepartmentAuth:ShadowMode.
    /// Herdável por cliente/site.
    /// </summary>
    public bool DepartmentAuthShadowMode { get; set; } = true;

    /// <summary>
    /// Notificações push (WebPush) habilitadas. Equivale a Push:Enabled.
    /// Herdável por cliente/site.
    /// </summary>
    public bool PushEnabled { get; set; } = true;

    /// <summary>
    /// Sincronização do catálogo de aplicativos a partir dos manifests do winget.
    /// Equivale a AppCatalog:Winget:Enabled. Herdável por cliente/site.
    /// </summary>
    public bool WingetCatalogEnabled { get; set; } = true;

    /// <summary>
    /// Acesso remoto (tela, terminal, arquivos) habilitado. Equivale a
    /// RemoteAccess:Enabled. Herdável por cliente/site.
    /// </summary>
    public bool RemoteAccessEnabled { get; set; } = true;

    /// <summary>
    /// Gravação das sessões de acesso remoto. Equivale a
    /// RemoteAccess:Recording:Enabled. Herdável por cliente/site.
    /// </summary>
    public bool RemoteAccessRecordingEnabled { get; set; } = true;

    /// <summary>
    /// Remoção automática das gravações expiradas conforme a retenção.
    /// Equivale a RemoteAccess:Recording:Retention:AutoDeleteExpired.
    /// Herdável por cliente/site.
    /// </summary>
    public bool RemoteAccessRecordingAutoDeleteExpired { get; set; } = true;

    /// <summary>
    /// Log automático de eventos de negócio/exceções. Equivale a
    /// AutomaticLogging:Enabled. Herdável por cliente/site.
    /// </summary>
    public bool AutomaticLoggingEnabled { get; set; } = true;

    /// <summary>
    /// Exportação de telemetria (OpenTelemetry). Equivale a OpenTelemetry:Enabled.
    /// Default false (o coletor é opcional). NÃO herda por cliente/site: o
    /// pipeline é do processo inteiro do servidor.
    /// </summary>
    public bool OpenTelemetryEnabled { get; set; } = false;

    /// <summary>
    /// Executa as migrations FluentMigrator no boot da API. Equivale a
    /// Migrations:RunOnStartup. ⚠️ RISCO: desligar impede a migração no boot e
    /// deixa o schema dessincronizado em instalações novas. NÃO herda por
    /// cliente/site (o schema é do banco inteiro).
    /// </summary>
    public bool MigrationsRunOnStartup { get; set; } = true;

    /// <summary>
    /// Modo do contrato de eventos de realtime: "auto" (default), "strict" ou
    /// "legacy". Equivale a RealtimeContract:Mode. ⚠️ RISCO: "strict" pode
    /// rejeitar emissores legados e quebrar o dashboard em tempo real.
    /// NÃO herda por cliente/site (é o contrato global do dashboard).
    /// </summary>
    public string RealtimeContractMode { get; set; } = "auto";

    // ============ Loja de aplicativos ============

    /// <summary>Política de acesso à loja de aplicativos</summary>
    public AppStorePolicyType AppStorePolicy { get; set; } = AppStorePolicyType.PreApproved;

    /// <summary>
    /// Resultado da última sincronização de catálogo por tipo (JSON:
    /// {"Winget": AppCatalogSyncResultDto, "Chocolatey": ...}). Gravado pelo job
    /// automático (Quartz) e pelo sync manual — alimenta o status no console web.
    /// </summary>
    public string AppCatalogSyncSettingsJson { get; set; } = "{}";

    // ============ Inventário e updates ============

    /// <summary>Intervalo padrão de inventário (horas)</summary>
    public int InventoryIntervalHours { get; set; } = 24;

    /// <summary>Configurações padrão de self-update do agent.</summary>
    public string AgentUpdatePolicyJson { get; set; } = string.Empty;

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

    /// <summary>Habilita a conexão com o servidor NATS (desabilitar para ambientes sem NATS).</summary>
    public bool NatsEnabled { get; set; } = true;

    /// <summary>Habilita autenticacao JWT no NATS para agentes/usuarios.</summary>
    public bool NatsAuthEnabled { get; set; } = true;

    /// <summary>TTL em minutos para JWT de agentes.</summary>
    public int NatsAgentJwtTtlMinutes { get; set; } = 60; // 1h

    /// <summary>TTL em minutos para JWT de usuarios.</summary>
    public int NatsUserJwtTtlMinutes { get; set; } = 60; // 1h

    /// <summary>Usa subjects com tenant/client/site (novo formato).</summary>
    [JsonIgnore]
    public bool NatsUseScopedSubjects { get; set; } = true;

    /// <summary>
    /// Host ou hostname do servidor NATS usada internamente pelo backend da API (privada).
    /// Exemplo: 192.168.1.20
    /// </summary>
    public string NatsServerHostInternal { get; set; } = "localhost";

    /// <summary>
    /// Host ou hostname do servidor NATS exposta para agentes externos.
    /// Exemplo: nats.tngplacas.com.br
    /// </summary>
    public string NatsServerHostExternal { get; set; } = "localhost";

    /// <summary>
    /// Usa WSS externo para conexão dos agentes.
    /// </summary>
    public bool NatsUseWssExternal { get; set; } = false;

    /// <summary>
    /// URL WebSocket externa do NATS para o dashboard/browser conectar.
    /// Exemplo: wss://tngplacas.com.br/nats
    /// </summary>
    public string NatsWebSocketExternalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Configurações globais de reporting (JSON).
    /// Exemplo: {"processingTimeoutSeconds":300,"maxConcurrentExecutions":2,...}
    /// </summary>
    public string ReportingSettingsJson { get; set; } = "{}";

    /// <summary>
    /// Configurações de retenção e manutenção de base de dados (JSON).
    /// Controla: logs, notificações, comandos, sessões, tokens, telemetria, VACUUM/REINDEX.
    /// </summary>
    public string RetentionSettingsJson { get; set; } = "{}";

    /// <summary>
    /// Configuração global de sensores (JSON): kill switch, intervalo default,
    /// auto-ticket e notificações. Ver <c>SensorsSettings</c>.
    /// </summary>
    public string SensorsSettingsJson { get; set; } = "{}";

    /// <summary>
    /// Configurações globais de anexos de tickets (JSON).
    /// Exemplo: {"enabled":true,"maxFileSizeBytes":10485760,"allowedContentTypes":["image/jpeg","image/png","application/pdf"]}
    /// </summary>
    public string TicketAttachmentSettingsJson { get; set; } = "{}";

    // ── Anexos por nível (comentário x repositório do chamado) ─────────────
    // O anexo de COMENTÁRIO faz parte da conversa (pequeno, com miniatura); o
    // anexo do CHAMADO é o repositório de arquivos grandes (até 2 GB, com upload
    // resumível). Ambos configuráveis aqui e resolvidos por Site > Cliente >
    // Servidor.
    public long AttachmentCommentMaxFileSizeBytes { get; set; } = 10L * 1024 * 1024;
    public string AttachmentCommentAllowedContentTypes { get; set; } =
        "image/jpeg,image/png,image/webp,application/pdf";
    public long AttachmentTicketMaxFileSizeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public string AttachmentTicketAllowedContentTypes { get; set; } =
        "image/jpeg,image/png,image/webp,application/pdf,application/zip," +
        "application/x-7z-compressed,application/x-msdownload,text/plain,text/csv," +
        "application/octet-stream";
    /// <summary>Cota total de anexos por chamado (0 = sem cota).</summary>
    public long AttachmentTicketTotalQuotaBytes { get; set; }

    /// <summary>
    /// Configuração global dos processamentos em segundo plano (JSON): ciclo das
    /// métricas por atendente e ciclo de triagem por IA. Herdada por clientes
    /// (ver BackgroundProcessingSettingsJson em ClientConfiguration).
    /// </summary>
    public string BackgroundProcessingSettingsJson { get; set; } = "{}";

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

    /// <summary>
    /// Usa o object storage INTERNO deste servidor (RustFS): as credenciais e o
    /// endpoint de loopback vem do env do projeto (ObjectStorage__*), enquanto o
    /// Endpoint/Bucket/Regiao desta configuracao definem a URL PUBLICA usada nas
    /// URLs pre-assinadas (o host que o agent e o navegador alcancam).
    /// </summary>
    public bool ObjectStorageInternal { get; set; }

    /// <summary>
    /// Somente leitura para o cliente: indica que há um segredo de storage configurado,
    /// sem expor o valor cifrado. Não é persistido.
    /// </summary>
    [NotMapped]
    public bool ObjectStorageSecretKeyConfigured { get; set; }

    /// <summary>
    /// Somente leitura para o cliente: indica que há uma ApiKey de IA configurada,
    /// sem expor o valor cifrado. Não é persistido.
    /// </summary>
    [NotMapped]
    public bool AiApiKeyConfigured { get; set; }

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

    /// <summary>
    /// Política de auto-ticket deste nível (JSON, ver AutoTicketPolicy).
    /// Null/vazio = HERDA integralmente do nível superior (Server -> Client -> Site).
    /// Preenchido = sobrescreve apenas os campos presentes no blob.
    /// </summary>
    public string? AutoTicketPolicyJson { get; set; } = string.Empty;


    // ============ Gerenciador de Patches (Windows Update) ============

    /// <summary>
    /// Kill switch do gerenciador de patches. Desligado, o agente não roda scan
    /// nem aceita comando de patch (o inventário existente é preservado).
    /// </summary>
    public bool WindowsUpdateEnabled { get; set; } = true;

    /// <summary>Intervalo do scan WUA no agente (minutos, mínimo 60).</summary>
    public int WindowsUpdateScanIntervalMinutes { get; set; } = 360;

    // ============ Autorização de acesso remoto (somente desktop) ============

    /// <summary>
    /// Como tratar a conexão de área de trabalho: Off (sem aviso), NotifyOnly
    /// (avisa e conecta) ou Prompt (solicita autorização e aguarda). Herdado por
    /// cliente/site e sobrescrito por grupo de usuários/usuário.
    /// </summary>
    public RemoteConsentMode RemoteConsentMode { get; set; } = RemoteConsentMode.Prompt;

    /// <summary>Segundos de contagem regressiva do prompt (10–600).</summary>
    public int RemoteConsentTimeoutSeconds { get; set; } = 60;

    /// <summary>Ação aplicada quando a contagem termina sem decisão do usuário.</summary>
    public RemoteConsentTimeoutAction RemoteConsentTimeoutAction { get; set; } = RemoteConsentTimeoutAction.Deny;

    /// <summary>Exibe o banner local (operador + tempo + desconectar) durante a sessão de tela.</summary>
    public bool RemoteConsentShowBanner { get; set; } = true;

    /// <summary>
    /// Texto do prompt. Placeholders: {operator}, {client}, {site}, {agent}.
    /// </summary>
    public string RemoteConsentMessage { get; set; } =
        "{operator} solicita acesso remoto ao seu computador. Você autoriza?";

    /// <summary>Texto do banner local. Placeholders: {operator}, {client}, {site}, {agent}.</summary>
    public string RemoteConsentBannerText { get; set; } = "Sessão remota em andamento: {operator}";

    // ============ Auditoria ============

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Versão para rastreamento de mudanças</summary>
    public int Version { get; set; } = 1;

    // ============ Embedding ============

    /// <summary>
    /// Dimensão dos vetores de embedding atualmente persistidos no banco.
    /// Quando AIIntegrationSettings.EmbeddingDimensions for diferente deste valor,
    /// todos os embeddings são invalidados e reprocessados com o novo modelo.
    /// </summary>
    public int CurrentEmbeddingDimensions { get; set; } = 1536;

    /// <summary>
    /// Janela de manutenção do agent (reboot/patches): JSON
    /// { enabled, daysOfWeek, startUtc, durationMinutes, rebootGraceSeconds }.
    /// Null/vazio = herda/usa default (desligado).
    /// </summary>
    // Nullable: coluna jsonb nullable no banco e NULL = "herdar do nivel superior".
    // Ler NULL como string nao-nula causava InvalidCastException no Npgsql (crash
    // do host no startup apos adicionar a coluna em instalacoes existentes).
    public string? MaintenanceWindowJson { get; set; } = string.Empty;

    /// <summary>
    /// [LEGADO] Política de tools MCP do chat do agent (M243): JSON
    /// { mode: "all"|"allowlist", allow: [nomes] }. NÃO é mais lida pelo
    /// payload do agent nem editável nas telas de configuração — a fonte de
    /// verdade é a governança de /settings/mcp-tools (mcp_tool_policies),
    /// da qual o servidor deriva a allowlist enviada ao agent. A coluna
    /// permanece apenas para histórico; uma migração futura pode removê-la.
    /// </summary>
    public string? McpToolPolicyJson { get; set; } = string.Empty;
}
