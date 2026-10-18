# Matriz de PermissÃµes â€” DiscoveryRMM API

**Gerado em:** 05/05/2026
**Branch:** dev

> Esta matriz documenta todos os endpoints da API com seus requisitos de permissÃ£o.
> `Scope` = escopo de autorizaÃ§Ã£o aplicado: **G**lobal, de **R**ota, ou **I**nline (manual).

---

## ResourceType Ã— ActionType

| ResourceType      | View                                                      | Create            | Edit                                                                          | Delete                                                                                                                     | Execute         |
| ----------------- | --------------------------------------------------------- | ----------------- | ----------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- | --------------- |
| **Agents**        | Labels, info                                              | Regras de label   | Labels, regras                                                                | -                                                                                                                          | -               |
| **Tickets**       | Listar, detalhes                                          | Criar             | Editar, workflow, comentÃ¡rios                                                 | -                                                                                                                          | -               |
| **Clients**       | Listar, detalhes                                          | Criar             | Editar, custom fields                                                         | Excluir                                                                                                                    | -               |
| **Sites**         | âœ– (nÃ£o tem controller prÃ³prio)                            | -                 | -                                                                             | -                                                                                                                          | -               |
| **Reports**       | Datasets, templates                                       | Criar template    | -                                                                             | -                                                                                                                          | -               |
| **ServerConfig**  | Todas GETs config                                         | Custom fields     | Todas PUTs/PATCHs config                                                      | Custom fields                                                                                                              | -               |
| **ClientConfig**  | âœ– (nÃ£o usado)                                             | -                 | -                                                                             | -                                                                                                                          | -               |
| **SiteConfig**    | MeshCentral site                                          | -                 | MeshCentral site                                                              | -                                                                                                                          | -               |
| **Users**         | Listar, MFA keys                                          | Criar, API tokens | Editar, reset MFA, senha admin                                                | Desativar, revogar token                                                                                                   | -               |
| **Automation**    | Scripts, tasks                                            | Scripts, tasks    | Scripts, tasks                                                                | Scripts, tasks                                                                                                             | Consumir script |
| **Deployment**    | Tokens, updates                                           | Tokens, updates   | Tokens, updates                                                               | Revogar token                                                                                                              | -               |
| **KnowledgeBase** | âœ– (nÃ£o tem controller)                                    | -                 | -                                                                             | -                                                                                                                          | -               |
| **AiChat**        | âœ” (`TicketAiController`, `AgentAuthController.AiChat.cs`) | `Execute`         | `/api/v{version}/tickets/{id}/ai/*`, `/api/v{version}/agent-auth/me/ai-chat*` | Agentes autenticados podem usar chat IA; usuÃ¡rios com permissÃ£o podem gerar triagem/resumo/sugestÃ£o de resposta em tickets |
| **AppStore**      | CatÃ¡logo, diffs                                           | -                 | Custom, approvals, sync                                                       | Remover approval                                                                                                           | -               |
| **Logs**          | Audit, monitoring                                         | -                 | -                                                                             | -                                                                                                                          | Ingest events   |
| **Dashboard**     | Summaries                                                 | -                 | -                                                                             | -                                                                                                                          | -               |
| **RemoteDebug**   | âœ– (nÃ£o tem controller)                                    | -                 | -                                                                             | -                                                                                                                          | -               |
| **Patches**       | InventÃ¡rio/overview/aprovaÃ§Ãµes/polÃ­tica                   | -                 | Aprovar/recusar e editar polÃ­tica                                             | Remover aprovaÃ§Ã£o                                                                                                          | Instalar/scan/enforcement no SO |

---

## Endpoints Ã— PermissÃµes

### Agent Install (token de deploy)

| Endpoint                                     | Auth                                         | PermissÃ£o                                |
| -------------------------------------------- | -------------------------------------------- | ---------------------------------------- |
| `POST /api/v1/agent-install/register`        | `Bearer {deploy_token}` (escopo client/site) | â€” (sem RBAC; validaÃ§Ã£o por deploy token) |
| `POST /api/v1/agent-install/{agentId}/token` | `Bearer {deploy_token}` (escopo client/site) | â€” (sem RBAC; validaÃ§Ã£o por deploy token) |

