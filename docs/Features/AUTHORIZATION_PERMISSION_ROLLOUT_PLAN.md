# Plano de Rollout de Permissoes e Escopos

## Objetivo
Aplicar um modelo consistente de autorizacao com:
- Autenticacao global por padrao para endpoints de usuario.
- Permissoes baseadas em recurso + acao.
- Escopo organizacional (Global, Client, Site, Agent) para reduzir excesso de privilegios.

## Estado Atual
- Protecao global de usuario habilitada em `AddControllers` com `RequireUserAuthAttribute`.
- Excecoes explicitas com `AllowAnonymous` para fluxos com autenticacao propria:
  - `api/auth` (login/refresh/MFA/logout com filtros por metodo)
  - `api/agent-auth` (token de agent via middleware dedicado)
  - `api/agent-install` (deploy token validado manualmente)

## Modelo de Permissao
Permissao canonica: `(ResourceType, ActionType, ScopeLevel, ScopeId)`

- `ResourceType`: Users, Roles, UserGroups, Clients, Sites, Agents, Tickets, Reports, Automation, Knowledge, MeshCentral, DeployTokens, Configurations, Notifications, etc.
- `ActionType`: View, Create, Edit, Delete, Execute, Manage, Approve, Export.
- `ScopeLevel`: Global, Client, Site, Agent.
- `ScopeId`:
  - `null` quando Global
  - `ClientId` quando escopo Client
  - `SiteId` quando escopo Site
  - `AgentId` quando escopo Agent

## Matriz Inicial de Permissoes (Baseline)

### Identidade e Acesso
- Users: View/Create/Edit/Delete em Global
- Roles: View/Create/Edit/Delete em Global
- UserGroups: View/Create/Edit/Delete em Global
- ApiTokens: View/Create/Delete em escopo do proprio usuario (ou Global para admin)

### Organizacao
- Clients: View/Create/Edit/Delete em Global
- Sites: View/Create/Edit/Delete em Client
- Departments: View/Create/Edit/Delete em Client

### Operacao
- Agents: View/Edit/Delete em Site
- AgentCommands: Execute em Site ou Agent
- SoftwareInventory: View em Site

### Service Desk
- Tickets: View/Create/Edit/Delete em Site
- TicketAudit: View em Site
- TicketSla: View/Edit em Site
- Notes: View/Create/Edit/Delete no escopo da entidade alvo

### Automacao
- AutomationScripts: View/Create/Edit/Delete em Client
- AutomationTasks: View/Create/Edit/Delete/Execute em Site
- AppStore: View/Execute em Site

### Relatorios
- Reports: View/Create/Edit/Delete/Export em Client ou Site

### Configuracoes
- Configurations: View/Edit em Global/Client/Site
- ConfigurationAudit: View em Global/Client/Site

### Integracoes e Suporte Remoto
- MeshCentral: View/Manage em Site
- DeployTokens: View/Create/Delete em Site

## Aplicacao por Controller (Faseada)

### Fase 1 (Critico)
Adicionar `RequirePermission` nas rotas mutaveis (POST/PUT/PATCH/DELETE):
- `ClientsController`, `SitesController`, `AgentsController`
- `TicketsController`, `WorkflowController`, `WorkflowProfilesController`
- `DeployTokensController`, `ConfigurationsController`

### Fase 2 (Leitura sensivel)
Adicionar `RequirePermission(..., View)` em GETs que retornam dados sensiveis:
- `DashboardController`, `LogsController`, `KnowledgeController`, `ReportsController`

### Fase 3 (Escopo refinado)
Substituir verificacao global por verificacao scoped:
- Resolver `ClientId/SiteId/AgentId` da rota/entidade
- Chamar `IPermissionService.HasPermissionAsync` com escopo correto

### Fase 4 (Governanca)
- Criar endpoint de simulacao de permissao (dry-run)
- Auditar negacoes 403 com contexto (userId, recurso, acao, scope)
- Adicionar testes de autorizacao por matriz de perfil

## Regras de Excecao
Manter sem autenticacao de usuario apenas endpoints de bootstrap/integracao com token proprio:
- `/api/auth/login`
- `/api/auth/refresh`
- `/api/agent-auth/*`
- `/api/agent-install/*`

## Estrategia de Implementacao Tecnica
1. Enforcar autenticacao global (ja aplicado).
2. Decorar controllers com `RequirePermission` por metodo (comecar por escrita).
3. Evoluir `RequirePermissionFilter` para aceitar escopo dinamico (route/query/body).
4. Adicionar testes automatizados de autorizacao por papel e escopo.

## Testes Minimos por Endpoint
Para cada endpoint relevante:
1. Sem token: 401
2. Token mfa_pending/mfa_setup (quando nao permitido): 401
3. Token valido sem permissao: 403
4. Token valido com permissao fora do escopo: 403
5. Token valido com permissao no escopo: 2xx

## Riscos e Mitigacoes
- Risco: bloquear endpoints usados por automacoes internas.
  - Mitigacao: mapear consumidores e aplicar `AllowAnonymous` apenas onde existir token proprio.
- Risco: permissao global excessiva inicialmente.
  - Mitigacao: migrar rapidamente para escopo Client/Site nas fases 2 e 3.
- Risco: regressao silenciosa.
  - Mitigacao: testes de autorizacao e auditoria de negacoes.
