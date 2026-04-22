# Auditoria de Integracao Meduza_Site x Discovery.Api

Data da auditoria: 2026-04-17

## Resumo Executivo

O site em C:\Projetos\Meduza_Site cobre bem os fluxos centrais do backoffice, mas ainda nao esta com cobertura completa da API em C:\Projetos\SRV_Meduza_2.

Os blocos mais maduros hoje sao autenticacao, MFA, clientes, sites, agentes, tickets core, knowledge base, automacao, relatorios, IAM basico, MeshCentral, configuracoes e inventario. Os gaps de maior impacto estao em realtime, P2P, semantica de configuracao local versus effective e em alguns modulos administrativos que ja existem no backend, mas ainda nao tem superficie no site.

### Principais conclusoes

- Integrado e operacional: auth, MFA, profile/security, clientes, sites, agentes, tickets core, knowledge, reports, app store, automacao, logs, notes, IAM principal, MeshCentral, configuracao e inventory.
- Parcial e com risco: hub principal SignalR, status realtime, P2P, configuracao hierarquica, Ticket Alerts, Agent Alerts, Software Store, Deploy Tokens avancado, App Store avancado e preview de relatorios.
- Ausente no site, mas pronto no backend: AgentUpdates, ApiTokens, AutoTicketRules, SlaCalendars, EscalationRules, TicketSavedViews, TicketWatchers, TicketKpi, TicketCustomFields, TicketAutomationLinks, TicketRemoteSessions e Notifications.
- Fora do escopo do site: endpoints de runtime do agent, bootstrap/provisioning do agent e ingestao operacional interna.

## Escopo

Esta auditoria cobre:

- Backend ASP.NET Core em C:\Projetos\SRV_Meduza_2.
- Frontend React/Vite em C:\Projetos\Meduza_Site.
- Endpoints HTTP, hubs SignalR e trilhas realtime/NATS relevantes para o backoffice web.

Nao entram como falha do site:

- Rotas exclusivas do runtime do agent.
- Fluxos de provisao/instalacao do agent sem UX administrativa web prevista.
- Ingestao operacional de monitoring que nao representa, por si so, superficie de backoffice.

## Legenda de Status

- Integrado: existe client/hook/pagina e o fluxo principal esta ligado ao backend.
- Parcial: existe consumo, mas ha contrato fraco, tela incompleta, rota errada, mock, ou cobertura incompleta da superficie.
- Ausente: o backend expõe a funcionalidade, mas nao ha superficie clara no site.
- Fora do Escopo: endpoint de uso interno, runtime do agent, ou sem responsabilidade direta do backoffice web.

## Contratos-Base da API

### Autenticacao e Autorizacao

- A API aplica autenticacao global no pipeline de controllers e exige opt-out explicito para rotas publicas.
- Ha tres trilhas principais: JWT de usuario, API token e token de agent.
- O hub principal de agents exige identidade valida; conexoes anonimas sao abortadas.
- Para browser/NATS protegido, o backend ja expõe emissao de credenciais em POST /api/nats-auth/user/credentials.

Arquivos-chave do backend:

- src/Discovery.Api/Program.cs
- src/Discovery.Api/Filters/AuthFilters.cs
- src/Discovery.Api/Middleware/UserAuthMiddleware.cs
- src/Discovery.Api/Middleware/ApiTokenAuthMiddleware.cs
- src/Discovery.Api/Middleware/AgentAuthMiddleware.cs
- src/Discovery.Api/Hubs/AgentHub.cs

### JSON, Enums e Shape de Payload

- O serializer aceita camelCase e PascalCase no input.
- Enums saem como string.
- Campos null sao omitidos nas respostas.
- O frontend hoje carrega varios normalizadores defensivos porque alguns modulos ainda admitem multiplos shapes no lado do site.

### Paginacao

- Limit/offset: tickets, automacao, scripts e alguns listados administrativos.
- Cursor string: app store.
- Cursor Guid: alguns fluxos de auditoria/software.

### Uploads

- Ticket attachments usam fluxo em duas etapas: presigned-upload e complete-upload.
- Upload multipart classico aparece em artefatos de AgentUpdates.

### Realtime

- Hubs expostos: /hubs/agent, /hubs/notifications e /hubs/remote-debug.
- O site usa /hubs/agent para dashboard/status e /hubs/remote-debug para console remoto.
- O estado de conexao exibido no site ainda nao reflete o estado real do SignalR.

### P2P

- O backend ja tem DTOs canonicos para overview, timeseries, ranking e seed plan.
- O site ainda espera outro shape para parte desse modulo, especialmente overview, ranking e seed plan.

## IDs de Backlog

| ID | Item |
|---|---|
| B00 | Corrigir autenticacao e estado real do SignalR no backoffice |
| B01 | Definir estrategia NATS no browser e usar /api/nats-auth/user/credentials ou remover a trilha |
| B02 | Corrigir fallback REST de comandos para /api/agents/{id}/commands |
| B03 | Alinhar contrato P2P backend x frontend e atualizar dashboard |
| B04 | Corrigir semantica de configuracao local x effective |
| B05 | Remover preview mock de relatorios e usar so a API real |
| B10 | Unificar a camada de configuracao do site |
| B11 | Reduzir fragilidade de contratos, casing e normalizadores defensivos |
| B12 | Remover hardcodes, placeholders e lacunas pequenas em modulos ja expostos |
| B20 | Fechar gaps residuais em modulos ja integrados |
| B21 | Integrar TicketAlertRules no lugar da tela atual incompleta |
| B22 | Completar CRUD de AgentAlerts |
| B23 | Integrar Notifications persistidas ao site |
| B24 | Completar Software Store e lacunas de App Store/Deploy |
| B30 | Criar superficie de AgentUpdates no site |
| B31 | Criar superficie de ApiTokens no site |
| B32 | Criar superficie de AutoTicketRules no site |
| B33 | Criar superficie de SlaCalendars no site |
| B34 | Criar superficie de EscalationRules no site |
| B35 | Criar superficie dos modulos avancados de tickets |
| B40 | Manter matriz de cobertura viva e smoke tests por modulo |

## Matriz de Cobertura por Endpoint

Para manter o documento legivel, endpoints do mesmo fluxo foram agrupados na mesma linha quando compartilham o mesmo client/hook/pagina, o mesmo status e a mesma acao de backlog.

### 1. Auth, MFA e IAM

