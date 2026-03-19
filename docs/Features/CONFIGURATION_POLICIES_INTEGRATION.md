# Configuration Policies - Integracao com Site de Gerenciamento

## Objetivo

Documentar a integracao do painel de gerenciamento com o modulo de configuracoes hierarquicas, refletindo o estado atual do codigo:

- Server (Global)
- Client
- Site

Inclui:

- endpoints reais disponiveis
- campos existentes por escopo
- campos exclusivos do servidor
- campos herdaveis
- validacoes e formatos de resposta

---

## Base URL

/api/configurations

---

## Modelo de Heranca

Hierarquia:

1. Server
2. Client
3. Site

Regra de resolucao efetiva:

1. Se Site tiver valor local, usa Site.
2. Senao, se Client tiver valor local, usa Client.
3. Senao, usa Server.

Bloqueios:

- Locks sao armazenados em LockedFieldsJson (array JSON de nomes de campos).
- Lock no pai bloqueia sobrescrita no filho.
- Metadata informa, por campo, sourceType e flags canEditAtClient/canEditAtSite/canEditAtAgent.

Mapeamento de origem (ConfigurationPriorityType):

- 0 = Block
- 2 = Global
- 3 = Client
- 4 = Site
- 5 = Agent (reservado para uso futuro)

---

## Endpoints Atuais

### Server

- GET /api/configurations/server
- PUT /api/configurations/server
- PATCH /api/configurations/server
- POST /api/configurations/server/reset
- GET /api/configurations/server/metadata
- GET /api/configurations/server/reporting
- PUT /api/configurations/server/reporting
- GET /api/configurations/server/ticket-attachments
- PUT /api/configurations/server/ticket-attachments
- POST /api/configurations/server/object-storage/test

### Client

- GET /api/configurations/clients/{clientId}
- GET /api/configurations/clients/{clientId}/effective
- GET /api/configurations/clients/{clientId}/metadata
- PUT /api/configurations/clients/{clientId}
- PATCH /api/configurations/clients/{clientId}
- DELETE /api/configurations/clients/{clientId}
- POST /api/configurations/clients/{clientId}/reset/{propertyName}

### Site

- GET /api/configurations/sites/{siteId}
- GET /api/configurations/sites/{siteId}/effective
- GET /api/configurations/sites/{siteId}/metadata
- PUT /api/configurations/sites/{siteId}
- PATCH /api/configurations/sites/{siteId}
- DELETE /api/configurations/sites/{siteId}
- POST /api/configurations/sites/{siteId}/reset/{propertyName}

### Auditoria de Configuracao

Base URL: /api/configuration-audit

- GET /api/configuration-audit
- GET /api/configuration-audit/{entityType}/{entityId}
- GET /api/configuration-audit/{entityType}/{entityId}/field/{fieldName}
- GET /api/configuration-audit/by-user/{username}
- GET /api/configuration-audit/report

---

## Configuracoes Herdaveis (Catalogo Oficial)

Campos gerenciados oficialmente para heranca e metadata:

- RecoveryEnabled
- DiscoveryEnabled
- P2PFilesEnabled
- SupportEnabled
- MeshCentralGroupPolicyProfile
- ChatAIEnabled
- KnowledgeBaseEnabled
- AppStorePolicy
- InventoryIntervalHours
- AutoUpdateSettingsJson
- AIIntegrationSettingsJson
- TicketAttachmentSettingsJson
- AgentHeartbeatIntervalSeconds

Observacao:

- Esses campos aparecem na metadata por escopo e participam da cadeia de locks/heranca.

---

## Configuracoes Exclusivas do Servidor

Campos existentes no ServerConfiguration que nao sao sobrescritos por Client/Site:

- BrandingSettingsJson
- ReportingSettingsJson
- ObjectStorageBucketName
- ObjectStorageEndpoint
- ObjectStorageRegion
- ObjectStorageAccessKey
- ObjectStorageSecretKey
- ObjectStorageUrlTtlHours
- ObjectStorageUsePathStyle
- ObjectStorageSslVerify

Tambem exclusivos no nivel global dentro de AIIntegrationSettings:

- ApiKey
- BaseUrl
- Provider
- EmbeddingModel
- EmbeddingEnabled
- EmbeddingArticlesEnabled
- MSPServers
- TimeoutMs
- RateLimitPerMinute
- TokenBudgetDaily
- CostControlEnabled

---

## Configuracoes de Client e Site

### ClientConfiguration

Campos de override (nullable; semantica de heranca):

- RecoveryEnabled
- DiscoveryEnabled
- P2PFilesEnabled
- SupportEnabled
- MeshCentralGroupPolicyProfile
- ChatAIEnabled
- KnowledgeBaseEnabled
- AppStorePolicy
- AIIntegrationSettingsJson
- InventoryIntervalHours
- AutoUpdateSettingsJson
- AgentHeartbeatIntervalSeconds
- LockedFieldsJson

### SiteConfiguration

Mesmo conjunto de override do Client, mais campos especificos de site:

