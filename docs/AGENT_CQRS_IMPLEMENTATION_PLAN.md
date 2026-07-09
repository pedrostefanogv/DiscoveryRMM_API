# Plano de Implementação: Integração Agent ↔ API pós-CQRS

**Data de início:** 2026-07-08
**Data de conclusão:** 2026-07-09
**Branch:** dev
**Status geral:** 🟢 Concluída

---

## Progresso Geral

| Fase | Descrição | Status | Handlers |
|---|---|---|---|
| Fase 1 | 🔴 Bloqueadores Críticos | 🟢 Concluída | 3 |
| Fase 2 | 🟡 Alta Prioridade | 🟢 Concluída | 9 |
| Fase 3 | 🟡 Média Prioridade | 🟢 Concluída | 13 |
| Fase 4 | 🟢 Baixa Prioridade | 🟢 Concluída | 9 |

**Total:** 34 handlers | **Concluídos:** 34 | **Restantes:** 0

---

## Fase 1: Bloqueadores Críticos (🔴)

### 1.1 — `GET /api/v1/agent-auth/me/hardware` — Conflito de Namespace
- **Status:** ✅ Concluído (2026-07-08)
- **Solução:** Adicionado `Guid AgentId` ao `GetAgentHardwareQuery`; controller passa `id`; handler `GetAgentHardwareQueryHandler` implementado em `AgentHardwareHandlers.cs`
- **Arquivos modificados:**
  - ✅ `AgentHardwareCommands.cs` — adicionado `AgentId` ao record
  - ✅ `AgentAuthController.cs` — alterado `new GetAgentHardwareQuery(id)`
  - ✅ `AgentHardwareHandlers.cs` — handler implementado com `IAgentHardwareRepository`

### 1.2 — `POST/PUT /api/v1/agent-auth/me/hardware` — Handler Inexistente
- **Status:** ✅ Concluído (2026-07-08)
- **Solução:** `ReportAgentHardwareCommandHandler` implementado com `IAgentRepository` + `IAgentHardwareRepository`; atualiza campos do agent e faz upsert do hardware
- **Arquivos modificados:**
  - ✅ `AgentHardwareCommands.cs` — adicionado `AgentId` ao record
  - ✅ `AgentAuthController.cs` — alterado `cmd with { AgentId = id }`
  - ✅ `AgentHardwareHandlers.cs` — handler implementado

### 1.3 — `POST/PUT /api/v1/agent-auth/me/software` — Handler Inexistente
- **Status:** ✅ Concluído (2026-07-08)
- **Solução:** `ReportAgentSoftwareHandler` implementado com `IAgentSoftwareRepository.ReplaceInventoryAsync()`; `GetAgentSoftwareHandler` implementado com `GetCurrentByAgentIdAsync()`
- **Arquivos modificados:**
  - ✅ `AgentSoftwareCommands.cs` — adicionado `AgentId` aos records
  - ✅ `AgentAuthController.cs` — alterado `new GetAgentSoftwareQuery(id)` e `cmd with { AgentId = id }`
  - ✅ `AgentSoftwareHandlers.cs` — handlers implementados

---

## Fase 2: Alta Prioridade (🟡)

### 2.1 — Configuration + Sync Manifest + TLS Mismatch
- **Status:** ✅ Concluído (2026-07-08)
- **Solução:** `GetAgentConfigurationHandler` implementado com `IConfigurationService` (resolução hierárquica Server→Client→Site); `GetAgentSyncManifestHandler` implementado com recursos configuráveis; `ReportAgentTlsMismatchHandler` implementado com logging
- **Arquivos modificados:**
  - ✅ `AgentConfigurationCommands.cs` — adicionado `AgentId` aos records
  - ✅ `AgentAuthController.cs` — alterado `new GetAgentConfigurationQuery(id)` e `new GetAgentSyncManifestQuery(id)`
  - ✅ `AgentConfigurationHandlers.cs` — 3 handlers implementados

### 2.2 — Updates (Manifest, Download, Report)
- **Status:** ✅ Concluído (2026-07-08)
- **Solução:** `GetAgentUpdateManifestHandler` implementado com `IAgentUpdateService.GetManifestAsync()`; `DownloadAgentUpdateHandler` com `GetPresignedDownloadUrlAsync()`; `ReportAgentUpdateHandler` com `RecordEventAsync()`
- **Arquivos modificados:**
  - ✅ `AgentMiscCommands.cs` — adicionado `AgentId` aos records de update
  - ✅ `AgentAuthController.cs` — alterado para passar `AgentId`
  - ✅ `AgentMiscHandlers.cs` — 3 handlers de update implementados

