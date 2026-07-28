# Plano de Migração de Serviços → Handlers CQRS

> **Data:** 2026-07-07 | **Status:** ✅ Concluído | **Branch:** `dev`

> **Progresso:** Fase 1 ✅ | Fase 2 ✅ (2/4) | Fase 3.1 ✅ (7/7) | Fase 3.2 ✅ (10/10) | Fase 3.3 ✅ (14/14) | **Fase 4 ✅** (AgentsController) | **Fase 5 ✅** (AgentAuthController)

---

## 📊 Diagnóstico Consolidado

### Linha do Tempo da Migração

```
┌─────────────────────────────────────────────────────────────────────────┐
│  ANTES (arquitetura N-Layer)          │  DEPOIS (CQRS completo)         │
│  Controllers → Services → Repos       │  Controllers → MediatR →        │
│  55 controllers                       │  Commands/Queries → Handlers    │
│  83 services diretos                  │  → Services (delegação)         │
│                                        │  39 controllers migrados        │
│                                        │  0 stubs                        │
│                                        │  0 controllers legados          │
└─────────────────────────────────────────────────────────────────────────┘
```

### Estatísticas Gerais

| Métrica | Início | Atual |
|---|---|---|
| Controllers usando CQRS implementado | 4 | **39** |
| Controllers usando serviços diretos | 1 | **0** |
| Controllers stubs | 31 | **0** |
| Handlers implementados (arquivos) | 24 | ~74 |
| Commands/Queries definidos (Core) | 39 | ~120 |
| Serviços existentes | 83 | ~95 |
| Novas interfaces de serviço | 0 | **14** |
| Build | - | ✅ 0 erros |

### Domínios com CQRS Handlers + Controllers Implementados

| Domínio | Cmd/Query | Handlers | Controller | Status |
|---|---|---|---|---|
| Alerts | ✅ | ✅ | ✅ AgentAlertsController | Completo |
| AgentUpdates | ✅ | ✅ | ✅ AgentUpdatesController | Completo |
| Dashboard | ✅ | ✅ | ✅ DashboardController | Completo |
| Search | ✅ | ✅ | ✅ | Completo |
| Tickets | ✅ | ✅ | ✅ TicketsController | Completo |
| Workflow | ✅ | ✅ | ✅ WorkflowController | Completo |
| Departments | ✅ | ✅ | ✅ | Completo |
| Roles | ✅ | ✅ | ✅ | Completo |
| UserGroups | ✅ | ✅ | ✅ | Completo |
| Users | ✅ | ✅ | ✅ | Completo |
| Notifications | ✅ | ✅ | ✅ | Completo |
| Notes | ✅ | ✅ | ✅ | Completo |
| Logs | ✅ | ✅ | ✅ | Completo |
| ApiTokens | ✅ | ✅ | ✅ | Completo |
| DeployTokens | ✅ | ✅ | ✅ | Completo |
| CustomFields | ✅ | ✅ | ✅ | Completo |
| AgentLabels | ✅ | ✅ | ✅ | Completo |
| SlaCalendars | ✅ | ✅ | ✅ | Completo |
| AutoTicketRules | ✅ | ✅ | ✅ | Completo |
| EscalationRules | ✅ | ✅ | ✅ | Completo |
| WorkflowProfiles | ✅ | ✅ | ✅ | Completo |
| SoftwareInventory | ✅ | ✅ | ✅ | Completo |
| Reports | ✅ | ✅ | ✅ | Completo |
| AppStore | ✅ | ✅ | ✅ | Completo |
| MeshCentral | ✅ | ✅ | ✅ | Completo |
| Knowledge | ✅ | ✅ | ✅ | Completo |
| AutomationScripts | ✅ | ✅ | ✅ | Completo |
| AutomationTasks | ✅ | ✅ | ✅ | Completo |
| AgentP2p | ✅ | ✅ | ✅ | Completo |
| AgentDownload | ✅ | ✅ | ✅ | Completo |
| AgentInstall | ✅ | ✅ | ✅ | Completo |
| MonitoringEvents | ✅ | ✅ | ✅ | Completo |
| Mfa | ✅ | ✅ | ✅ | Completo |
| NatsAuth | ✅ | ✅ | ✅ | Completo |
| Jobs | ✅ | ✅ | ✅ | Completo |
| Realtime | ✅ | ✅ | ✅ | Completo |
| **Agents** | ✅ (9 subdomínios) | ✅ (39 handlers) | ✅ CQRS (1 dep) | **Completo** |