| Metodo | Endpoint | Backend/Contrato | Site (client/hook/pagina) | Status | Observacoes | Backlog |
|---|---|---|---|---|---|---|
| POST | /api/auth/login | AuthController; LoginRequestDto -> LoginResponseDto | src/api/auth.ts; AuthContext; pages/auth/LoginPage.tsx | Integrado | Fluxo principal de login ligado ao site | - |
| POST | /api/auth/refresh | AuthController; RefreshTokenRequestDto -> TokenPairDto | src/api/auth.ts; AuthContext | Integrado | Refresh automatico ligado ao client HTTP | - |
| POST | /api/auth/logout | AuthController | src/api/auth.ts; AuthContext | Integrado | Logout funcional | - |
| GET | /api/auth/first-access/status | AuthController; FirstAccessStatusDto | src/api/auth.ts; pages/auth/FirstAccessPage.tsx | Integrado | Primeira ativacao coberta | - |
| POST | /api/auth/first-access/complete | AuthController | src/api/auth.ts; pages/auth/FirstAccessPage.tsx | Integrado | Fluxo coberto | - |
| POST | /api/auth/mfa/fido2/begin | AuthController | src/api/auth.ts; pages/auth/MfaAssertionPage.tsx | Integrado | Login MFA coberto | - |
| POST | /api/auth/mfa/fido2/complete | AuthController | src/api/auth.ts; pages/auth/MfaAssertionPage.tsx | Integrado | Login MFA coberto | - |
| POST | /api/auth/mfa/otp/complete | AuthController | src/api/auth.ts; pages/auth/MfaAssertionPage.tsx | Integrado | Login MFA coberto | - |
| GET | /api/mfa/keys | MfaController; MfaKeyDto[] | src/api/auth.ts; hooks/useAuthSecurity.ts; pages/settings/AuthenticationSettingsPage.tsx | Integrado | Lista de chaves MFA ligada ao perfil | - |
| POST | /api/mfa/fido2/register/begin | MfaController | src/api/auth.ts; pages/auth/MfaRegistrationPage.tsx | Integrado | Cadastro de chave FIDO2 coberto | - |
| POST | /api/mfa/fido2/register/complete | MfaController | src/api/auth.ts; pages/auth/MfaRegistrationPage.tsx | Integrado | Cadastro de chave FIDO2 coberto | - |
| POST | /api/mfa/totp/register/begin | MfaController | src/api/auth.ts; pages/auth/MfaRegistrationPage.tsx | Integrado | Cadastro TOTP coberto | - |
| POST | /api/mfa/totp/register/complete | MfaController | src/api/auth.ts; pages/auth/MfaRegistrationPage.tsx | Integrado | Cadastro TOTP coberto | - |
| PATCH | /api/mfa/keys/{keyId}/name | MfaController | src/api/auth.ts; hooks/useAuthSecurity.ts | Integrado | Renomear chave coberto | - |
| DELETE | /api/mfa/keys/{keyId} | MfaController | src/api/auth.ts; hooks/useAuthSecurity.ts | Integrado | Remover chave coberto | - |
| GET | /api/users<br>/api/users/me<br>/api/users/me/security<br>/api/users/{id}/mfa/keys | UsersController; UserDto/UserSummaryDto/MySecurityProfileDto | src/api/iam.ts; hooks/useIdentity.ts; pages/settings/ProfilePage.tsx; pages/settings/AuthenticationSettingsPage.tsx; pages/settings/IamUsersPage.tsx | Integrado | Leitura principal de usuarios e seguranca coberta | - |
| POST | /api/users<br>/api/users/me/change-password<br>/api/users/{id}/change-password | UsersController | src/api/iam.ts; hooks/useIdentity.ts; pages/settings/ProfilePage.tsx; pages/settings/IamUsersPage.tsx | Integrado | Criacao e troca de senha cobertas | - |
| PUT | /api/users/me<br>/api/users/{id} | UsersController | src/api/iam.ts; hooks/useIdentity.ts; pages/settings/ProfilePage.tsx; pages/settings/IamUsersPage.tsx | Integrado | Edicao coberta | - |
| DELETE | /api/users/{id} | UsersController | src/api/iam.ts; hooks/useIdentity.ts; pages/settings/IamUsersPage.tsx | Integrado | Exclusao principal coberta | - |
| GET/DELETE/POST | /api/users/{id}<br>/api/users/{id}/mfa<br>/api/users/{id}/mfa/keys/{keyId}<br>/api/users/{id}/force-password-reset | UsersController | Sem evidencia clara de client/hook/pagina especifica | Parcial | O site cobre o grosso de IAM, mas nao ha evidencia clara dessas operacoes administrativas residuais | B20 |
| GET/POST/PUT/DELETE | /api/roles<br>/api/roles/{id} | RolesController; RoleDto | src/api/iam.ts; hooks/useIdentity.ts; pages/settings/IamRolesPage.tsx | Integrado | CRUD principal de roles coberto | - |
| GET/POST/DELETE | /api/roles/permissions<br>/api/roles/{id}/permissions<br>/api/roles/{id}/permissions/{permissionId} | RolesController; PermissionDto | src/api/iam.ts; hooks/useIdentity.ts; pages/settings/IamRolesPage.tsx | Integrado | Permissoes ligadas ao UI | - |
| GET/POST/PUT/DELETE | /api/user-groups<br>/api/user-groups/{id} | UserGroupsController; GroupDto | src/api/iam.ts; hooks/useIdentity.ts; pages/settings/IamGroupsPage.tsx | Integrado | CRUD principal de grupos coberto | - |
| GET/POST/DELETE | /api/user-groups/{id}/members<br>/api/user-groups/{id}/roles<br>/api/user-groups/{id}/roles/{assignmentId} | UserGroupsController | src/api/iam.ts; hooks/useIdentity.ts; pages/settings/IamGroupsPage.tsx | Integrado | Membros e associacoes cobertos | - |
| GET/POST/DELETE | /api/api-tokens<br>/api/api-tokens/{tokenId} | ApiTokensController | Sem client/hook/pagina no site | Ausente | Backend pronto, sem superficie no site | B31 |
| POST | /api/nats-auth/user/credentials | NatsAuthController; NatsCredentialsRequest | Sem uso no site atual; browser usa URL NATS direta | Ausente | Necessario se o browser for operar com NATS protegido | B01 |
| POST | /api/meshcentral/embed-url | MeshCentralController | src/api/auth.ts ou src/api/iam.ts; telas de suporte/perfil | Integrado | Embed URL de suporte consumido | - |
| GET/POST/PUT/DELETE | /api/meshcentral/rights-profiles<br>/api/meshcentral/rights-profiles/usage | MeshCentralController | src/api/iam.ts; pages/settings/IamMeshProfilesPage.tsx | Integrado | Perfis de direito cobertos | - |
| GET/POST | /api/meshcentral/diagnostics/health<br>/api/meshcentral/identity-sync/backfill<br>/api/meshcentral/node-links/backfill | MeshCentralController | src/api/iam.ts; pages/settings/MeshCentralDiagnosticsPage.tsx; pages/settings/MeshNodeLinksBackfillPage.tsx | Integrado | Operacoes administrativas conectadas | - |
| GET/POST | /api/meshcentral/group-policy/sites/{siteId}/status<br>/api/meshcentral/group-policy/reconcile | MeshCentralController | src/api/iam.ts; telas MeshCentral no settings | Integrado | Reconcile/status cobertos | - |

