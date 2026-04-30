# Matriz de Permissões — DiscoveryRMM API

**Gerado em:** 30/04/2026
**Branch:** dev

> Esta matriz documenta todos os endpoints da API com seus requisitos de permissão.
> `Scope` = escopo de autorização aplicado: **G**lobal, de **R**ota, ou **I**nline (manual).

---

## ResourceType × ActionType

| ResourceType | View | Create | Edit | Delete | Execute |
|---|---|---|---|---|---|
| **Agents** | Labels, info | Regras de label | Labels, regras | - | - |
| **Tickets** | Listar, detalhes | Criar | Editar, workflow, comentários | - | - |
| **Clients** | Listar, detalhes | Criar | Editar, custom fields | Excluir | - |
| **Sites** | ✖ (não tem controller próprio) | - | - | - | - |
| **Reports** | Datasets, templates | Criar template | - | - | - |
| **ServerConfig** | Todas GETs config | Custom fields | Todas PUTs/PATCHs config | Custom fields | - |
| **ClientConfig** | ✖ (não usado) | - | - | - | - |
| **SiteConfig** | MeshCentral site | - | MeshCentral site | - | - |
| **Users** | Listar, MFA keys | Criar, API tokens | Editar, reset MFA, senha admin | Desativar, revogar token | - |
| **Automation** | Scripts, tasks | Scripts, tasks | Scripts, tasks | Scripts, tasks | Consumir script |
| **Deployment** | Tokens, updates | Tokens, updates | Tokens, updates | Revogar token | - |
| **KnowledgeBase** | ✖ (não tem controller) | - | - | - | - |
| **AiChat** | ✖ (não tem controller) | - | - | - | - |
| **AppStore** | Catálogo, diffs | - | Custom, approvals, sync | Remover approval | - |
| **Logs** | Audit, monitoring | - | - | - | Ingest events |
| **Dashboard** | Summaries | - | - | - | - |
| **RemoteDebug** | ✖ (não tem controller) | - | - | - | - |

---

## Endpoints × Permissões

### Auth (público)
| Endpoint | Auth | Permissão |
|---|---|---|
| `POST /api/v1/auth/login` | `[AllowAnonymous]` | — |
| `POST /api/v1/auth/refresh` | `[AllowAnonymous]` | — |
| `POST /api/v1/auth/logout` | `[RequireUserAuth]` | — (qualquer sessão) |
| `POST /api/v1/auth/mfa/fido2/begin` | `[RequireMfaPending]` | — |
| `POST /api/v1/auth/mfa/fido2/complete` | `[RequireMfaPending]` | — |
| `POST /api/v1/auth/mfa/otp/complete` | `[RequireMfaPending]` | — |
| `POST /api/v1/auth/first-access/complete` | `[RequireMfaSetupOrFullSession]` | — |

### MFA (registro público, gerenciamento autenticado)
| Endpoint | Auth | Permissão |
|---|---|---|
| `GET /api/v1/mfa/keys` | `[RequireUserAuth]` | — (próprio usuário) |
| `POST /api/v1/mfa/fido2/register/begin` | `[AllowAnonymous]` + `[RequireMfaSetupOrFullSession]` | — |
| `POST /api/v1/mfa/fido2/register/complete` | `[AllowAnonymous]` + `[RequireMfaSetupOrFullSession]` | — |
| `POST /api/v1/mfa/totp/register/begin` | `[AllowAnonymous]` + `[RequireMfaSetupOrFullSession]` | — |
| `POST /api/v1/mfa/totp/register/complete` | `[AllowAnonymous]` + `[RequireMfaSetupOrFullSession]` | — |
| `DELETE /api/v1/mfa/keys/{keyId}` | `[RequireUserAuth]` | — (próprias chaves) |
| `PATCH /api/v1/mfa/keys/{keyId}/name` | `[RequireUserAuth]` | — (próprias chaves) |