- Timezone
- Location
- ContactPerson
- ContactEmail
- MeshCentralGroupName
- MeshCentralMeshId
- MeshCentralAppliedGroupPolicyProfile
- MeshCentralAppliedGroupPolicyAt

---

## Tipos de Dados Relevantes para o Frontend

### Enum AppStorePolicyType

- 0 = Disabled
- 1 = PreApproved
- 2 = All

### AutoUpdateSettings (armazenado em AutoUpdateSettingsJson)

- Enabled: bool
- CheckEveryHours: int
- AllowUserDelay: bool
- MaxDelayHours: int
- ForceRestartDelay: bool
- RestartDelayHours: int
- UpdateOnLogon: bool
- MaintenanceWindows: array
- SilentInstall: bool
- AutoRollbackOnFailure: bool

### AIIntegrationSettings (Server)

Principais campos:

- Enabled: bool
- ChatAIEnabled: bool
- KnowledgeBaseEnabled: bool
- MSPServers: string[]
- TimeoutMs: int
- MaxTokensPerRequest: int
- Provider: string
- ApiKey: string? (nunca retornada em claro)
- BaseUrl: string?
- ChatModel: string?
- EmbeddingModel: string?
- PromptTemplate: string?
- Temperature: double
- EmbeddingEnabled: bool
- EmbeddingArticlesEnabled: bool
- MaxHistoryMessages: int
- MaxKbContextTokens: int
- RateLimitPerMinute: int
- TokenBudgetDaily: int
- CostControlEnabled: bool
- MinSimilarityScore: double
- MaxKbChunks: int

### AIIntegrationSettingsOverride (Client/Site)

Sobrescritas permitidas em Client/Site:

- Enabled
- ChatAIEnabled
- KnowledgeBaseEnabled
- ChatModel
- PromptTemplate
- Temperature
- MaxTokensPerRequest
- MaxHistoryMessages
- MaxKbContextTokens
- MaxKbChunks
- MinSimilarityScore

### TicketAttachmentSettings (Server)

- Enabled: bool
- MaxFileSizeBytes: long
- AllowedContentTypes: string[]
- PresignedUploadUrlTtlMinutes: int

---

## Validacoes Atuais no Backend

- InventoryIntervalHours: 1 a 168
- AgentHeartbeatIntervalSeconds: 10 a 3600
- TicketAttachmentSettings:
  - MaxFileSizeBytes > 0
  - MaxFileSizeBytes <= 1GB
  - PresignedUploadUrlTtlMinutes entre 1 e 120
  - AllowedContentTypes nao vazio e sem valores invalidos
- Reporting (PUT /server/reporting): DatabaseRetentionDays e FileRetentionDays precisam estar contidos em AllowedRetentionDays

---

## Padrao de Resposta e Erros

Sucesso:

- GET/PUT/PATCH: 200 com objeto
- DELETE e reset de propriedade: 204 sem body

Erros comuns:

- 400 com payload contendo error ou errors
- 404 quando client/site nao encontrado

Comportamentos relevantes:

- AIIntegrationSettingsJson e respostas efetivas sao sanitizadas para nao expor ApiKey.
- ObjectStorageSecretKey e dados sensiveis sao protegidos no backend.

---

## Observacoes Importantes para o Frontend

1. Parse de campos JSON

- AutoUpdateSettingsJson, AIIntegrationSettingsJson e TicketAttachmentSettingsJson precisam ser tratados como objetos tipados no frontend.

2. Metadata e UX de heranca

- Sempre carregar metadata do escopo antes de renderizar formulario.
- Usar sourceType para badge de origem.
- Desabilitar edicao com base em canEditAtClient/canEditAtSite/canEditAtAgent.

3. Locks

- Exibir claramente quando campo estiver bloqueado por nivel superior.
- Em erro 400 de bloqueio, mostrar mensagem no campo correspondente.

4. Diferenca entre documentacao conceitual e comportamento atual

- Conceito: null em Client/Site representa heranca.
- Comportamento atual de implementacao: bool? pode ser normalizado para false em alguns fluxos de update/reset.
- Recomendacao: tratar esse ponto como comportamento atual da API ate ajuste de regra no backend.

---

## Fluxo Recomendado de Integracao no Site

1. Ler metadata do nivel desejado.
2. Renderizar controles com estado herdado/local/bloqueado.
3. Enviar PATCH apenas com campos alterados.
4. Usar endpoint reset/{propertyName} para retornar para heranca.
5. Recarregar configuracao efetiva apos salvar para atualizar badges e locks.

---

## Checklist de Integracao

1. Implementar consumo de metadata por escopo.
2. Implementar mapeamento fixo de enums (AppStorePolicyType e sourceType).
3. Implementar parse/serialize seguro dos campos JSON.
4. Tratar 400 e 404 com mensagens por campo e contexto.
5. Exibir estado de lock e origem efetiva em todos os campos gerenciados.
6. Validar UI com os endpoints /effective de Client e Site.

---

Ultima atualizacao: 19 de marco de 2026