### Agent Auth (token mdz\_)

| Endpoint                                   | Auth                                | PermissÃ£o               |
| ------------------------------------------ | ----------------------------------- | ----------------------- |
| `GET /api/v1/agent-auth/me/configuration`  | `Bearer mdz_{token}` + `X-Agent-ID` | â€” (identidade do agent) |
| `POST /api/v1/agent-auth/me/p2p/bootstrap` | `Bearer mdz_{token}` + `X-Agent-ID` | â€” (identidade do agent) |
| `GET /api/v1/agent-auth/me/sync-manifest`  | `Bearer mdz_{token}` + `X-Agent-ID` | â€” (identidade do agent) |

### NATS Auth (usuÃ¡rio dashboard)

| Endpoint                                  | Auth                | PermissÃ£o                                                    |
| ----------------------------------------- | ------------------- | ------------------------------------------------------------ |
| `POST /api/v1/nats-auth/user/credentials` | `[RequireUserAuth]` | `Dashboard.View` (escopo resolvido por `IPermissionService`) |

### NATS ACL (perfil tÃ©cnico)

| Perfil          | Subscribe Allow                                                                                                                                                                                                                                  | Publish Allow                                                                                                                                                                 |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AgentIdentity` | `tenant.{c}.site.{s}.agent.{a}.command`, `tenant.{c}.site.{s}.agents.command`, `tenant.{c}.agents.command`, `tenant.global.agents.command`, `tenant.global.pong`, `tenant.{c}.site.{s}.agent.{a}.sync.ping`, `tenant.{c}.site.{s}.p2p.discovery` | `tenant.{c}.site.{s}.agent.{a}.heartbeat`, `tenant.{c}.site.{s}.agent.{a}.result`, `tenant.{c}.site.{s}.agent.{a}.hardware`, `tenant.{c}.site.{s}.agent.{a}.remote-debug.log` |

### Endpoints Legados Removidos

| Endpoint                                      | Status                                                     |
| --------------------------------------------- | ---------------------------------------------------------- |
| `POST /api/v1/agent-auth/me/nats-credentials` | **Removido** (fluxo atual: `agent_token` via auth callout) |

### Auth (pÃºblico)

| Endpoint                                  | Auth                             | PermissÃ£o           |
| ----------------------------------------- | -------------------------------- | ------------------- |
| `POST /api/v1/auth/login`                 | `[AllowAnonymous]`               | â€”                   |
| `POST /api/v1/auth/refresh`               | `[AllowAnonymous]`               | â€”                   |
| `POST /api/v1/auth/logout`                | `[RequireUserAuth]`              | â€” (qualquer sessÃ£o) |
| `POST /api/v1/auth/mfa/fido2/begin`       | `[RequireMfaPending]`            | â€”                   |
| `POST /api/v1/auth/mfa/fido2/complete`    | `[RequireMfaPending]`            | â€”                   |
| `POST /api/v1/auth/mfa/otp/complete`      | `[RequireMfaPending]`            | â€”                   |
| `POST /api/v1/auth/first-access/complete` | `[RequireMfaSetupOrFullSession]` | â€”                   |

### MFA (registro pÃºblico, gerenciamento autenticado)

| Endpoint                                   | Auth                                                  | PermissÃ£o           |
| ------------------------------------------ | ----------------------------------------------------- | ------------------- |
| `GET /api/v1/mfa/keys`                     | `[RequireUserAuth]`                                   | â€” (prÃ³prio usuÃ¡rio) |
| `POST /api/v1/mfa/fido2/register/begin`    | `[AllowAnonymous]` + `[RequireMfaSetupOrFullSession]` | â€”                   |
| `POST /api/v1/mfa/fido2/register/complete` | `[AllowAnonymous]` + `[RequireMfaSetupOrFullSession]` | â€”                   |
| `POST /api/v1/mfa/totp/register/begin`     | `[AllowAnonymous]` + `[RequireMfaSetupOrFullSession]` | â€”                   |
| `POST /api/v1/mfa/totp/register/complete`  | `[AllowAnonymous]` + `[RequireMfaSetupOrFullSession]` | â€”                   |
| `DELETE /api/v1/mfa/keys/{keyId}`          | `[RequireUserAuth]`                                   | â€” (prÃ³prias chaves) |
| `PATCH /api/v1/mfa/keys/{keyId}/name`      | `[RequireUserAuth]`                                   | â€” (prÃ³prias chaves) |

### Users

| Endpoint                                       | PermissÃ£o          | Scope |
| ---------------------------------------------- | ------------------ | ----- |
| `GET /api/v1/users`                            | `Users.View`       | G     |
| `GET /api/v1/users/{id}`                       | `Users.View`       | G     |
| `GET /api/v1/users/me`                         | â€” (prÃ³prio perfil) | G     |
| `PUT /api/v1/users/me`                         | â€” (prÃ³prio perfil) | G     |
| `GET /api/v1/users/me/security`                | â€” (prÃ³prio perfil) | G     |
| `POST /api/v1/users/me/change-password`        | â€” (prÃ³pria senha)  | G     |
| `POST /api/v1/users`                           | `Users.Create`     | G     |
| `PUT /api/v1/users/{id}`                       | `Users.Edit`       | G     |
| `POST /api/v1/users/{id}/change-password`      | `Users.Edit`       | G     |
| `GET /api/v1/users/{id}/mfa/keys`              | `Users.View`       | G     |
| `DELETE /api/v1/users/{id}/mfa`                | `Users.Edit`       | G     |
| `DELETE /api/v1/users/{id}/mfa/keys/{keyId}`   | `Users.Edit`       | G     |
| `POST /api/v1/users/{id}/force-password-reset` | `Users.Edit`       | G     |
| `DELETE /api/v1/users/{id}`                    | `Users.Delete`     | G     |

### Roles

| Endpoint                                         | PermissÃ£o      | Scope |
| ------------------------------------------------ | -------------- | ----- |
| `GET /api/v1/roles`                              | `Users.View`   | G     |
| `GET /api/v1/roles/{id}`                         | `Users.View`   | G     |
| `POST /api/v1/roles`                             | `Users.Create` | G     |
| `PUT /api/v1/roles/{id}`                         | `Users.Edit`   | G     |
| `DELETE /api/v1/roles/{id}`                      | `Users.Delete` | G     |
| `GET /api/v1/roles/{id}/permissions`             | `Users.View`   | G     |
| `GET /api/v1/roles/permissions`                  | `Users.View`   | G     |
| `POST /api/v1/roles/{id}/permissions`            | `Users.Edit`   | G     |
| `DELETE /api/v1/roles/{id}/permissions/{permId}` | `Users.Edit`   | G     |

### User Groups

| Endpoint                                               | PermissÃ£o      | Scope |
| ------------------------------------------------------ | -------------- | ----- |
| `GET /api/v1/user-groups`                              | `Users.View`   | G     |
| `GET /api/v1/user-groups/{id}`                         | `Users.View`   | G     |
| `POST /api/v1/user-groups`                             | `Users.Create` | G     |
| `PUT /api/v1/user-groups/{id}`                         | `Users.Edit`   | G     |
| `DELETE /api/v1/user-groups/{id}`                      | `Users.Delete` | G     |
| `GET /api/v1/user-groups/{id}/members`                 | `Users.View`   | G     |
| `POST /api/v1/user-groups/{id}/members`                | `Users.Edit`   | G     |
| `DELETE /api/v1/user-groups/{id}/members/{userId}`     | `Users.Edit`   | G     |
| `GET /api/v1/user-groups/{id}/roles`                   | `Users.View`   | G     |
| `POST /api/v1/user-groups/{id}/roles`                  | `Users.Edit`   | G     |
| `DELETE /api/v1/user-groups/{id}/roles/{assignmentId}` | `Users.Edit`   | G     |

### API Tokens

| Endpoint                              | PermissÃ£o           | Scope |
| ------------------------------------- | ------------------- | ----- |
| `GET /api/v1/api-tokens`              | â€” (prÃ³prios tokens) | G     |
| `POST /api/v1/api-tokens`             | `Users.Create`      | G     |
| `DELETE /api/v1/api-tokens/{tokenId}` | `Users.Delete`      | G     |

### Clients

| Endpoint                                         | PermissÃ£o        | Scope |
| ------------------------------------------------ | ---------------- | ----- |
| `GET /api/v1/clients`                            | `Clients.View`   | G     |
| `GET /api/v1/clients/{id}`                       | `Clients.View`   | G     |
| `POST /api/v1/clients`                           | `Clients.Create` | G     |
| `PUT /api/v1/clients/{id}`                       | `Clients.Edit`   | G     |
| `DELETE /api/v1/clients/{id}`                    | `Clients.Delete` | G     |
| `GET /api/v1/clients/{id}/custom-fields`         | `Clients.View`   | G     |
| `PUT /api/v1/clients/{id}/custom-fields/{defId}` | `Clients.Edit`   | G     |

### Tickets

| Endpoint                                                 | PermissÃ£o        | Scope |
| -------------------------------------------------------- | ---------------- | ----- |
| `GET /api/v1/tickets`                                    | `Tickets.View`   | G     |
| `GET /api/v1/tickets/by-client/{clientId}`               | `Tickets.View`   | R     |
| `GET /api/v1/tickets/{id}`                               | `Tickets.View`   | G     |
| `POST /api/v1/tickets`                                   | `Tickets.Create` | G     |
| `PUT /api/v1/tickets/{id}`                               | `Tickets.Edit`   | G     |
| `PATCH /api/v1/tickets/{id}/workflow-state`              | `Tickets.Edit`   | G     |
| `GET /api/v1/tickets/{id}/comments`                      | `Tickets.View`   | G     |
| `POST /api/v1/tickets/{id}/comments`                     | `Tickets.Edit`   | G     |
| `GET /api/v1/tickets/{id}/attachments`                   | `Tickets.View`   | G     |
| `POST /api/v1/tickets/{id}/attachments/presigned-upload` | `Tickets.Edit`   | G     |
| `POST /api/v1/tickets/{id}/attachments/complete-upload`  | `Tickets.Edit`   | G     |

#### Chat de suporte vinculado ao chamado

O chat reusa as permissÃµes jÃ¡ existentes do chamado â€” **nÃ£o** hÃ¡ permissÃ£o nova.
Todos os endpoints user-auth validam o escopo do chamado
(`CanAccessTicketAsync`/`ITicketAccessGuard`) alÃ©m da permissÃ£o.

| Endpoint                                                                        | PermissÃ£o       | Scope |
| ------------------------------------------------------------------------------- | --------------- | ----- |
| `GET  /api/v1/tickets/{id}/chat/sessions/current`                              | `Tickets.View`  | G     |
| `GET  /api/v1/tickets/{id}/chat/sessions`                                      | `Tickets.View`  | G     |
| `GET  /api/v1/tickets/{id}/chat/sessions/{sessionId}/messages`                 | `Tickets.View`  | G     |
| `GET  /api/v1/tickets/{id}/chat/attachments/{attachmentId}`                    | `Tickets.View`  | G     |
| `POST /api/v1/tickets/{id}/chat/sessions` (start â€” **sÃ³ o atendente**)         | `Tickets.Edit`  | G     |
| `POST /api/v1/tickets/{id}/chat/sessions/{sessionId}/invite/retry`             | `Tickets.Edit`  | G     |
| `POST /api/v1/tickets/{id}/chat/sessions/{sessionId}/messages`                 | `Tickets.Edit`  | G     |
| `POST /api/v1/tickets/{id}/chat/sessions/{sessionId}/end`                      | `Tickets.Edit`  | G     |
| `POST /api/v1/tickets/{id}/chat/sessions/{sessionId}/attachments/*`            | `Tickets.Edit`  | G     |

Lado do agent (`/api/v1/agent-auth/me/support-chat/*`): autenticaÃ§Ã£o por token
`mdz_` + `X-Agent-ID`; **nenhum** endpoint cria sessÃ£o e toda rota valida que a
sessÃ£o pertence ao agent autenticado (404 caso contrÃ¡rio).

### Dashboard

| Endpoint                                                          | PermissÃ£o        | Scope |
| ----------------------------------------------------------------- | ---------------- | ----- |
| `GET /api/v1/dashboard/global/summary`                            | `Dashboard.View` | G     |
| `GET /api/v1/clients/{clientId}/dashboard/summary`                | `Dashboard.View` | R     |
| `GET /api/v1/clients/{clientId}/sites/{siteId}/dashboard/summary` | `Dashboard.View` | R     |

### Configurations

| Endpoint                                               | PermissÃ£o           | Scope |
| ------------------------------------------------------ | ------------------- | ----- |
| `GET /api/v1/configurations/server`                    | `ServerConfig.View` | G     |
| `PUT /api/v1/configurations/server`                    | `ServerConfig.Edit` | G     |
| `PATCH /api/v1/configurations/server`                  | `ServerConfig.Edit` | G     |
| `POST /api/v1/configurations/server/reset`             | `ServerConfig.Edit` | G     |
| `GET /api/v1/configurations/server/metadata`           | `ServerConfig.View` | G     |
| `POST /api/v1/configurations/server/locks/impact`      | `ServerConfig.View` | G     |
| `GET /api/v1/configurations/server/export`             | `ServerConfig.View` | G     |
| `POST /api/v1/configurations/server/import`            | `ServerConfig.Edit` | G     |
| `POST /api/v1/configurations/server/ai/test`           | `ServerConfig.Execute` | G  |
| `POST /api/v1/configurations/server/nats/test`         | `ServerConfig.Execute` | G  |
| `PATCH /api/v1/configurations/server/nats`             | `ServerConfig.Edit` | G     |
| `POST /api/v1/configurations/server/object-storage/test` | `ServerConfig.Execute` | G |
| `GET /api/v1/configurations/server/ticket-attachments` | `ServerConfig.View` | G     |
| `PUT /api/v1/configurations/server/ticket-attachments` | `ServerConfig.Edit` | G     |
| `GET /api/v1/configurations/clients/{id}/effective`     | `ClientConfig.View` | G     |
| `GET /api/v1/configurations/clients/{id}/metadata`      | `ClientConfig.View` | G     |
| `GET /api/v1/configurations/sites/{id}/effective`       | `SiteConfig.View`   | G     |
| `GET /api/v1/configurations/sites/{id}/metadata`        | `SiteConfig.View`   | G     |

### Deploy Tokens

| Endpoint                     | PermissÃ£o           | Scope |
| ---------------------------- | ------------------- | ----- |
| `GET /api/v1/deploy-tokens`  | `Deployment.View`   | G     |
| `POST /api/v1/deploy-tokens` | `Deployment.Create` | G     |

### Reports

| Endpoint                                | PermissÃ£o        | Scope |
| --------------------------------------- | ---------------- | ----- |
| `GET /api/v1/reports/datasets`          | `Reports.View`   | G     |
| `GET /api/v1/reports/layout-schema`     | `Reports.View`   | G     |
| `GET /api/v1/reports/autocomplete`      | `Reports.View`   | G     |
| `POST /api/v1/reports/templates`        | `Reports.Create` | G     |
| `GET /api/v1/reports/templates/library` | `Reports.View`   | G     |

### Departments

| Endpoint                          | PermissÃ£o        | Scope |
| --------------------------------- | ---------------- | ----- |
| `GET /api/v1/departments/global`  | `Clients.View`   | G     |
| `GET /api/v1/departments`         | `Clients.View`   | G     |
| `GET /api/v1/departments/{id}`    | `Clients.View`   | G     |
| `POST /api/v1/departments`        | `Clients.Create` | G     |
| `PUT /api/v1/departments/{id}`    | `Clients.Edit`   | G     |
| `DELETE /api/v1/departments/{id}` | `Clients.Delete` | G     |

### Custom Fields

| Endpoint                                          | PermissÃ£o             | Scope |
| ------------------------------------------------- | --------------------- | ----- |
| `GET /api/v1/custom-fields`                       | `ServerConfig.View`   | G     |
| `GET /api/v1/custom-fields/{id}`                  | `ServerConfig.View`   | G     |
| `POST /api/v1/custom-fields`                      | `ServerConfig.Create` | G     |
| `PUT /api/v1/custom-fields/{id}`                  | `ServerConfig.Edit`   | G     |
| `DELETE /api/v1/custom-fields/{id}`               | `ServerConfig.Delete` | G     |
| `GET /api/v1/custom-fields/values/{scopeType}`    | `ServerConfig.View`   | G     |
| `PUT /api/v1/custom-fields/values/{definitionId}` | `ServerConfig.Edit`   | G     |

### Auto Ticket Rules

| Endpoint                             | PermissÃ£o        | Scope |
| ------------------------------------ | ---------------- | ----- |
| `GET /api/v1/auto-ticket-rules`      | `Tickets.View`   | G     |
| `GET /api/v1/auto-ticket-rules/{id}` | `Tickets.View`   | G     |
| `POST /api/v1/auto-ticket-rules`     | `Tickets.Create` | G     |
| `PUT /api/v1/auto-ticket-rules/{id}` | `Tickets.Edit`   | G     |

### Agent Labels

| Endpoint                                         | PermissÃ£o        | Scope |
| ------------------------------------------------ | ---------------- | ----- |
| `GET /api/v1/agent-labels/agents/{agentId}`      | `Agents.View`    | G     |
| `GET /api/v1/agent-labels/agents/{agentId}/suppressions` | `Agents.View` | G  |
| `GET /api/v1/agent-labels/agents/{agentId}/history` | `Agents.View`  | G     |
| `DELETE /api/v1/agent-labels/suppressions/{id}`  | `Agents.Edit`    | G     |
| `POST /api/v1/agent-labels/batch`                | `Agents.View`    | G     |
| `GET /api/v1/agent-labels/usage`                 | `Agents.View`    | G     |
| `GET /api/v1/agent-labels/agents-by-label`       | `Agents.View`    | G     |
| `GET /api/v1/agent-labels/rules/{ruleId}/agents` | `Agents.View`    | G     |
| `GET /api/v1/agent-labels/rules`                 | `Agents.View`    | G     |
| `GET /api/v1/agent-labels/rules/{id}/versions`   | `Agents.View`    | G     |
| `GET /api/v1/agent-labels/rules/export`          | `Agents.View`    | G     |
| `POST /api/v1/agent-labels/rules`                | `Agents.Create`  | G     |
| `PUT /api/v1/agent-labels/rules/{id}`            | `Agents.Edit`    | G     |
| `DELETE /api/v1/agent-labels/rules/{id}`         | `Agents.Edit`    | G     |
| `POST /api/v1/agent-labels/rules/import`         | `Agents.Edit`    | G     |
| `POST /api/v1/agent-labels/rules/impact`         | `Agents.View`    | G     |
| `POST /api/v1/agent-labels/rules/dry-run`        | `Agents.View`    | G     |
| `POST /api/v1/agent-labels/rules/dry-run/batch`  | `Agents.View`    | G     |
| `GET /api/v1/agent-labels/protected-labels`      | `Agents.View`    | G     |
| `POST /api/v1/agent-labels/protected-labels`     | `Agents.Edit`    | G     |
| `DELETE /api/v1/agent-labels/protected-labels/{id}` | `Agents.Edit` | G     |
| `POST /api/v1/agent-labels/manual`               | `Agents.Edit`    | G     |
| `DELETE /api/v1/agent-labels/manual/{id}`        | `Agents.Edit`    | G     |
| `POST /api/v1/agent-labels/reprocess`            | `Agents.Execute` | G     |
| `GET /api/v1/agent-labels/reprocess/{jobId}`     | `Agents.View`    | G     |

### Monitoring Events

| Endpoint                                       | PermissÃ£o      | Scope |
| ---------------------------------------------- | -------------- | ----- |
| `POST /api/v1/monitoring-events`               | `Logs.Execute` | G     |
| `POST /api/v1/monitoring-events/{id}/evaluate` | `Logs.Execute` | G     |

### Configuration Audit

| Endpoint                                                                | PermissÃ£o   | Scope |
| ----------------------------------------------------------------------- | ----------- | ----- |
| `GET /api/v1/configuration-audit`                                       | `Logs.View` | G     |
| `GET /api/v1/configuration-audit/{entityType}/{entityId}`               | `Logs.View` | G     |
| `GET /api/v1/configuration-audit/{entityType}/{entityId}/field/{field}` | `Logs.View` | G     |
| `GET /api/v1/configuration-audit/by-user/{username}`                    | `Logs.View` | G     |
| `GET /api/v1/configuration-audit/report`                                | `Logs.View` | G     |

### App Store

| Endpoint                                      | PermissÃ£o         | Scope      |
| --------------------------------------------- | ----------------- | ---------- |
| `GET /api/v1/app-store/catalog`               | `AppStore.View`   | G          |
| `GET /api/v1/app-store/catalog/{packageId}`   | `AppStore.View`   | G          |
| `POST /api/v1/app-store/catalog/custom`       | `AppStore.Edit`   | G          |
| `POST /api/v1/app-store/sync`                 | `AppStore.Edit`   | G          |
| `DELETE /api/v1/app-store/approvals/{ruleId}` | `AppStore.Delete` | G          |
| `GET /api/v1/app-store/approvals`             | `AppStore.View`   | I (scoped) |
| `POST /api/v1/app-store/approvals`            | `AppStore.Edit`   | I (scoped) |
| `GET /api/v1/app-store/diff/effective`        | `AppStore.View`   | I (scoped) |
| `GET /api/v1/app-store/diff/{packageId}`      | `AppStore.View`   | I (scoped) |
| `GET /api/v1/app-store/effective`             | `AppStore.View`   | I (scoped) |
| `GET /api/v1/app-store/approvals/audit`       | `AppStore.View`   | I (scoped) |

### Automation Scripts

| Endpoint                                      | PermissÃ£o            | Scope        |
| --------------------------------------------- | -------------------- | ------------ |
| `GET /api/v1/automation/scripts`              | `Automation.View`    | I (clientId) |
| `GET /api/v1/automation/scripts/{id}`         | `Automation.View`    | I (clientId) |
| `POST /api/v1/automation/scripts`             | `Automation.Create`  | I (clientId) |
| `PUT /api/v1/automation/scripts/{id}`         | `Automation.Edit`    | I (clientId) |
| `DELETE /api/v1/automation/scripts/{id}`      | `Automation.Delete`  | I (clientId) |
| `GET /api/v1/automation/scripts/{id}/consume` | `Automation.Execute` | I (clientId) |
| `GET /api/v1/automation/scripts/{id}/audit`   | `Automation.View`    | I (clientId) |

### Automation Tasks

| Endpoint                               | PermissÃ£o           | Scope         |
| -------------------------------------- | ------------------- | ------------- |
| `GET /api/v1/automation/tasks`         | `Automation.View`   | I (scopeType) |
| `GET /api/v1/automation/tasks/{id}`    | `Automation.View`   | I (scopeType) |
| `POST /api/v1/automation/tasks`        | `Automation.Create` | I (scopeType) |
| `PUT /api/v1/automation/tasks/{id}`    | `Automation.Edit`   | I (scopeType) |
| `DELETE /api/v1/automation/tasks/{id}` | `Automation.Delete` | I (scopeType) |

### Agent Updates

| Endpoint           | PermissÃ£o                            | Scope |
| ------------------ | ------------------------------------ | ----- |
| Todos os endpoints | `Deployment.View/Create/Edit/Delete` | G     |

### MeshCentral

| Endpoint                                                               | PermissÃ£o | Scope |
| ---------------------------------------------------------------------- | --------- | ----- |
| `Users.View/Create/Edit/Delete`, `Agents.Edit`, `SiteConfig.View/Edit` | â€”         | G     |

### Admin Background Services

| Endpoint           | PermissÃ£o                                  | Scope |
| ------------------ | ------------------------------------------ | ----- |
| Todos os endpoints | `RequireUserAuth` (sem permissÃ£o granular) | G     |

---

## Legenda de Scope

| SÃ­mbolo | Significado                                                                   |
| ------- | ----------------------------------------------------------------------------- |
| **G**   | `[RequirePermission(X, Y)]` â€” escopo Global                                   |
| **R**   | `[RequirePermission(X, Y, ScopeSource.FromRoute)]` â€” escopo via rota          |
| **I**   | VerificaÃ§Ã£o inline com `IPermissionService` (escopo dinÃ¢mico por query param) |

---

## Notas

1. **PermissÃµes vazias**: `KnowledgeBase`, `RemoteDebug` nÃ£o tÃªm controllers implementados ainda.
2. **Sites**: NÃ£o hÃ¡ `SitesController` dedicado â€” sites sÃ£o gerenciados via MeshCentral embed.
3. **AppStore scoped**: Endpoints de approvals/diffs usam escopo dinÃ¢mico resolvido por `AppApprovalScopeType` (Global/Client/Site/Agent).
4. **Automation scoped**: Scripts e tasks resolvem escopo por `clientId` ou `scopeType/scopeId`.

### Patches (Windows Update)

| Endpoint | Permissão | Escopo |
| --- | --- | --- |
| `GET /api/v1/patches/overview` | `Patches.View` | Access list |
| `GET /api/v1/patches/snapshot` | `Patches.View` | Access list |
| `GET /api/v1/patches/inventory` | `Patches.View` | Access list |
| `GET /api/v1/patches/inventory/{groupKey}/agents` | `Patches.View` | Access list |
| `GET /api/v1/patches/pending` | `Patches.View` | Access list |
| `GET /api/v1/patches/approvals` | `Patches.View` | Global |
| `GET /api/v1/patches/approvals/audit` | `Patches.View` | Global |
| `POST /api/v1/patches/approvals` | `Patches.Edit` | Global |
| `DELETE /api/v1/patches/approvals/{ruleId}` | `Patches.Delete` | Global |
| `GET /api/v1/patches/policy` | `Patches.View` | Global |
| `PUT /api/v1/patches/policy` | `Patches.Edit` | Global |
| `POST /api/v1/patches/scan` | `Patches.Execute` | Access list |
| `POST /api/v1/patches/install` | `Patches.Execute` | Access list |
| `GET /api/v1/agents/{id}/windows-updates/page` | `Patches.View` | Agente (access list) |
| `GET /api/v1/agents/{id}/windows-updates/snapshot` | `Patches.View` | Agente (access list) |
| `POST /api/v1/agents/{id}/windows-updates/scan` | `Patches.Execute` | Agente (access list) |
| `POST /api/v1/agents/{id}/windows-updates/{updateKey}/install` | `Patches.Execute` | Agente (access list) |
| `POST /api/v1/agents/{id}/windows-updates/install-approved` | `Patches.Execute` | Agente (access list) |
| `POST /api/v1/agents/{id}/windows-updates/{updateKey}/approve` | `Patches.Edit` | Agente (access list) |
| `POST /api/v1/agents/{id}/windows-updates/{updateKey}/deny` | `Patches.Edit` | Agente (access list) |
| `POST /api/v1/agents/{id}/windows-updates/bulk` | `Patches.Execute` | Agente (access list) |
| `POST /api/v1/agents/{id}/windows-updates/os-policy/apply` | `Patches.Execute` | Agente (access list) |
| `POST /api/v1/agents/{id}/windows-updates/os-policy/rollback` | `Patches.Execute` | Agente (access list) |

**Notas de patches**

- **Seed**: `M231_SeedPatchPermissions` cria `Patches:View|Create|Edit|Delete|Execute`. Admin/Manager recebem tudo; Operator recebe View+Execute; Support/Viewer somente View.
- **Resolução de aprovação**: Agent → Site → Client → Global; a mais específica vence e, em empate, `Deny` vence `Approve`. Sem regra explícita, a política pode auto-aprovar por severidade/classificação.
- **Escopo de dados**: todos os endpoints de frota filtram pela access list do usuário (`IScopeContext.GetAccessAsync`); endpoints por agente validam que o cliente do agente está na access list.
- **Forçar instalação** (`force=true`) exige `Patches.Execute` e grava auditoria (`patch_approval_audits`) com o usuário/IP da requisição.
- **Enforcement no SO** só é aceito quando a política do escopo está em `OsEnforced`; é best-effort e tem rollback a partir do backup devolvido pelo agente.