### Users
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/users` | `Users.View` | G |
| `GET /api/v1/users/{id}` | `Users.View` | G |
| `GET /api/v1/users/me` | — (próprio perfil) | G |
| `PUT /api/v1/users/me` | — (próprio perfil) | G |
| `GET /api/v1/users/me/security` | — (próprio perfil) | G |
| `POST /api/v1/users/me/change-password` | — (própria senha) | G |
| `POST /api/v1/users` | `Users.Create` | G |
| `PUT /api/v1/users/{id}` | `Users.Edit` | G |
| `POST /api/v1/users/{id}/change-password` | `Users.Edit` | G |
| `GET /api/v1/users/{id}/mfa/keys` | `Users.View` | G |
| `DELETE /api/v1/users/{id}/mfa` | `Users.Edit` | G |
| `DELETE /api/v1/users/{id}/mfa/keys/{keyId}` | `Users.Edit` | G |
| `POST /api/v1/users/{id}/force-password-reset` | `Users.Edit` | G |
| `DELETE /api/v1/users/{id}` | `Users.Delete` | G |

### Roles
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/roles` | `Users.View` | G |
| `GET /api/v1/roles/{id}` | `Users.View` | G |
| `POST /api/v1/roles` | `Users.Create` | G |
| `PUT /api/v1/roles/{id}` | `Users.Edit` | G |
| `DELETE /api/v1/roles/{id}` | `Users.Delete` | G |
| `GET /api/v1/roles/{id}/permissions` | `Users.View` | G |
| `GET /api/v1/roles/permissions` | `Users.View` | G |
| `POST /api/v1/roles/{id}/permissions` | `Users.Edit` | G |
| `DELETE /api/v1/roles/{id}/permissions/{permId}` | `Users.Edit` | G |

### User Groups
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/user-groups` | `Users.View` | G |
| `GET /api/v1/user-groups/{id}` | `Users.View` | G |
| `POST /api/v1/user-groups` | `Users.Create` | G |
| `PUT /api/v1/user-groups/{id}` | `Users.Edit` | G |
| `DELETE /api/v1/user-groups/{id}` | `Users.Delete` | G |
| `GET /api/v1/user-groups/{id}/members` | `Users.View` | G |
| `POST /api/v1/user-groups/{id}/members` | `Users.Edit` | G |
| `DELETE /api/v1/user-groups/{id}/members/{userId}` | `Users.Edit` | G |
| `GET /api/v1/user-groups/{id}/roles` | `Users.View` | G |
| `POST /api/v1/user-groups/{id}/roles` | `Users.Edit` | G |
| `DELETE /api/v1/user-groups/{id}/roles/{assignmentId}` | `Users.Edit` | G |

### API Tokens
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/api-tokens` | — (próprios tokens) | G |
| `POST /api/v1/api-tokens` | `Users.Create` | G |
| `DELETE /api/v1/api-tokens/{tokenId}` | `Users.Delete` | G |

### Clients
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/clients` | `Clients.View` | G |
| `GET /api/v1/clients/{id}` | `Clients.View` | G |
| `POST /api/v1/clients` | `Clients.Create` | G |
| `PUT /api/v1/clients/{id}` | `Clients.Edit` | G |
| `DELETE /api/v1/clients/{id}` | `Clients.Delete` | G |
| `GET /api/v1/clients/{id}/custom-fields` | `Clients.View` | G |
| `PUT /api/v1/clients/{id}/custom-fields/{defId}` | `Clients.Edit` | G |

### Tickets
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/tickets` | `Tickets.View` | G |
| `GET /api/v1/tickets/by-client/{clientId}` | `Tickets.View` | R |
| `GET /api/v1/tickets/{id}` | `Tickets.View` | G |
| `POST /api/v1/tickets` | `Tickets.Create` | G |
| `PUT /api/v1/tickets/{id}` | `Tickets.Edit` | G |
| `PATCH /api/v1/tickets/{id}/workflow-state` | `Tickets.Edit` | G |
| `GET /api/v1/tickets/{id}/comments` | `Tickets.View` | G |
| `POST /api/v1/tickets/{id}/comments` | `Tickets.Edit` | G |
| `GET /api/v1/tickets/{id}/attachments` | `Tickets.View` | G |
| `POST /api/v1/tickets/{id}/attachments/presigned-upload` | `Tickets.Edit` | G |
| `POST /api/v1/tickets/{id}/attachments/complete-upload` | `Tickets.Edit` | G |