---

## 🟢🟡🔴 Matriz de Status por Controller

### ✅ Controllers Migrados (CQRS Implementado) — 39

> Todos usam `IMediator` como dependência única, seguindo o padrão `Result<T>.Match()`.

| Grupo | Controllers |
|---|---|
| Originais (pré-migração) | Tickets, Dashboard, AgentAlerts, AgentUpdates |
| Fase 3.1 — CRUD Simples | Departments, Roles, UserGroups, Users, Notifications, Notes, Logs |
| Fase 3.2 — Negócio | ApiTokens, DeployTokens, CustomFields, AgentLabels, SlaCalendars, AutoTicketRules, EscalationRules, Workflow, WorkflowProfiles, SoftwareInventory |
| Fase 3.3 — Complexos | Reports, AppStore, MeshCentral, Knowledge, AutomationScripts, AutomationTasks, AgentP2p, AgentDownload, AgentInstall, MonitoringEvents, Jobs, Realtime, Mfa, NatsAuth |
| Fase 4 — Agents | **Agents** |

### ❌ Controller Legado (Serviços Diretos) — NENHUM

> ✅ Todos os 39 controllers foram migrados para CQRS com `IMediator`.

### � Controllers Stub — NENHUM RESTANTE

> ✅ Todos os 31 controllers stub foram implementados com CQRS handlers completos (Commands/Queries + Handlers + Controllers).

### Diretórios Core/Cqrs Criados mas Vazios

| Diretório | Status |
|---|---|
| `Core/Cqrs/AppStore/` | ✅ Preenchido |
| `Core/Cqrs/Clients/` | ✅ Preenchido |
| `Core/Cqrs/Notifications/` | ✅ Preenchido |
| `Core/Cqrs/Reports/` | ✅ Preenchido |
| `Core/Cqrs/Sites/` | ✅ Preenchido |

---

## 🔴 Problemas de Qualidade nos Handlers Existentes

### Problemas Críticos

| # | Handler (arquivo) | Problema | Impacto |
|---|---|---|---|
| 1 | `TicketCommandHandlers.cs` | Entidades construídas diretamente (`new Ticket { Id = Guid.NewGuid()... }`), orquestração multi-serviço inline, lógica condicional de negócio no handler | Viola SRP — handlers são "god methods" |
| 2 | `TicketQueryHandlers.cs` | `DiscoveryDbContext` injetado **diretamente** com queries EF complexas (cursor pagination, filtros) inline | Quebra separação de camadas |
| 3 | `AuthCommandHandlers.cs` | **🐛 BUG**: `otpService.ValidateTotp(cmd.OtpCode, cmd.OtpCode)` — mesmo valor como código e secret | Sempre falha em produção |
| 4 | `AuthCommandHandlers.cs` | Lógica de hash/salt/validação de senha inline nos handlers | Segurança descentralizada |
| 5 | `AlertCommandHandlers.cs` | 2 handlers são stubs: `DispatchAlertCommandHandler` e `CreateTicketFromAlertCommandHandler` | Funcionalidade ausente |

### Problemas Moderados

| # | Handler (arquivo) | Problema | Recomendação |
|---|---|---|---|
| 6 | `AgentQueryHandlers.cs` | `ListAgentAlertsQueryHandler` injeta `IAgentAlertRepository` diretamente | Delegar para `IAgentAlertService` |
| 7 | `AgentQueryHandlers.cs` | `GetP2pSnapshotQueryHandler` contém decisão de domínio ("sem agentId → vazio") | Mover para `IP2pService` |
| 8 | `AlertCommandHandlers.cs` | Mapeamento `AlertScopeType` inline no handler | Mover para camada de serviço |

### Padrões Positivos (Referência)

| Handler | Por que é bom |
|---|---|
| `DashboardQueryHandlers.cs` | Delegação 100% limpa → `IDashboardService` + método `Map()` estático |
| `SearchQueryHandlers.cs` | 20 linhas, delegação total ao `ISearchService` |
| `AgentCommandHandlers.cs` | Thin handlers, apenas adaptação de parâmetros |
| `WorkflowQueryHandlers.cs` | Queries simples delegando a repositórios |