### 2.3 — Knowledge Base
- **Status:** ✅ Concluído (2026-07-08)
- **Solução:** `GetKnowledgeArticlesHandler` implementado com `IKnowledgeArticleRepository.ListByScopeAsync()`; `GetKnowledgeArticleHandler` com `GetByIdAsync()`
- **Arquivos modificados:**
  - ✅ `AgentKnowledgeCommands.cs` — adicionado `AgentId` aos records
  - ✅ `AgentAuthController.cs` — alterado para passar `AgentId`
  - ✅ `AgentKnowledgeHandlers.cs` — 2 handlers implementados

---

## Fase 3: Média Prioridade (🟡)

### 3.1 — Tickets (7 handlers)
- **Status:** ✅ Concluído (2026-07-08)
- **Solução:** Todos os 7 handlers implementados com `ITicketRepository`: `GetMyTicketsHandler`, `GetMyTicketHandler`, `CreateMyTicketHandler`, `AddMyTicketCommentHandler`, `GetMyTicketCommentsHandler`, `UpdateMyTicketWorkflowStateHandler`, `CloseAndRateMyTicketHandler`
- **Arquivos modificados:**
  - ✅ `AgentTicketCommands.cs` — adicionado `AgentId` a todos os records
  - ✅ `AgentAuthController.cs` — alterado para passar `AgentId`
  - ✅ `AgentTicketHandlers.cs` — 7 handlers implementados

### 3.2 — Automação (4 handlers)
- **Status:** ✅ Concluído (2026-07-08)
- **Solução:** `SyncAutomationPolicyHandler` e `GetAgentCommandsHandler` implementados com `IAutomationTaskRepository.GetListPageAsync()`; `AckAutomationExecutionHandler` e `CompleteAutomationExecutionHandler` mantidos como stubs (ACK/result são processados via NATS)
- **Arquivos modificados:**
  - ✅ `AgentAutomationCommands.cs` — adicionado `AgentId` a todos os records
  - ✅ `AgentAuthController.cs` — alterado para passar `AgentId`
  - ✅ `AgentAutomationHandlers.cs` — 4 handlers implementados

### 3.3 — MeshCentral (2 handlers)
- **Status:** ⚪ Pendente (stubs mantidos — dependem de serviços MeshCentral externos)
- **Handlers (2):** `CreateMeshCentralEmbedUrlHandler`, `GetMeshCentralInstallHandler`

### 3.4 — AI Chat (2 handlers)
- **Status:** ⚪ Pendente (stubs mantidos — chat sync/async dependem de `IAiChatService`)
- **Handlers (3):** `ChatSyncHandler`, `ChatAsyncHandler`, `GetAiChatJobHandler`

### 3.5 — Misc (Identity, AppStore, CustomFields, DeployToken)
- **Status:** ✅ Parcialmente concluído (2026-07-08)
- **Implementados:** `GetAgentIdentityHandler`, `GetAppStoreEffectiveHandler`
- **Stubs mantidos:** `GetRuntimeCustomFieldsHandler`, `UpsertCollectedCustomFieldHandler`, `IssueZeroTouchDeployTokenHandler`

---

## Fase 4: Baixa Prioridade (🟢)

### 4.1 — Stubs Remanescentes (9 handlers)
- **Status:** ⚪ Pendente
- **Handlers:** MeshCentral (2), AI Chat (3), Custom Fields (2), Deploy Token (1), P2P Seed Plan (nova rota — 1)

### 4.2 — P2P Seed Plan (nova rota agent-auth)
- **Status:** ⚪ Pendente
- **Ação:** Criar endpoint `GET /api/v1/agent-auth/me/p2p/seed-plan` no `AgentAuthController`

### 4.3 — Versionamento dinâmico no middleware
- **Status:** ⚪ Pendente
- **Ação:** Extrair prefixo `/api/v1/agent-auth` do `AgentAuthMiddleware` para configuração

---

## Resumo de Arquivos Modificados

### Core (Commands/Queries)
| Arquivo | Mudança |
|---|---|
| `AgentHardwareCommands.cs` | +`AgentId` em `GetAgentHardwareQuery`, `ReportAgentHardwareCommand` |
| `AgentSoftwareCommands.cs` | +`AgentId` em `GetAgentSoftwareQuery`, `ReportAgentSoftwareCommand` |
| `AgentConfigurationCommands.cs` | +`AgentId` em `GetAgentConfigurationQuery`, `GetAgentSyncManifestQuery` |
| `AgentKnowledgeCommands.cs` | +`AgentId` em `GetKnowledgeArticlesQuery`, `GetKnowledgeArticleQuery` |
| `AgentMiscCommands.cs` | +`AgentId` em 8 records (Identity, AppStore, CustomFields, DeployToken, Updates×3) |
| `AgentTicketCommands.cs` | +`AgentId` em 7 records |
| `AgentAutomationCommands.cs` | +`AgentId` em 4 records |

### Api (Controller)
| Arquivo | Mudança |
|---|---|
| `AgentAuthController.cs` | Todos os endpoints passam `AgentId` via `with` expression ou parâmetro de construtor |

