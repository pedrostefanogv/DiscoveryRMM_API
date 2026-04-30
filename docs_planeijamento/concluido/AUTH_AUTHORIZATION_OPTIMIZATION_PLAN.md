# Plano de Otimização — Autenticação & Autorização

**Criado em:** 30/04/2026
**Status:** Concluído ✅
**Branch:** dev

---

## Diagnóstico Geral

O sistema DiscoveryRMM_API tem uma base sólida de autenticação (JWT RS256, MFA FIDO2/TOTP, API tokens), mas a autorização (permissões) está implementada de forma parcial e inconsistente. Apenas 4 dos ~30 controllers têm cobertura adequada de `[RequirePermission]`.

---

## Fase 1 — Fechar brechas críticas

### 1.1 Adicionar `[RequirePermission]` em todos os controladores desprotegidos ✅

| Controller | ResourceType | Actions | Status |
|---|---|---|---|
| ClientsController | `ResourceType.Clients` | View/Create/Edit/Delete | ✅ |
| TicketsController | `ResourceType.Tickets` | View/Create/Edit/Delete | ✅ |
| ConfigurationsController | `ResourceType.ServerConfig` | View/Edit | ✅ |
| DeployTokensController | `ResourceType.Deployment` | View/Create/Edit/Delete | ✅ |
| ReportsController | `ResourceType.Reports` | View/Create/Edit/Delete | ✅ |
| DashboardController | `ResourceType.Dashboard` | View | ✅ |
| DepartmentsController | `ResourceType.Clients` | View/Create/Edit/Delete | ✅ |
| CustomFieldsController | `ResourceType.ServerConfig` | View/Create/Edit/Delete | ✅ |
| AutoTicketRulesController | `ResourceType.Tickets` | View/Create/Edit/Delete | ✅ |
| MonitoringEventsController | `ResourceType.Logs` | View/Execute | ✅ |
| AgentLabelsController | `ResourceType.Agents` | View/Create/Edit/Delete | ✅ |
| ConfigurationAuditController | `ResourceType.Logs` | View | ✅ |
| ApiTokensController | `ResourceType.Users` | View/Create/Revoke | ✅ |

### 1.2 Corrigir `HasAdminScope` bug no UsersController ✅

Substituído o código quebrado `HttpContext.Items["HasAdminScope"]` por `[RequirePermission(ResourceType.Users, ActionType.Edit)]` no endpoint admin `ChangePassword`.

### 1.3 Remover `[AllowAnonymous]` da classe MfaController ✅

Movido para `[AllowAnonymous]` individual nos 4 métodos de registro MFA apenas.

---

## Fase 2 — Ativar autorização com escopo ✅

### 2.1 Criar atributo `ScopeSource` e `RequirePermission` com escopo via rota ✅

- Novo enum `ScopeSource` em `Discovery.Core.Enums.Identity`: `Global` | `FromRoute`
- `RequirePermissionAttribute` estendido com construtor `(ResourceType, ActionType, ScopeSource)`
- `RequirePermissionFilter` implementa resolução de escopo:
  - **`FromRoute`**: extrai `{siteId}` (Site), `{clientId}` (Client), ou `{id}` com heurística de controller (ClientsController → Client)
  - Popula `HttpContext.Items["ClientId"]` e `HttpContext.Items["SiteId"]`

### 2.2 Escopo aplicado nos controllers multitenant ✅

| Controller | Endpoint | Escopo |
|---|---|---|
| DashboardController | `GET clients/{clientId}/dashboard/summary` | `Client` via `{clientId}` |
| DashboardController | `GET clients/{clientId}/sites/{siteId}/dashboard/summary` | `Site` via `{siteId}` + `{clientId}` |
| TicketsController | `GET by-client/{clientId}` | `Client` via `{clientId}` |
| ClientsController | `GET/PUT/DELETE {id}` | `Client` via heurística (controller name) |

### 2.3 População de HttpContext.Items ✅

O filtro agora popula automaticamente `ClientId`/`SiteId` no `HttpContext.Items` após verificação de escopo, disponível para queries filtradas.

---

## Fase 3 — Simplificação e consistência ✅

### 3.1 Verificações inline migradas para `[RequirePermission]` ✅

- `AppStoreController`: 6 endpoints com verificações inline migrados para `[RequirePermission]` (catalog, catalog/{id}, custom, delete approval, sync)
- Endpoints com escopo dinâmico (`EnsureAppStoreScopePermissionAsync`) mantidos inline (aprovadores/diffs por escopo)

### 3.2 `[Authorize]` → `[RequireUserAuth]` ✅

- `BackgroundServicesController`: removido `[Authorize]` do ASP.NET, substituído por `[RequireUserAuth]` + `using Discovery.Api.Filters`

### 3.3 Documentação da matriz de permissões ✅

- Criado `docs/PERMISSIONS_MATRIX.md` com tabela completa de:
  - Todos os ResourceTypes × ActionTypes disponíveis
  - Todos os endpoints com permissão e escopo (Global / FromRoute / Inline)
  - Legenda de escopos

---

## Fase 4 — Melhorias de segurança adicionais ✅

### 4.1 Rate limiting no login ✅ (já existente)

- Partitioned rate limiting com 3 tiers: `auth` (20 req/min), `agent` (600 req/min), `general` (240 req/min)
- Configurável via `appsettings.json`: `Security:RateLimiting:*`
- Rejeição com HTTP 429 + JSON body

### 4.2 Lockout de conta após falhas consecutivas ✅ (já existente)

- Lockout progressivo: 5 falhas → 60s, 10 falhas → 300s (5min), 20+ falhas → 1800s (30min)
- Reset automático ao fazer login com sucesso
- Endpoint `POST /api/v1/users/{id}/unlock` para admin desbloquear manualmente
- Campos na entidade `User`: `FailedLoginAttempts` (int), `LockoutUntil` (DateTime?)

### 4.3 Log de auditoria de autenticação ✅ (implementado)

- Nova entidade `AuthAuditLog` com: UserId, EventType, Success, FailureReason, IpAddress, UserAgent, Detail, OccurredAt
- Nova migration `M116_CreateAuthAuditLogs` (FluentMigrator)
- Integrado no `UserAuthService`: registra login_success, login_failed, session_created
- Repositório `AuthAuditLogRepository` com queries: GetByUser, GetRecent, GetFailed
- Tabela `auth_audit_logs` com índices em (user_id, occurred_at) e (event_type, occurred_at)

### 4.4 API Token com permissões (futuro)

- Atualmente API tokens concedem acesso completo do usuário
- Evolução futura: adicionar campo `PermissionsJson` no `ApiToken` para subset de permissões

---

## Progresso

| Fase | Status | Data |
|---|---|---|
| Fase 1 | ✅ Concluída | 30/04/2026 |
| Fase 2 | ✅ Concluída | 30/04/2026 |
| Fase 3 | ✅ Concluída | 30/04/2026 |
| Fase 4 | ✅ Concluída | 30/04/2026 |