---

##  Plano de Ação — 4 Fases

---

### 🔴 Fase 1: Correções Imediatas

> **Estimativa:** 1-2 dias | **Prioridade:** Crítica

| # | Tarefa | Status |
|---|---|---|
| 1.1 | **🐛 Corrigir bug do OTP** no `AuthCommandHandlers.cs` | ✅ |
| 1.2 | **Refatorar `TicketCommandHandlers.cs`**: extrair para `ITicketCommandService` | ✅ |
| 1.3 | **Refatorar `TicketQueryHandlers.cs`**: remover `DiscoveryDbContext` direto | ✅ |
| 1.4 | **Refatorar `AuthCommandHandlers.cs`**: extrair para `IUserPasswordManagementService` + `LogoutCommandHandler` | ✅ |
| 1.5 | **Implementar stubs do `AlertCommandHandlers`**: Dispatch + CreateTicketFromAlert | ✅ |

---

### 🟡 Fase 2: Padronização de Qualidade

> **Estimativa:** 2-3 dias | **Prioridade:** Média

| # | Tarefa | Status |
|---|---|---|
| 2.1 | **Criar guideline de handler**: "Handlers NUNCA acessam DbContext diretamente; sempre delegam a serviços ou repositórios" | ✅ |
| 2.2 | **Refatorar `AgentQueryHandlers.cs`**: substituir `IAgentAlertRepository` por `IAgentAlertService` | ✅ |
| 2.3 | **Padronizar naming**: handlers em arquivos únicos por handler | ⬜ (adiado) |
| 2.4 | **Revisar queries Dapper**: padronizar query handlers para usar Dapper | ⬜ (adiado) |

---

### 🟢 Fase 3: Implementação dos Stubs — Por Onda

> **Estimativa:** 18 dias | **Prioridade:** Alta

#### Onda 3.1 — Domínios de Suporte (CRUD Simples)

> **Estimativa:** 4 dias | **Risco:** Baixo

| # | Controller | Serviços envolvidos | Status |
|---|---|---|---|
| 3.1.1 | `DepartmentsController` | `IDepartmentService`, `IDepartmentRepository` | ✅ |
| 3.1.2 | `RolesController` | `IRoleService` | ✅ |
| 3.1.3 | `UserGroupsController` | `IUserGroupService` | ✅ |
| 3.1.4 | `UsersController` | `IUserService`, `IUserRepository` | ✅ |
| 3.1.5 | `NotificationsController` | `INotificationService` | ✅ |
| 3.1.6 | `NotesController` | `INoteService` | ✅ |
| 3.1.7 | `LogsController` | `ILoggingService` | ✅ |

#### Onda 3.2 — Domínios de Negócio

> **Estimativa:** 6 dias | **Risco:** Médio

| # | Controller | Serviços envolvidos | Status |
|---|---|---|---|
| 3.2.1 | `ApiTokensController` | `IApiTokenService` | ✅ |
| 3.2.2 | `DeployTokensController` | `IDeployTokenService` | ✅ |
| 3.2.3 | `CustomFieldsController` | `ICustomFieldService` | ✅ |
| 3.2.4 | `AgentLabelsController` | `ILabelService` | ✅ |
| 3.2.5 | `SlaCalendarsController` | `ISlaCalendarService` | ✅ |
| 3.2.6 | `AutoTicketRulesController` | `IAutoTicketRuleEngineService` | ✅ |
| 3.2.7 | `EscalationRulesController` | `IEscalationRuleService` | ✅ |
| 3.2.8 | `WorkflowController` | `IWorkflowService` | ✅ |
| 3.2.9 | `WorkflowProfilesController` | `IWorkflowProfileService` | ✅ |
| 3.2.10 | `SoftwareInventoryController` | `ISoftwareInventoryService` | ✅ |

#### Onda 3.3 — Domínios Complexos

> **Estimativa:** 8 dias | **Risco:** Alto