### 2. Core Administrativo e Configuracao

| Metodo | Endpoint | Backend/Contrato | Site (client/hook/pagina) | Status | Observacoes | Backlog |
|---|---|---|---|---|---|---|
| GET/POST/PUT/DELETE | /api/clients<br>/api/clients/{id} | ClientsController; entidade Client | src/api/clients.ts; hooks/useClients.ts; pages/clients/ClientList.tsx; pages/clients/ClientDetail.tsx | Integrado | CRUD principal coberto | - |
| GET/POST/PUT/DELETE | /api/clients/{clientId}/sites<br>/api/clients/{clientId}/sites/{id} | SitesController; entidade Site | src/api/sites.ts; hooks/useSites.ts; pages/clients/SiteList.tsx; pages/clients/SiteDetail.tsx | Integrado | CRUD principal coberto | - |
| GET/POST/PUT/DELETE | /api/departments/global<br>/api/departments<br>/api/departments/{id} | DepartmentsController | src/api/departments.ts; hooks/useDepartments.ts; pages/settings/DepartmentSettings.tsx | Integrado | Cobertura administrativa boa | - |
| GET | /api/dashboard/global/summary<br>/api/clients/{clientId}/dashboard/summary<br>/api/clients/{clientId}/sites/{siteId}/dashboard/summary | DashboardController; DashboardSummaryDto | src/api/dashboard.ts; hooks/useDashboardSummary.ts; pages/Dashboard.tsx; ClientDetail/SiteDetail | Integrado | Summary REST coberto | - |
| GET/POST/PUT/DELETE | /api/custom-fields/definitions<br>/api/custom-fields/definitions/{id} | CustomFieldsController; definicoes | src/api/custom-fields.ts; hooks/useCustomFields.ts; pages/settings/CustomFieldsSettings.tsx | Integrado | CRUD de definicoes coberto | - |
| GET/PUT | /api/custom-fields/values/{scopeType}<br>/api/custom-fields/values/{definitionId} | CustomFieldsController; CustomFieldResolvedValueDto | src/api/custom-fields.ts; hooks/useCustomFields.ts; paginas de settings e detalhe | Integrado | Leitura/escrita de valores usada pelo site | - |
| GET | /api/custom-fields/schema/{scopeType} | CustomFieldsController; CustomFieldSchemaItemDto[] | Sem uso claro no site atual | Ausente | Backend ja suporta schema enriquecido, util para formularios dinamicos | B20 |
| GET/PUT/PATCH/POST | /api/configurations/server<br>/api/configurations/server/reset<br>/api/configurations/server/metadata<br>/api/configurations/server/reporting<br>/api/configurations/server/ticket-attachments<br>/api/configurations/server/object-storage/test<br>/api/configurations/server/nats/test<br>/api/configurations/server/nats/generate-account-key | ConfigurationsController; ServerConfiguration e payloads auxiliares | src/services/configurationApi.ts; src/api/configuration.ts; hooks/useConfigurationApi.ts; pages/settings/ServerConfigurationPage.tsx | Integrado | Bloco principal do settings de servidor esta ligado | - |
| PATCH | /api/configurations/server/nats | ConfigurationsController; NatsSettingsRequest | Sem evidencia clara de rota dedicada no site | Parcial | Parte dos campos pode ser editada via PATCH generico; a rota especifica nao esta claramente consumida | B20 |
| GET/PUT/PATCH/DELETE/POST | /api/configurations/clients/{clientId}<br>/api/configurations/clients/{clientId}/effective<br>/api/configurations/clients/{clientId}/metadata<br>/api/configurations/clients/{clientId}/reset/{propertyName}<br>/api/configurations/sites/{siteId}<br>/api/configurations/sites/{siteId}/effective<br>/api/configurations/sites/{siteId}/metadata<br>/api/configurations/sites/{siteId}/reset/{propertyName} | ConfigurationsController; ResolvedConfiguration/metadata | src/services/configurationApi.ts; src/api/configuration.ts; hooks/useConfigurationApi.ts; pages/settings/ClientConfigurationPage.tsx; pages/settings/SiteConfigurationPage.tsx | Parcial | O site consome esses endpoints, mas o backend retorna effective tanto no local quanto no effective, quebrando a semantica de heranca esperada pela UI | B04, B10 |
| GET | /api/configuration-audit<br>/api/configuration-audit/{entityType}/{entityId}<br>/api/configuration-audit/{entityType}/{entityId}/field/{fieldName}<br>/api/configuration-audit/by-user/{username}<br>/api/configuration-audit/report | ConfigurationAuditController | src/services/configurationApi.ts; hooks/useConfigurationApi.ts; pages/settings/ConfigurationAudit.tsx | Integrado | Auditoria de configuracao coberta | - |
| GET/POST | /api/logs | LogsController | src/api/logs.ts; hooks/useLogs.ts; pages/logs/LogViewer.tsx | Integrado | Consulta e envio de logs cobertos | - |
| GET/POST/PUT/DELETE | /api/clients/{clientId}/notes<br>/api/sites/{siteId}/notes<br>/api/agents/{agentId}/notes<br>/api/notes/{id} | NotesController | src/api/notes.ts; hooks/useNotes.ts; components/notes/NotesPanel.tsx | Integrado | Notes integradas em cliente/site/agent | - |
| GET/POST/PATCH | /api/notifications<br>/api/notifications/{id}/read | NotificationsController | Sem client/hook/pagina; sino atual usa estado local | Ausente | Site usa notificacoes locais para relatorios, nao a camada persistida do backend | B23 |
| GET/POST/PUT/DELETE | /api/workflow/states<br>/api/workflow/states/{id}<br>/api/workflow/transitions<br>/api/workflow/transitions/from/{fromStateId}<br>/api/workflow/transitions/{id} | WorkflowController | src/api/workflow.ts; hooks/useWorkflow.ts; pages/settings/WorkflowSettings.tsx | Integrado | States/transitions conectados | - |
| GET/POST/PUT/DELETE | /api/workflowprofiles/global<br>/api/workflowprofiles<br>/api/workflowprofiles/by-department/{departmentId}<br>/api/workflowprofiles/{id} | WorkflowProfilesController | src/api/workflowProfiles.ts; hooks/useWorkflowProfiles.ts; pages/settings/WorkflowProfileSettings.tsx | Integrado | Perfis de workflow conectados | - |