### Dashboard
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/dashboard/global/summary` | `Dashboard.View` | G |
| `GET /api/v1/clients/{clientId}/dashboard/summary` | `Dashboard.View` | R |
| `GET /api/v1/clients/{clientId}/sites/{siteId}/dashboard/summary` | `Dashboard.View` | R |

### Configurations
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/configurations/server` | `ServerConfig.View` | G |
| `PUT /api/v1/configurations/server` | `ServerConfig.Edit` | G |
| `PATCH /api/v1/configurations/server` | `ServerConfig.Edit` | G |
| `POST /api/v1/configurations/server/reset` | `ServerConfig.Edit` | G |
| `POST /api/v1/configurations/server/nats/test` | `ServerConfig.View` | G |
| `PATCH /api/v1/configurations/server/nats` | `ServerConfig.Edit` | G |
| `GET /api/v1/configurations/server/metadata` | `ServerConfig.View` | G |
| `GET /api/v1/configurations/server/reporting` | `ServerConfig.View` | G |
| `PUT /api/v1/configurations/server/reporting` | `ServerConfig.Edit` | G |
| `GET /api/v1/configurations/server/ticket-attachments` | `ServerConfig.View` | G |
| `PUT /api/v1/configurations/server/ticket-attachments` | `ServerConfig.Edit` | G |
| `GET /api/v1/configurations/server/retention` | `ServerConfig.View` | G |
| `PUT /api/v1/configurations/server/retention` | `ServerConfig.Edit` | G |
| `POST /api/v1/configurations/server/retention/reset` | `ServerConfig.Edit` | G |

### Deploy Tokens
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/deploy-tokens` | `Deployment.View` | G |
| `POST /api/v1/deploy-tokens` | `Deployment.Create` | G |

### Reports
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/reports/datasets` | `Reports.View` | G |
| `GET /api/v1/reports/layout-schema` | `Reports.View` | G |
| `GET /api/v1/reports/autocomplete` | `Reports.View` | G |
| `POST /api/v1/reports/templates` | `Reports.Create` | G |
| `GET /api/v1/reports/templates/library` | `Reports.View` | G |

### Departments
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/departments/global` | `Clients.View` | G |
| `GET /api/v1/departments` | `Clients.View` | G |
| `GET /api/v1/departments/{id}` | `Clients.View` | G |
| `POST /api/v1/departments` | `Clients.Create` | G |
| `PUT /api/v1/departments/{id}` | `Clients.Edit` | G |
| `DELETE /api/v1/departments/{id}` | `Clients.Delete` | G |

### Custom Fields
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/custom-fields/definitions` | `ServerConfig.View` | G |
| `GET /api/v1/custom-fields/definitions/{id}` | `ServerConfig.View` | G |
| `POST /api/v1/custom-fields/definitions` | `ServerConfig.Create` | G |
| `PUT /api/v1/custom-fields/definitions/{id}` | `ServerConfig.Edit` | G |
| `DELETE /api/v1/custom-fields/definitions/{id}` | `ServerConfig.Delete` | G |
| `GET /api/v1/custom-fields/values/{scopeType}` | `ServerConfig.View` | G |
| `GET /api/v1/custom-fields/schema/{scopeType}` | `ServerConfig.View` | G |

### Auto Ticket Rules
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/auto-ticket-rules` | `Tickets.View` | G |
| `GET /api/v1/auto-ticket-rules/{id}` | `Tickets.View` | G |
| `POST /api/v1/auto-ticket-rules` | `Tickets.Create` | G |
| `PUT /api/v1/auto-ticket-rules/{id}` | `Tickets.Edit` | G |

### Agent Labels
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/agent-labels/agents/{agentId}` | `Agents.View` | G |
| `GET /api/v1/agent-labels/rules/{ruleId}/agents` | `Agents.View` | G |
| `GET /api/v1/agent-labels/rules` | `Agents.View` | G |
| `POST /api/v1/agent-labels/rules` | `Agents.Create` | G |

### Monitoring Events
| Endpoint | Permissão | Scope |
|---|---|---|
| `POST /api/v1/monitoring-events` | `Logs.Execute` | G |
| `POST /api/v1/monitoring-events/{id}/evaluate` | `Logs.Execute` | G |