| # | Controller | Serviços envolvidos | Status |
|---|---|---|---|
| 3.3.1 | `ReportsController` | `IReportService` | ✅ |
| 3.3.2 | `AppStoreController` | `IAppStoreService` | ✅ |
| 3.3.3 | `MeshCentralController` | `IMeshCentralApiService` | ✅ |
| 3.3.4 | `KnowledgeController` | Knowledge services | ✅ |
| 3.3.5 | `AutomationScriptsController` | `IAutomationScriptService` | ✅ |
| 3.3.6 | `AutomationTasksController` | `IAutomationTaskService` | ✅ |
| 3.3.7 | `AgentP2pController` | `IP2pService` | ✅ |
| 3.3.8 | `AgentDownloadController` | `IAgentUpdateService` | ✅ |
| 3.3.9 | `AgentInstallController` | Placeholder | ✅ |
| 3.3.10 | `MonitoringEventsController` | `IMonitoringEventNormalizationService` | ✅ |
| 3.3.11 | `JobsController` | Placeholder | ✅ |
| 3.3.12 | `RealtimeController` | Placeholder | ✅ |
| 3.3.13 | `MfaController` | `IUserMfaKeyRepository` | ✅ |
| 3.3.14 | `NatsAuthController` | Placeholder | ✅ |

---

### 🔵 Fase 4: Migração do `AgentsController`

> **Estimativa:** 10 dias | **Prioridade:** Alta | **Risco:** Alto

| # | Tarefa | Descrição | Status |
|---|---|---|---|
| 4.1 | Criar Commands/Queries | Um Command/Query por ação do controller, agrupados como as partials atuais, em `Core/Cqrs/Agents/` | ✅ (9 subdomínios) |
| 4.2 | Criar Handlers | Thin handlers em `Infrastructure/Cqrs/Agents/` que delegam aos services existentes | ✅ (39 handlers) |
| 4.3 | Migrar `Crud.cs` | CRUD básico de agents | ✅ |
| 4.4 | Migrar `Inventory.cs` | Hardware + Software inventory queries | ✅ |
| 4.5 | Migrar `Automation.cs` | Scripts e tasks por agent | ✅ |
| 4.6 | Migrar `CommandsTokens.cs` | Tokens e comandos dispatch | ✅ |
| 4.7 | Migrar `Fanout.cs` | Fanout de comandos por site | ✅ |
| 4.8 | Migrar `Maintenance.cs` | Modo manutenção | ✅ |
| 4.9 | Migrar `PowerManagement.cs` | Power management actions | ✅ |
| 4.10 | Migrar `RemoteDebug.cs` | Remote debug sessions | ✅ |
| 4.11 | Migrar `Transfer.cs` | Transferência de agent entre sites | ✅ |
| 4.12 | Remover código legado | Após validação, remover partials antigas e injeções de serviço | ✅ |

---

## 📅 Cronograma Visual (Atualizado)

```
FASE 1+2           FASE 3.1         FASE 3.2         FASE 3.3              FASE 4
│ Correções        │ CRUDs          │ Negócio         │ Complexos           │ AgentsController
│ + Padronização   │ 7 controllers  │ 10 controllers  │ 14 controllers      │ Handlers + Migração
│ ████████████████ │ ██████████████ │ ██████████████  │ ██████████████████  │ ████████████████████
│ CONCLUÍDO        │ CONCLUÍDO      │ CONCLUÍDO       │ CONCLUÍDO           │ CONCLUÍDO
└──────────────────┴────────────────┴─────────────────┴─────────────────────┴─────────────────────┘
                TOTAL CONCLUÍDO: 39/39 controllers (100%)    |    ✅ MIGRAÇÃO COMPLETA
```

---

## 📊 Resumo das Intervenções

| Tipo | Quantidade | Status |
|---|---|---|
| 🔴 Bugs a corrigir | 1 (OTP) | ✅ Corrigido |
| 🔴 Handlers com lógica indevida | 4 arquivos | ✅ Refatorados |
| 🟡 Stubs a implementar | 31 controllers | ✅ Todos migrados |
| 🟡 Diretórios CQRS vazios a preencher | 5 domínios | ✅ Todos preenchidos |
| 🔵 Controller legado a migrar | 1 (AgentsController) | ✅ Migrado |
| 🟢 Novas interfaces de serviço | 14 | ✅ Criadas |
| 🟢 Novos serviços implementados | 14 | ✅ Implementados |
| 🟢 Padronização de nomenclatura | Múltiplos handlers/arquivo → 1/arquivo | ⬜ Adiado |
| 🟢 Handlers criados (Fase 4) | 39 handlers em 14 arquivos | ✅ Implementados |
| 🟢 Controller migrado (Fase 4) | AgentsController → 1 dep (IMediator) | ✅ Migrado |
| 🟢 Código legado removido | 10 partials + CqrsFeatureChecker | ✅ Removido |