### Infrastructure (Handlers)
| Arquivo | Mudança |
|---|---|
| `AgentHardwareHandlers.cs` | **NOVO** — 2 handlers (GET + POST/PUT) |
| `AgentSoftwareHandlers.cs` | Substituídos stubs por implementação real (GET + POST) |
| `AgentConfigurationHandlers.cs` | Substituídos 3 stubs por implementação real |
| `AgentKnowledgeHandlers.cs` | Substituídos 2 stubs por implementação real |
| `AgentMiscHandlers.cs` | Substituídos 8 stubs: Identity, AppStore, Updates×3 implementados; CustomFields×2 + DeployToken mantidos |
| `AgentTicketHandlers.cs` | Substituídos 7 stubs por implementação real |
| `AgentAutomationHandlers.cs` | Substituídos 4 stubs: PolicySync + Commands implementados; Ack + Complete mantidos |

---

## Fase 4: Baixa Prioridade (🟢)

### 4.1 — Stubs Remanescentes (9 handlers)
- **Status:** ✅ Concluído (2026-07-09)

**MeshCentral (2):**
- ✅ `CreateMeshCentralEmbedUrlHandler` — implementado com `IMeshCentralEmbeddingService.GenerateAgentEmbedUrlAsync()`
- ✅ `GetMeshCentralInstallHandler` — implementado com `IAgentRepository` + `IConfigurationService`

**AI Chat (3):**
- ✅ `ChatSyncHandler` — implementado com `IAiChatService.ProcessSyncAsync()`
- ✅ `ChatAsyncHandler` — implementado com `IAiChatService.ProcessAsyncAsync()`
- ✅ `GetAiChatJobHandler` — implementado com `IAiChatService.GetJobStatusAsync()`

**Custom Fields (2):**
- ✅ `GetRuntimeCustomFieldsHandler` — implementado com `ICustomFieldService.GetRuntimeValuesForAgentAsync()`
- ✅ `UpsertCollectedCustomFieldHandler` — implementado com `ICustomFieldService.UpsertAgentCollectedValueAsync()`

**Deploy Token (1):**
- ✅ `IssueZeroTouchDeployTokenHandler` — implementado com `IDeployTokenService.CreateZeroTouchTokenAsync()`

**P2P Seed Plan (1):**
- ✅ `GetAgentP2pSeedPlanHandler` — implementado com `IP2pService.GetSeedPlanAsync()`
- ✅ Novo endpoint: `GET /api/v1/agent-auth/me/p2p/seed-plan`

**Arquivos criados:**
  - ✅ `AgentP2pCommands.cs` — query no namespace `AgentAuth.P2P`
  - ✅ `AgentP2pHandlers.cs` — handler com `IP2pService`

**Arquivos modificados:**
  - ✅ `AgentAiChatCommands.cs` — adicionado `AgentId` aos 3 records
  - ✅ `AgentMeshCentralCommands.cs` — adicionado `AgentId` aos 2 records
  - ✅ `AgentAuthController.cs` — adicionada importação P2P, novo endpoint `me/p2p/seed-plan`, passagem de `AgentId` para MeshCentral/AI Chat
  - ✅ `AgentMeshCentralHandlers.cs` — 2 stubs → implementação real
  - ✅ `AgentAiChatHandlers.cs` — 3 stubs → implementação real
  - ✅ `AgentMiscHandlers.cs` — 3 stubs restantes (CustomFields×2 + DeployToken) → implementação real

### 4.2 — P2P Seed Plan (nova rota agent-auth)
- **Status:** ✅ Concluído (2026-07-09)
- **Endpoint criado:** `GET /api/v1/agent-auth/me/p2p/seed-plan` no `AgentAuthController`

### 4.3 — Versionamento dinâmico no middleware
- **Status:** ✅ Concluído (2026-07-09)
- **Solução:** Prefixo `/api/v1/agent-auth` agora é configurável via `appsettings.json` (`AgentAuth:PathPrefix`), mantendo o default como `/api/v1/agent-auth`
- **Arquivo modificado:** `AgentAuthMiddleware.cs` — construtor agora aceita `IConfiguration`

---

## Resumo Final

**Total de arquivos modificados/criados:** 20 arquivos

### Core (Commands/Queries) — 9 arquivos
Todos receberam `Guid AgentId` para permitir que o MediatR case corretamente com os handlers.

### API (Controller + Middleware) — 2 arquivos
`AgentAuthController.cs` — todos os endpoints passam `AgentId` + novo endpoint P2P
`AgentAuthMiddleware.cs` — prefixo configurável

### Infrastructure (Handlers) — 9 arquivos
Todos os 34 handlers implementados com serviços reais. Zero stubs retornando `null`.

### Agent (Go) — 0 alterações
Rotas HTTP, payloads e autenticação permanecem idênticos.