### Configuration Audit
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/configuration-audit` | `Logs.View` | G |
| `GET /api/v1/configuration-audit/{entityType}/{entityId}` | `Logs.View` | G |
| `GET /api/v1/configuration-audit/{entityType}/{entityId}/field/{field}` | `Logs.View` | G |
| `GET /api/v1/configuration-audit/by-user/{username}` | `Logs.View` | G |
| `GET /api/v1/configuration-audit/report` | `Logs.View` | G |

### App Store
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/app-store/catalog` | `AppStore.View` | G |
| `GET /api/v1/app-store/catalog/{packageId}` | `AppStore.View` | G |
| `POST /api/v1/app-store/catalog/custom` | `AppStore.Edit` | G |
| `POST /api/v1/app-store/sync` | `AppStore.Edit` | G |
| `DELETE /api/v1/app-store/approvals/{ruleId}` | `AppStore.Delete` | G |
| `GET /api/v1/app-store/approvals` | `AppStore.View` | I (scoped) |
| `POST /api/v1/app-store/approvals` | `AppStore.Edit` | I (scoped) |
| `GET /api/v1/app-store/diff/effective` | `AppStore.View` | I (scoped) |
| `GET /api/v1/app-store/diff/{packageId}` | `AppStore.View` | I (scoped) |
| `GET /api/v1/app-store/effective` | `AppStore.View` | I (scoped) |
| `GET /api/v1/app-store/approvals/audit` | `AppStore.View` | I (scoped) |

### Automation Scripts
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/automation/scripts` | `Automation.View` | I (clientId) |
| `GET /api/v1/automation/scripts/{id}` | `Automation.View` | I (clientId) |
| `POST /api/v1/automation/scripts` | `Automation.Create` | I (clientId) |
| `PUT /api/v1/automation/scripts/{id}` | `Automation.Edit` | I (clientId) |
| `DELETE /api/v1/automation/scripts/{id}` | `Automation.Delete` | I (clientId) |
| `GET /api/v1/automation/scripts/{id}/consume` | `Automation.Execute` | I (clientId) |
| `GET /api/v1/automation/scripts/{id}/audit` | `Automation.View` | I (clientId) |

### Automation Tasks
| Endpoint | Permissão | Scope |
|---|---|---|
| `GET /api/v1/automation/tasks` | `Automation.View` | I (scopeType) |
| `GET /api/v1/automation/tasks/{id}` | `Automation.View` | I (scopeType) |
| `POST /api/v1/automation/tasks` | `Automation.Create` | I (scopeType) |
| `PUT /api/v1/automation/tasks/{id}` | `Automation.Edit` | I (scopeType) |
| `DELETE /api/v1/automation/tasks/{id}` | `Automation.Delete` | I (scopeType) |

### Agent Updates
| Endpoint | Permissão | Scope |
|---|---|---|
| Todos os endpoints | `Deployment.View/Create/Edit/Delete` | G |

### MeshCentral
| Endpoint | Permissão | Scope |
|---|---|---|
| `Users.View/Create/Edit/Delete`, `Agents.Edit`, `SiteConfig.View/Edit` | — | G |

### Admin Background Services
| Endpoint | Permissão | Scope |
|---|---|---|
| Todos os endpoints | `RequireUserAuth` (sem permissão granular) | G |

---

## Legenda de Scope

| Símbolo | Significado |
|---|---|
| **G** | `[RequirePermission(X, Y)]` — escopo Global |
| **R** | `[RequirePermission(X, Y, ScopeSource.FromRoute)]` — escopo via rota |
| **I** | Verificação inline com `IPermissionService` (escopo dinâmico por query param) |

---

## Notas

1. **Permissões vazias**: `KnowledgeBase`, `AiChat`, `RemoteDebug` não têm controllers implementados ainda.
2. **Sites**: Não há `SitesController` dedicado — sites são gerenciados via MeshCentral embed.
3. **AppStore scoped**: Endpoints de approvals/diffs usam escopo dinâmico resolvido por `AppApprovalScopeType` (Global/Client/Site/Agent).
4. **Automation scoped**: Scripts e tasks resolvem escopo por `clientId` ou `scopeType/scopeId`.