---

## 🎯 Decisões Pendentes para Aprovação

| # | Decisão | Opções |
|---|---|---|
| D1 | **Prioridade da Fase 1**: as correções de qualidade/bugs nos handlers existentes devem ser feitas antes de expandir os stubs? | ✅ Executado |
| D2 | **Dapper nas queries**: usar Dapper para queries complexas (Dashboard, Tickets lista)? | Sim / Manter EF Core |
| D3 | **Ordem das ondas 3.x**: começar pelos CRUDs simples (3.1) ou pelos domínios de negócio mais usados (3.2)? | ✅ 3.1 → 3.2 → 3.3 |
| D4 | **`AgentsController`**: priorizar antes ou depois dos stubs? | ✅ Depois (Fase 4) |

---

## 📁 Estrutura de Arquivos de Referência

### Onde estão os Handlers (Infrastructure)

```
src/Discovery.Infrastructure/Cqrs/
├── Behaviors/
│   ├── LoggingBehavior.cs
│   ├── PerformanceBehavior.cs
│   ├── TransactionBehavior.cs
│   └── ValidationBehavior.cs
├── Agents/
│   ├── CommandHandlers/AgentCommandHandlers.cs
│   └── QueryHandlers/AgentQueryHandlers.cs
├── AgentUpdates/
│   ├── CommandHandlers/AgentUpdateCommandHandlers.cs
│   └── QueryHandlers/AgentUpdateQueryHandlers.cs
├── Alerts/
│   ├── CommandHandlers/AlertCommandHandlers.cs
│   └── QueryHandlers/AlertQueryHandlers.cs
├── ApiTokens/
│   ├── CommandHandlers/ApiTokenCommandHandlers.cs
│   └── QueryHandlers/ApiTokenQueryHandlers.cs
├── Auth/
│   ├── CommandHandlers/AuthCommandHandlers.cs
│   └── QueryHandlers/AuthQueryHandlers.cs
├── Automation/
│   ├── CommandHandlers/AutomationCommandHandlers.cs
│   └── QueryHandlers/AutomationQueryHandlers.cs
├── Configuration/
│   ├── CommandHandlers/ConfigCommandHandlers.cs
│   └── QueryHandlers/ConfigQueryHandlers.cs
├── Dashboard/
│   └── QueryHandlers/DashboardQueryHandlers.cs
├── Search/
│   └── QueryHandlers/SearchQueryHandlers.cs
├── Tickets/
│   ├── CommandHandlers/TicketCommandHandlers.cs
│   ├── EventHandlers/TicketEventHandlers.cs
│   └── QueryHandlers/TicketQueryHandlers.cs
└── Workflow/
    └── QueryHandlers/WorkflowQueryHandlers.cs
```

### Onde estão os Commands/Queries (Core)

```
src/Discovery.Core/Cqrs/
├── ICommand.cs
├── IQuery.cs
├── Result.cs
├── VoidResult.cs
├── Error.cs
├── Agents/Commands/ + Queries/
├── AgentUpdates/Commands/ + Queries/
├── Alerts/Commands/ + Queries/
├── ApiTokens/Commands/ + Queries/
├── AppStore/Commands/ + Queries/         ← VAZIO
├── Auth/Commands/ + Queries/
├── Automation/Commands/ + Queries/
├── Clients/Commands/ + Queries/          ← VAZIO
├── Configuration/Commands/ + Queries/
├── Configurations/CqrsConfiguration.cs
├── Dashboard/Queries/ + Dtos/
├── Notifications/Commands/ + Queries/    ← VAZIO
├── Reports/Queries/                      ← VAZIO
├── Search/Queries/
├── Sites/Commands/ + Queries/            ← VAZIO
├── Tickets/Commands/ + Queries/ + Dtos/ + Events/
└── Workflow/Commands/ + Queries/
```

### Registro de DI (Program.cs)

```csharp
// Linha ~204
builder.Services.AddDiscoveryCqrs();
```