### 3. Agentes, Deploy, Inventory, Realtime e P2P

| Metodo | Endpoint | Backend/Contrato | Site (client/hook/pagina) | Status | Observacoes | Backlog |
|---|---|---|---|---|---|---|
| GET/PUT/DELETE | /api/agents/by-site/{siteId}<br>/api/agents/by-client/{clientId}<br>/api/agents/{id} | AgentsController; entidade Agent | src/api/agents.ts; hooks/useAgents.ts; pages/agents/AgentList.tsx; pages/agents/AgentDetail.tsx | Integrado | Listagem/detalhe/edicao principal cobertos | - |
| POST | /api/agents | AgentsController; CreateAgentRequest | Sem evidencia clara de fluxo no site | Ausente | Nao ha tela clara para criacao manual de agent | B20 |
| POST | /api/agents/{agentId}/approve-zero-touch | AgentsController | Sem superficie clara no site | Ausente | Fluxo operacional ainda sem UI | B20 |
| GET/PUT | /api/agents/{id}/custom-fields<br>/api/agents/{id}/custom-fields/{definitionId} | AgentsController; custom fields do agent | Sem uso claro dedicado | Ausente | O site usa custom fields gerais, mas nao ha evidencia clara dessa superficie por agent via endpoint dedicado | B20 |
| GET | /api/agents/{id}/hardware | AgentsController | src/api/agents.ts; pages/agents/AgentDetail.tsx | Integrado | Inventario de hardware consumido | - |
| GET | /api/agents/{id}/software<br>/api/agents/{id}/software/snapshot | AgentsController | src/api/agents.ts; pages/agents/AgentDetail.tsx; pages/software/SoftwareInventory.tsx | Integrado | Software inventory por agent coberto | - |
| GET/POST | /api/agents/{id}/commands | AgentsController; SendCommandRequest | src/api/agents.ts; src/api/realtime.ts; AgentDetail | Parcial | Fluxo principal existe, mas o fallback REST em src/api/realtime.ts usa /agents/{id}/commands sem /api | B02 |
| POST/POST | /api/agents/{id}/remote-debug/start<br>/api/agents/{id}/remote-debug/{sessionId}/stop | AgentsController; StartRemoteDebugRequest | src/api/agents.ts; pages/agents/RemoteDebugConsole.tsx | Integrado | Remote debug principal ligado ao site | - |
| POST/POST/POST/GET | /api/agents/{id}/automation/tasks/{taskId}/run-now<br>/api/agents/{id}/automation/scripts/{scriptId}/run-now<br>/api/agents/{id}/automation/force-sync<br>/api/agents/{id}/automation/executions | AgentsController | src/api/automation.ts; hooks/useAutomation.ts; pages/automation/* | Integrado | Operacao de automacao por agent coberta | - |
| GET/POST | /api/agents/{id}/tokens | AgentsController | src/api/agents.ts; AgentDetail | Integrado | Leitura e criacao ligadas ao site | - |
| DELETE | /api/agents/{id}/tokens/{tokenId}<br>/api/agents/{id}/tokens | AgentsController | Sem evidencia clara no site | Ausente | O site nao mostra claramente exclusao de tokens | B20 |
| GET/POST/PUT/DELETE | /api/agent-labels/rules<br>/api/agent-labels/rules/{id}<br>/api/agent-labels/rules/{ruleId}/agents | AgentLabelsController | src/modules/agent-labels/api.ts; pages/settings/AgentLabelsSettings.tsx | Integrado | Modulo principal coberto | - |
| GET/POST | /api/agent-labels/agents/{agentId}<br>/api/agent-labels/reprocess<br>/api/agent-labels/rules/dry-run<br>/api/agent-labels/rules/available-custom-fields | AgentLabelsController | src/modules/agent-labels/api.ts; pages/settings/AgentLabelsSettings.tsx; AgentDetail | Integrado | Dry-run e reprocess ligados | - |
| GET/POST/DELETE | /api/agent-alerts<br>/api/agent-alerts/{id}<br>/api/agent-alerts/{id}/dispatch<br>/api/agent-alerts/{id}/create-ticket<br>/api/agent-alerts/scope-options<br>/api/agent-alerts/test-dispatch | AgentAlertsController | src/api/agent-alerts.ts; hooks/useAgentAlerts.ts; pages/tickets/TicketAlertsPage.tsx | Parcial | O site consome scope-options e test-dispatch, mas nao expõe CRUD completo nem a superficie operacional inteira | B22 |
| GET/POST/PUT/DELETE | /api/agent-updates/releases<br>/api/agent-updates/releases/{releaseId}<br>/api/agent-updates/releases/{releaseId}/promote<br>/api/agent-updates/releases/{releaseId}/artifacts<br>/api/agent-updates/artifacts/{artifactId}<br>/api/agent-updates/agents/{agentId}/events<br>/api/agent-updates/dashboard/rollout<br>/api/agent-updates/agents/{agentId}/force-check<br>/api/agent-updates/releases/{releaseId}/build-artifact | AgentUpdatesController | Sem client/hook/pagina no site | Ausente | Backend pronto, sem UX no site | B30 |
| POST | /api/deploy-tokens<br>/api/deploy-tokens/installer-options<br>/api/deploy-tokens/download-installer | DeployTokensController | src/api/deploy-tokens.ts; hooks/useDeployTokens.ts; pages/deploy/DeployTokens.tsx | Integrado | Fluxo principal de gerar/baixar instalador esta ligado | - |
| GET/POST | /api/deploy-tokens<br>/api/deploy-tokens/{id}/download<br>/api/deploy-tokens/{id}/meshcentral-install<br>/api/deploy-tokens/prebuild<br>/api/deploy-tokens/{id}/revoke | DeployTokensController | Sem evidencia clara de cobertura completa | Parcial | Parte avancada do modulo nao esta exposta de forma clara no site | B24 |
| GET | /api/software-inventory<br>/api/software-inventory/snapshot<br>/api/software-inventory/top<br>/api/software-inventory/by-client/{clientId}<br>/api/software-inventory/by-client/{clientId}/snapshot<br>/api/software-inventory/by-site/{siteId}<br>/api/software-inventory/by-site/{siteId}/snapshot<br>/api/software-inventory/by-site/{siteId}/top | SoftwareInventoryController | src/api/software-inventory.ts; hooks/useSoftwareInventory.ts; pages/software/SoftwareInventory.tsx | Integrado | Inventory e top software cobertos | - |
| GET | /api/realtime/stats | RealtimeController; RealtimeStatsResponse | src/api/realtime.ts; dashboard/telemetria | Integrado | Endpoint REST principal de telemetria esta ligado | - |
| GET | /api/realtime/status | RealtimeController | Client existe, mas nao ha evidencia de uso real na UI | Ausente | O site usa um hook local de status, sem consultar esse endpoint | B00 |
| SIGNALR | /hubs/agent | AgentHub | src/hooks/useAgentStatusRealtime.ts; src/hooks/useDashboardRealtime.ts; MainLayout/Dashboard | Parcial | O hub exige identidade valida, mas a trilha do site esta fragil e o status exibido nao mede conexao real | B00 |
| SIGNALR | /hubs/remote-debug | RemoteDebug hub | pages/agents/RemoteDebugConsole.tsx | Integrado | Fluxo principal do console remoto ligado | - |
| SIGNALR | /hubs/notifications | Notification hub | Sem uso no site | Ausente | Backend expõe hub, sem superficie no site | B23 |
| NATS | dashboard.events e subjects relacionados | NATS browser | src/api/nats.ts; useAgentStatusNats.ts | Parcial | O site tenta usar NATS no browser, mas sem emissao de credenciais do backend e com cliente nats.ws carregado dinamicamente | B01 |
| GET | /api/ops/p2p/overview<br>/api/ops/p2p/timeseries<br>/api/ops/p2p/artifacts/distribution<br>/api/ops/p2p/agents/ranking<br>/api/ops/p2p/seed-plan | OpsP2pController; DTOs P2P | src/api/p2p.ts; hooks/useP2P*.ts; pages/Dashboard.tsx | Parcial | O site consome a area, mas espera shape divergente do backend para overview, ranking e seed plan | B03 |

### 4. Tickets e Knowledge

| Metodo | Endpoint | Backend/Contrato | Site (client/hook/pagina) | Status | Observacoes | Backlog |
|---|---|---|---|---|---|---|
| GET/POST/PUT | /api/tickets<br>/api/tickets/{id}<br>/api/tickets/by-client/{clientId} | TicketsController; TicketFilterQuery/CreateTicketRequest/UpdateTicketRequest | src/api/tickets.ts; hooks/useTickets.ts; pages/tickets/TicketList.tsx; pages/tickets/TicketDetail.tsx | Integrado | CRUD principal e filtros base cobertos | - |
| PATCH | /api/tickets/{id}/workflow-state | TicketsController; UpdateWorkflowStateRequest | src/api/tickets.ts; hooks/useTickets.ts; TicketDetail | Integrado | Mudanca de estado coberta | - |
| GET/POST | /api/tickets/{id}/comments | TicketsController; AddCommentRequest | src/api/tickets.ts; TicketDetail | Integrado | Comentarios cobertos | - |
| GET/POST/POST | /api/tickets/{id}/attachments<br>/api/tickets/{id}/attachments/presigned-upload<br>/api/tickets/{id}/attachments/complete-upload | TicketsController; prepare/complete upload | src/api/tickets.ts; TicketDetail | Integrado | Fluxo de anexos em duas etapas coberto | - |
| GET | /api/tickets/{ticketId}/audit/timeline | TicketAuditController | src/api/tickets.ts; TicketDetail | Integrado | Timeline base consumida | - |
| GET | /api/tickets/{ticketId}/audit/timeline/unified<br>/api/tickets/{ticketId}/audit/timeline/activity-type/{activityType}<br>/api/tickets/{ticketId}/audit/timeline/user/{userId}<br>/api/tickets/{ticketId}/audit/timeline/date-range<br>/api/tickets/{ticketId}/audit/timeline/last<br>/api/tickets/{ticketId}/audit/statistics | TicketAuditController | Sem superficie clara no site | Ausente | O site consome apenas a timeline simples | B35 |
| GET/POST/PUT/DELETE | /api/ticket-saved-views<br>/api/ticket-saved-views/{id} | TicketSavedViewsController | Sem client/hook/pagina | Ausente | Backend pronto, sem UX | B35 |
| GET/POST/DELETE | /api/tickets/{ticketId}/watchers<br>/api/tickets/{ticketId}/watchers/{userId} | TicketWatchersController | Sem client/hook/pagina | Ausente | Backend pronto, sem UX | B35 |
| GET | /api/tickets/kpi | TicketKpiController | Sem client/hook/pagina | Ausente | Sem superficie analitica no site | B35 |
| GET/PUT | /api/tickets/{ticketId}/custom-fields<br>/api/tickets/{ticketId}/custom-fields/{definitionId} | TicketCustomFieldsController | Sem client/hook/pagina | Ausente | Campo customizado de ticket nao esta ligado | B35 |
| GET/POST/PATCH | /api/tickets/{ticketId}/automation-links<br>/api/tickets/{ticketId}/automation-links/{linkId}/approve<br>/api/tickets/{ticketId}/automation-links/{linkId}/reject | TicketAutomationLinksController | Sem client/hook/pagina | Ausente | Backend pronto, sem UX | B35 |
| GET/POST/PATCH | /api/tickets/{ticketId}/remote-sessions<br>/api/tickets/{ticketId}/remote-sessions/{sessionId}/end | TicketRemoteSessionsController | Sem client/hook/pagina | Ausente | Backend pronto, sem UX | B35 |
| POST | /api/tickets/{id}/ai/triage<br>/api/tickets/{id}/ai/summarize<br>/api/tickets/{id}/ai/suggest-reply<br>/api/tickets/{id}/ai/draft-kb-article | TicketAiController | Sem client/hook/pagina | Ausente | Capacidades de IA em tickets ainda nao expostas | B35 |
| GET | /api/tickets/{ticketId}/sla/status<br>/api/tickets/{ticketId}/sla/details | TicketSlaController | src/api/tickets.ts; TicketDetail | Integrado | Status SLA e detalhes cobertos | - |
| GET/POST/PUT/PATCH/DELETE | /api/ticket-alert-rules<br>/api/ticket-alert-rules/{id}<br>/api/ticket-alert-rules/by-workflow-state/{workflowStateId}<br>/api/ticket-alert-rules/{id}/toggle | TicketAlertRulesController | A tela atual usa agent-alerts, nao esse modulo | Ausente | A pagina TicketAlertsPage esta ligada ao modulo errado | B21 |
| GET/POST/PUT/DELETE | /api/knowledge<br>/api/knowledge/{id}<br>/api/knowledge/search | KnowledgeController; ArticleResponse/KbSearchResult | src/api/knowledge.ts; hooks/useKnowledge.ts; pages/knowledge/KnowledgeList.tsx; KnowledgeEditor.tsx | Integrado | CRUD/search principal ligado | - |
| POST | /api/knowledge/{id}/publish<br>/api/knowledge/{id}/unpublish | KnowledgeController | src/api/knowledge.ts; KnowledgeEditor | Integrado | Publicacao/unpublish cobertos | - |
| GET/POST/DELETE | /api/tickets/{ticketId}/knowledge<br>/api/tickets/{ticketId}/knowledge/{articleId}<br>/api/tickets/{ticketId}/knowledge/suggest | KnowledgeController | src/api/knowledge.ts; TicketDetail/Knowledge | Integrado | Vinculo ticket-artigo e sugestoes ligados | - |
| POST | /api/tickets/{ticketId}/knowledge/{articleId}/feedback | KnowledgeController | Sem superficie clara no site | Ausente | Feedback de artigo relacionado a ticket nao esta exposto | B35 |

### 5. Automacao, App Store, Reports e Regras Operacionais

| Metodo | Endpoint | Backend/Contrato | Site (client/hook/pagina) | Status | Observacoes | Backlog |
|---|---|---|---|---|---|---|
| GET/POST/PUT/DELETE | /api/automation/scripts<br>/api/automation/scripts/{id} | AutomationScriptsController; AutomationScriptDetailDto | src/api/automation.ts; hooks/useAutomation.ts; pages/automation/AutomationScriptsPage.tsx | Integrado | CRUD principal ligado | - |
| GET | /api/automation/scripts/{id}/consume<br>/api/automation/scripts/{id}/audit | AutomationScriptsController | src/api/automation.ts; hooks/useAutomation.ts; AutomationScriptsPage/AutomationAuditPage | Integrado | Consume e auditoria expostos | - |
| GET/POST/PUT/DELETE | /api/automation/tasks<br>/api/automation/tasks/{id} | AutomationTasksController; AutomationTaskDetailDto | src/api/automation.ts; hooks/useAutomation.ts; pages/automation/AutomationTasksPage.tsx | Integrado | CRUD principal ligado | - |
| POST/GET/GET | /api/automation/tasks/{id}/restore<br>/api/automation/tasks/{id}/audit<br>/api/automation/tasks/{id}/preview-agents | AutomationTasksController | src/api/automation.ts; hooks/useAutomation.ts; pages/automation/AutomationAuditPage.tsx | Integrado | Restore/audit/preview ligados | - |
| GET/POST/DELETE | /api/app-store/catalog<br>/api/app-store/catalog/{packageId}<br>/api/app-store/approvals<br>/api/app-store/approvals/{ruleId}<br>/api/app-store/approvals/audit<br>/api/app-store/effective<br>/api/app-store/sync | AppStoreController; DTOs App Store | src/api/app-store.ts; hooks/useAppStore.ts; pages/software/SoftwareStore.tsx | Integrado | Fluxo principal de catalogo/aprovacoes/effective esta ligado | - |
| POST/GET/GET | /api/app-store/catalog/custom<br>/api/app-store/diff/effective<br>/api/app-store/diff/{packageId} | AppStoreController | Sem evidencia clara de superficie completa no site | Parcial | Recursos avancados de diff/customizacao nao aparecem claramente na UI atual | B24 |
| GET/POST/PUT/DELETE | /api/reports/datasets<br>/api/reports/layout-schema<br>/api/reports/autocomplete<br>/api/reports/templates<br>/api/reports/templates/{id}<br>/api/reports/templates/{id}/history<br>/api/reports/run<br>/api/reports/executions<br>/api/reports/executions/{id}<br>/api/reports/executions/{id}/download | ReportsController; DTOs de relatorio | src/api/reports.ts; hooks/useReport*.ts; pages/reports/* | Integrado | Fluxo principal de templates, execucao e download esta ligado | - |
| POST/GET | /api/reports/preview<br>/api/reports/executions/{id}/download-stream | ReportsController | src/api/reports.ts; pages/reports/* | Parcial | Preview usa API real, mas o client ainda mantem fallback mock local; download-stream nao aparece no site | B05 |
| GET/POST/PUT/PATCH/DELETE | /api/auto-ticket-rules<br>/api/auto-ticket-rules/{id}<br>/api/auto-ticket-rules/{id}/enable<br>/api/auto-ticket-rules/{id}/disable<br>/api/auto-ticket-rules/{id}/dry-run<br>/api/auto-ticket-rules/seed-defaults<br>/api/auto-ticket-rules/{id}/stats | AutoTicketRulesController | Sem client/hook/pagina | Ausente | Backend pronto, sem UX | B32 |
| GET/POST/PUT/DELETE | /api/sla-calendars<br>/api/sla-calendars/{id}<br>/api/sla-calendars/{id}/holidays<br>/api/sla-calendars/{id}/holidays/{holidayId} | SlaCalendarsController | Sem client/hook/pagina | Ausente | Backend pronto, sem UX | B33 |
| GET/POST/PUT/DELETE | /api/escalation-rules<br>/api/escalation-rules/by-profile/{workflowProfileId}<br>/api/escalation-rules/{id} | EscalationRulesController | Sem client/hook/pagina | Ausente | Backend pronto, sem UX | B34 |

### 6. Fora do Escopo do Site

| Metodo | Endpoint | Backend/Contrato | Site (client/hook/pagina) | Status | Observacoes | Backlog |
|---|---|---|---|---|---|---|
| POST/POST | /api/agent-install/register<br>/api/agent-install/{agentId}/token | AgentInstallController | Nao aplicavel | Fora do Escopo | Provisioning/instalacao do agent | N/A |
| GET/POST/GET/POST/POST/POST/POST/GET/GET/POST/POST/GET/GET/POST/GET/POST/GET/GET/POST/GET/POST/GET/POST/POST/POST/GET/GET/POST/GET/GET/PUT/GET/POST/GET/POST/GET/POST/GET/POST/GET/POST/GET/POST | /api/agent-auth/me/* e /api/agent-auth/knowledge* | AgentAuthController | Nao aplicavel | Fora do Escopo | Runtime do agent, sync, update, AI agent-side, comandos e ingestao de inventario | N/A |
| GET/POST/GET | /api/agent-auth/me/p2p-seed-plan<br>/api/agent-auth/me/p2p-telemetry<br>/api/agent-auth/me/p2p-distribution-status | AgentP2pController | Nao aplicavel | Fora do Escopo | Runtime do agent para P2P | N/A |
| POST/POST/GET | /api/monitoring-events<br>/api/monitoring-events/{id}/evaluate<br>/api/monitoring-events/{id}/auto-ticket-decisions | MonitoringEventsController | Nao aplicavel | Fora do Escopo | Ingestao operacional/interna, sem obrigacao direta de UI | N/A |

## Backlog Quebrado por Epico

### Epico 0 - Corrigir contratos base e trilhas quebradas

#### B00 - Corrigir autenticacao e estado real do SignalR no backoffice

- Objetivo: garantir que o site conecte autenticado ao /hubs/agent e reflita o estado real da conexao na UI.
- Afeta: src/hooks/useAgentStatusRealtime.ts, src/hooks/useDashboardRealtime.ts, src/hooks/useRealtimeStatus.ts, integracao com token do frontend e contrato do hub.
- Dependencias: nenhuma.
- Criterios de aceite:
  - Dashboard e tela de agente recebem eventos AgentStatusChanged, DashboardEvent e CommandCompleted com usuario autenticado.
  - Indicador de status do site mostra estado real do socket, nao apenas configuracao.
  - Falha de conexao nao produz falso positivo de conectado.

#### B01 - Definir estrategia NATS no browser

- Objetivo: decidir entre suportar NATS no browser com credenciais do backend ou remover essa trilha e manter SignalR como canal principal.
- Afeta: src/api/nats.ts, useAgentStatusNats.ts, NatsAuthController.
- Dependencias: B00.
- Criterios de aceite:
  - Se NATS browser permanecer, o site usa POST /api/nats-auth/user/credentials e conecta com credenciais validas.
  - Se NATS browser sair, nao resta codigo morto com falso fallback operacional.

#### B02 - Corrigir fallback REST de comandos

- Objetivo: ajustar o fallback de src/api/realtime.ts para usar /api/agents/{id}/commands.
- Dependencias: nenhuma.
- Criterios de aceite:
  - Com SignalR/NATS indisponivel, o envio de comando cai no endpoint correto e nao retorna 404.

#### B03 - Alinhar contrato P2P backend x frontend

- Objetivo: usar um unico shape canonico para overview, timeseries, ranking e seed plan.
- Afeta: src/Discovery.Core/DTOs/P2pDtos.cs, src/api/p2p.ts, hooks/useP2P*.ts, Dashboard.tsx.
- Dependencias: nenhuma.
- Criterios de aceite:
  - Todas as consultas P2P do site consomem payload real sem normalizacao ad hoc fragil.
  - Overview, ranking e seed plan exibem dados coerentes com o DTO do backend.

#### B04 - Corrigir semantica de configuracao local x effective

- Objetivo: separar claramente configuracao local de effective, ou alinhar oficialmente a UI ao contrato atual.
- Afeta: ConfigurationsController, src/services/configurationApi.ts, src/api/configuration.ts, src/utils/configurationEditors.ts, paginas de configuracao.
- Dependencias: nenhuma.
- Criterios de aceite:
  - GET local e GET effective possuem semantica distinta, documentada e testada.
  - A UI de heranca nao induz overrides indevidos.

#### B05 - Remover preview mock de relatorios

- Objetivo: eliminar previewReportData()/mock local e depender apenas de /api/reports/preview.
- Afeta: src/api/reports.ts, componentes/paginas de relatorios.
- Dependencias: nenhuma.
- Criterios de aceite:
  - Preview HTML e document usam somente a API real.
  - Nao existe dado simulado em modo normal de operacao.

### Epico 1 - Reduzir fragilidade contratual do frontend

#### B10 - Unificar a camada de configuracao do site

- Objetivo: consolidar src/services/configurationApi.ts e src/api/configuration.ts em uma unica superficie canonica.
- Dependencias: B04.
- Criterios de aceite:
  - Existe um unico client de configuracao.
  - Hooks e paginas deixam de depender de camadas concorrentes.

#### B11 - Reduzir fragilidade de casing, enums e normalizadores defensivos

- Objetivo: simplificar normalizacoes espalhadas em auth, app store, automacao, custom fields e relatorios.
- Dependencias: B03, B04, B10.
- Criterios de aceite:
  - Tipos TS refletem o contrato real do backend.
  - O frontend nao precisa aceitar multiplos shapes conflitantes para os mesmos dados.

#### B12 - Remover hardcodes, placeholders e lacunas pequenas

- Objetivo: eliminar hardcodes de usuario/notas, revisar placeholders e completar pequenas lacunas de UX.
- Dependencias: nenhuma.
- Criterios de aceite:
  - Nao ha valores fixos de usuario ou placeholders operacionais em telas produtivas.
  - Modulos marcados como integracao futura ficam explicitamente fora da navegacao principal ou sao completados.

### Epico 2 - Fechar cobertura funcional das telas atuais

#### B20 - Fechar gaps residuais em modulos ja integrados

- Escopo: operacoes IAM residuais, custom-fields schema, zero-touch approve, agent create, agent custom-fields dedicados, delete de tokens de agent, dedicated patch NATS, e demais endpoints pequenos ainda sem superficie.
- Dependencias: B10 e B11 quando houver impacto de contrato.
- Criterios de aceite:
  - Cada endpoint residual relevante recebe decisao explicita: integrar, esconder, ou documentar como nao suportado.

#### B21 - Integrar TicketAlertRules no lugar da tela atual incompleta

- Objetivo: substituir o acoplamento atual com AgentAlerts por uma tela realmente baseada em TicketAlertRulesController.
- Dependencias: nenhuma.
- Criterios de aceite:
  - CRUD de regras por workflow state funcionando.
  - A pagina de alertas de ticket usa o modulo correto do backend.

#### B22 - Completar CRUD de AgentAlerts

- Objetivo: expor listagem, criacao, detalhe, disparo e create-ticket de AgentAlerts.
- Dependencias: nenhuma.
- Criterios de aceite:
  - O site cobre pelo menos CRUD basico e acao operacional principal do modulo.

#### B23 - Integrar Notifications persistidas ao site

- Objetivo: substituir o sino/localStorage paralelo por NotificationsController e, se necessario, /hubs/notifications.
- Dependencias: B00.
- Criterios de aceite:
  - Notificacoes podem ser lidas, marcadas como lidas e refletidas na UI.

#### B24 - Completar Software Store e lacunas de App Store/Deploy

- Escopo: catalog/custom, diff, recursos avancados da store, e endpoints adicionais de deploy tokens ainda nao expostos.
- Dependencias: B11.
- Criterios de aceite:
  - A Software Store deixa de ser placeholder e cobre os fluxos priorizados.
  - Deploy tokens avancados possuem superficie clara ou decisao de nao suporte.

### Epico 3 - Expandir o backoffice para modulos prontos no backend

#### B30 - AgentUpdates

- Objetivo: criar client, hooks, paginas e rotas para releases, rollout, artifacts, events e force-check.
- Dependencias: B11.
- Criterios de aceite:
  - E possivel visualizar releases, rollout e acionar force-check/promotion conforme permissao.

#### B31 - ApiTokens

- Objetivo: permitir listar, criar e revogar API tokens do usuario/admin no site.
- Dependencias: nenhuma.
- Criterios de aceite:
  - CRUD minimo funcional e alinhado ao backend.

#### B32 - AutoTicketRules

- Objetivo: expor CRUD, enable/disable, dry-run, seed-defaults e stats das regras automaticas.
- Dependencias: nenhuma.
- Criterios de aceite:
  - Modulo completo visivel no backoffice com dry-run e stats.

#### B33 - SlaCalendars

- Objetivo: expor calendarios e feriados de SLA.
- Dependencias: nenhuma.
- Criterios de aceite:
  - CRUD de calendarios e holidays funcional.

#### B34 - EscalationRules

- Objetivo: expor CRUD e consulta por workflow profile.
- Dependencias: B33 opcional, conforme desenho de operacao.
- Criterios de aceite:
  - Regras de escalonamento podem ser gerenciadas via UI.

#### B35 - Modulos avancados de tickets

- Escopo: saved views, watchers, KPI, audit avancado, custom fields por ticket, automation links, remote sessions, AI actions e knowledge feedback.
- Dependencias: B11 e, para alguns fluxos, B21.
- Criterios de aceite:
  - Cada submodulo priorizado tem client, hook, pagina e smoke test manual.

### Epico 4 - Governanca e validacao

#### B40 - Manter matriz de cobertura viva e smoke tests

- Objetivo: manter este documento atualizado e transformar a cobertura em checklist de entrega.
- Dependencias: todas as anteriores, conforme modulo.
- Criterios de aceite:
  - Existe rotina de revisar endpoint -> client -> hook -> pagina a cada entrega.
  - Build/typecheck do site e smoke tests manuais por modulo passam antes de liberar mudancas.

## Ordem Recomendada de Implementacao

1. B00, B02, B03, B04 e B05.
2. B01, B10, B11 e B12.
3. B20, B21, B22, B23 e B24.
4. B30, B31, B32, B33, B34 e B35.
5. B40 como disciplina continua.

## Criterios Gerais de Aceite por Novo Modulo

- Existe client TS dedicado ou reaproveitamento explicito da camada canonica.
- Existe hook React Query ou equivalente coerente com o padrao do site.
- Existe pagina/rota protegida por permissao apropriada.
- Loading, erro, estado vazio e sucesso estao tratados.
- O payload trafegado bate com o contrato real do backend.
- O fluxo foi validado com build/typecheck e smoke test manual.

## Arquivos-Chave Consultados

Backend:

- src/Discovery.Api/Program.cs
- src/Discovery.Api/Hubs/AgentHub.cs
- src/Discovery.Api/Controllers/AuthController.cs
- src/Discovery.Api/Controllers/MfaController.cs
- src/Discovery.Api/Controllers/UsersController.cs
- src/Discovery.Api/Controllers/RolesController.cs
- src/Discovery.Api/Controllers/UserGroupsController.cs
- src/Discovery.Api/Controllers/ClientsController.cs
- src/Discovery.Api/Controllers/SitesController.cs
- src/Discovery.Api/Controllers/AgentsController.cs
- src/Discovery.Api/Controllers/ConfigurationsController.cs
- src/Discovery.Api/Controllers/TicketsController.cs
- src/Discovery.Api/Controllers/KnowledgeController.cs
- src/Discovery.Api/Controllers/AppStoreController.cs
- src/Discovery.Api/Controllers/ReportsController.cs
- src/Discovery.Api/Controllers/AgentUpdatesController.cs
- src/Discovery.Api/Controllers/NatsAuthController.cs
- src/Discovery.Core/DTOs/P2pDtos.cs

Frontend:

- C:\Projetos\Meduza_Site\src\api\client.ts
- C:\Projetos\Meduza_Site\src\api\auth.ts
- C:\Projetos\Meduza_Site\src\api\agents.ts
- C:\Projetos\Meduza_Site\src\api\tickets.ts
- C:\Projetos\Meduza_Site\src\api\reports.ts
- C:\Projetos\Meduza_Site\src\api\app-store.ts
- C:\Projetos\Meduza_Site\src\api\p2p.ts
- C:\Projetos\Meduza_Site\src\api\realtime.ts
- C:\Projetos\Meduza_Site\src\api\nats.ts
- C:\Projetos\Meduza_Site\src\api\iam.ts
- C:\Projetos\Meduza_Site\src\api\custom-fields.ts
- C:\Projetos\Meduza_Site\src\services\configurationApi.ts
- C:\Projetos\Meduza_Site\src\hooks\useAgentStatusRealtime.ts
- C:\Projetos\Meduza_Site\src\hooks\useDashboardRealtime.ts
- C:\Projetos\Meduza_Site\src\hooks\useRealtimeStatus.ts
- C:\Projetos\Meduza_Site\src\router.tsx