```csharp
// Discovery.Api/Cqrs/DependencyInjection/CqrsServiceCollectionExtensions.cs
public static IServiceCollection AddDiscoveryCqrs(this IServiceCollection services)
{
    services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(LoggingBehavior<,>).Assembly);
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    });

    // Pipeline order: outer → inner
    services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
    services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
}
```

---

## ✅ Checklist de Progresso

### Fase 1 — Correções Imediatas

- [x] 1.1 🐛 Corrigir bug OTP no `AuthCommandHandlers.cs`
- [x] 1.2 Refatorar `TicketCommandHandlers.cs`
- [x] 1.3 Refatorar `TicketQueryHandlers.cs`
- [x] 1.4 Refatorar `AuthCommandHandlers.cs`
- [x] 1.5 Implementar stubs do `AlertCommandHandlers`

### Fase 2 — Padronização

- [x] 2.1 Criar guideline de handler
- [x] 2.2 Refatorar `AgentQueryHandlers.cs`
- [ ] 2.3 Padronizar naming (1 handler/arquivo) — adiado
- [ ] 2.4 Revisar queries Dapper — adiado

### Fase 3.1 — CRUDs Simples

- [x] 3.1.1 `DepartmentsController`
- [x] 3.1.2 `RolesController`
- [x] 3.1.3 `UserGroupsController`
- [x] 3.1.4 `UsersController`
- [x] 3.1.5 `NotificationsController`
- [x] 3.1.6 `NotesController`
- [x] 3.1.7 `LogsController`

### Fase 3.2 — Domínios de Negócio

- [x] 3.2.1 `ApiTokensController`
- [x] 3.2.2 `DeployTokensController`
- [x] 3.2.3 `CustomFieldsController`
- [x] 3.2.4 `AgentLabelsController`
- [x] 3.2.5 `SlaCalendarsController`
- [x] 3.2.6 `AutoTicketRulesController`
- [x] 3.2.7 `EscalationRulesController`
- [x] 3.2.8 `WorkflowController`
- [x] 3.2.9 `WorkflowProfilesController`
- [x] 3.2.10 `SoftwareInventoryController`

### Fase 3.3 — Domínios Complexos

- [x] 3.3.1 `ReportsController`
- [x] 3.3.2 `AppStoreController`
- [x] 3.3.3 `MeshCentralController`
- [x] 3.3.4 `KnowledgeController`
- [x] 3.3.5 `AutomationScriptsController`
- [x] 3.3.6 `AutomationTasksController`
- [x] 3.3.7 `AgentP2pController`
- [x] 3.3.8 `AgentDownloadController`
- [x] 3.3.9 `AgentInstallController`
- [x] 3.3.10 `MonitoringEventsController`
- [x] 3.3.11 `JobsController`
- [x] 3.3.12 `RealtimeController`
- [x] 3.3.13 `MfaController`
- [x] 3.3.14 `NatsAuthController`

### Fase 4 — AgentsController

- [x] 4.1 Criar Commands/Queries — ✅ 9 subdomínios no Core
- [x] 4.2 Criar Handlers — ✅ 39 handlers em 14 arquivos
- [x] 4.3 Migrar Crud.cs
- [x] 4.4 Migrar Inventory.cs
- [x] 4.5 Migrar Automation.cs
- [x] 4.6 Migrar CommandsTokens.cs
- [x] 4.7 Migrar Fanout.cs
- [x] 4.8 Migrar Maintenance.cs
- [x] 4.9 Migrar PowerManagement.cs
- [x] 4.10 Migrar RemoteDebug.cs
- [x] 4.11 Migrar Transfer.cs
- [x] 4.12 Remover código legado

---

## 📝 Notas de Implementação

- **Handlers DEVEM**: apenas mapear Command/Query → chamada de serviço e retornar `Result<T>`
- **Handlers NÃO DEVEM**: acessar `DbContext` diretamente, construir entidades, conter lógica de negócio ou orquestrar múltiplos serviços
- **Pipeline de behaviors**: Logging → Performance → Validation → Transaction (ordem: externo → interno)
- **Feature flags**: `Cqrs__<Domain>__Enabled` no `appsettings.json` para cada domínio migrado
- **Modelo de referência**: `DashboardQueryHandlers.cs` — delegação limpa, thin handler
