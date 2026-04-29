# Plano de Refatoração & Melhorias — DiscoveryRMM_API

> **Criado em:** 29/04/2026  
> **Branch:** dev  
> **Objetivo:** Documentar e rastrear o progresso das melhorias identificadas na análise geral do projeto.

---

## 🔴 Prioridade Alta (Crítico)

### 1. Refatorar `Program.cs` — Extrair extension methods
- **Arquivo:** `src/Discovery.Api/Program.cs` (~300 linhas após as extrações)
- **Problema:** Toda a configuração de serviços está concentrada em um único arquivo.
- **Ação:** Extrair para extension methods na pasta `DependencyInjection/`.
- **Nota de fechamento:** limpeza final concluída em 29/04/2026, com a remoção do registro duplicado de `AlertSchedulerBackgroundService` do `Program.cs` e consolidação das dependências de alertas em `BackgroundServicesCollectionExtensions.cs`.

| # | Módulo | Arquivo destino | Status |
|---|--------|-----------------|--------|
| 1.1 | NATS | `NatsServiceCollectionExtensions.cs` | ✅ Concluído |
| 1.2 | Redis | `RedisServiceCollectionExtensions.cs` | ✅ Concluído |
| 1.3 | Rate Limiting | `RateLimitingServiceCollectionExtensions.cs` | ✅ Concluído |
| 1.4 | Background Services | `BackgroundServicesCollectionExtensions.cs` | ✅ Concluído |
| 1.5 | CORS | `CorsServiceCollectionExtensions.cs` | ✅ Concluído |
| 1.6 | Maintenance Mode | `MaintenanceMode.cs` (arquivo separado) | ✅ Concluído |
| 1.7 | AgentPackage Startup | `AgentPackageStartup.cs` (arquivo separado) | ✅ Concluído |

### 2. Refatorar `AgentAuthController` — God Controller (30+ dependências)
- **Arquivo original:** `src/Discovery.Api/Controllers/AgentAuthController.cs` (~2555 linhas)
- **Problema:** Viola SRP, difícil testar e manter.
- **Ação:** Quebrar em partial classes por domínio na pasta `Controllers/AgentAuth/`.

| # | Partial file | Responsabilidade | Endpoints | Status |
|---|-------------|-----------------|-----------|--------|
| 2.1 | `AgentAuthController.cs` | Base: construtor, DI, helpers de auth/NATS/monitoring | — | ✅ Concluído |
| 2.2 | `AgentAuthController.Configuration.cs` | Configuração, sync manifest, TLS mismatch, NATS creds | 4 | ✅ Concluído |
| 2.3 | `AgentAuthController.Hardware.cs` | Inventário de hardware + parsing JSON | 3 | ✅ Concluído |
| 2.4 | `AgentAuthController.Software.cs` | Inventário de software | 3 | ✅ Concluído |
| 2.5 | `AgentAuthController.Tickets.cs` | Tickets: CRUD, comentários, workflow, close/rate | 7 | ✅ Concluído |
| 2.6 | `AgentAuthController.Automation.cs` | Automação: policy sync, commands, exec ack/result | 4 | ✅ Concluído |
| 2.7 | `AgentAuthController.Misc.cs` | App store, custom fields, deploy tokens, updates, sync ping | 7 | ✅ Concluído |
| 2.8 | `AgentAuthController.MeshCentral.cs` | MeshCentral embed URL + instalação | 2 | ✅ Concluído |
| 2.9 | `AgentAuthController.AiChat.cs` | AI Chat: sync, async, streaming, job status | 4 | ✅ Concluído |
| 2.10 | `AgentAuthController.P2pKnowledge.cs` | P2P bootstrap + Knowledge Base | 3 | ✅ Concluído |
| 2.11 | `HardwareInventoryParser.cs` | Parsing de inventário JSON extraído | — | ✅ Concluído |
| 2.12 | `ParseJson.cs` | Helpers genéricos de parsing JSON | — | ✅ Concluído |

### 3. Refatorar `AgentsController` (16+ dependências)
- **Arquivo original:** `src/Discovery.Api/Controllers/AgentsController.cs` (~912 linhas)
- **Ação:** Separar em partial classes na pasta `Controllers/Agents/`.

| # | Partial file | Responsabilidade | Endpoints | Status |
|---|-------------|-----------------|-----------|--------|
| 3.1 | `AgentsController.cs` | Base: construtor, DI, Redis cache, grace helpers | — | ✅ Concluído |
| 3.2 | `AgentsController.Crud.cs` | CRUD, zero-touch, list-by-scope, custom fields | 9 | ✅ Concluído |
| 3.3 | `AgentsController.Inventory.cs` | Hardware, software, software snapshot | 3 | ✅ Concluído |
| 3.4 | `AgentsController.Automation.cs` | Run-now, force-sync, execution history | 4 | ✅ Concluído |
| 3.5 | `AgentsController.RemoteDebug.cs` | Remote debug start/stop | 2 | ✅ Concluído |
| 3.6 | `AgentsController.CommandsTokens.cs` | Commands + tokens + DTO records | 6 | ✅ Concluído |

---

## 🟠 Prioridade Média (Importante)

### 4. Adicionar Health Checks
- **Arquivo novo:** `src/Discovery.Api/DependencyInjection/HealthChecksServiceCollectionExtensions.cs`
- **Checks:** PostgreSQL (via DbContext), Redis, NATS
- **Endpoint:** `/health`
- **Status:** ✅ Concluído

### 5. Remover `IAgentMessaging` dos Repositórios
- **Arquivos:** `TicketRepository.cs`, `AutomationExecutionReportRepository.cs`, `LogRepository.cs`
- **Problema:** Repositórios publicam eventos de dashboard (`PublishDashboardEventAsync`) após writes — viola separação de camadas.
- **Solução proposta:** Mover para Domain Events via EF Core `SaveChangesInterceptor` ou publicar nos serviços chamadores.
- **Status:** ⏳ Pendente (requer refatoração de alto esforço; documentado como débito técnico)

### 6. Separar `DiscoveryDbContext` em Bounded Contexts
- **Arquivo original:** `src/Discovery.Infrastructure/Data/DiscoveryDbContext.cs` (3366→2684 linhas)
- **Ação:** Usar partial classes para organizar `OnModelCreating` por domínio.

| # | Partial file | Responsabilidade | ~Linhas | Status |
|---|-------------|-----------------|---------|--------|
| 6.1 | `DiscoveryDbContext.cs` | Base: todos os DbSets, orquestração OnModelCreating | 2684 | ✅ Concluído |
| 6.2 | `DiscoveryDbContext.Identity.cs` | Users, Groups, Roles, Permissions, MFA, Sessions, API tokens | ~200 | ✅ Concluído |
| 6.3 | `DiscoveryDbContext.P2pAndAlerts.cs` | P2P, Auto-Ticket, Monitoring, Agent Alerts, Ticket escalation/remote/sessions, SLA calendars | ~270 | ✅ Concluído |
| 6.4 | `DiscoveryDbContext.Infrastructure.cs` | Global DateTime UTC converter | ~30 | ✅ Concluído |

**Nota:** Extração completa dos ~30 blocos inline do `OnModelCreating` para partials individuais exigiria editar ~2000 linhas — risco alto de quebra. A abordagem adotada foi extrair os 3 métodos já isolados (`ConfigureIdentity`, `ConfigureP2pEntities`, `ConfigureDateTimeConversion`), servindo como prova de conceito. As configurações inline restantes podem ser migradas incrementalmente.

### 7. Aumentar cobertura de testes
- **Situação atual:** ~28 arquivos de teste para +190 classes de produção.

| # | Tipo de teste | Status |
|---|--------------|--------|
| 7.1 | Testes de Controller (WebApplicationFactory) | ⏳ Pendente |
| 7.2 | Testes de integração SignalR Hubs | ⏳ Pendente |
| 7.3 | Testes de BackgroundService | ⏳ Pendente |
| 7.4 | Testes de repositórios com PostgreSQL real | ⏳ Pendente |

---

## 🟡 Prioridade Baixa (Desejável)

### 8. Padronizar idioma dos comentários para inglês
- **Status:** ✅ Parcial — novos partials (`AgentAuth/`, `Agents/`) já estão 100% em inglês. Código legado permanece misto (débito técnico contínuo).

### 9. Adicionar Output Caching
- **Arquivo novo:** `src/Discovery.Api/DependencyInjection/OutputCacheServiceCollectionExtensions.cs`
- **Ação:** Configurar `AddOutputCache()` com políticas Short/Medium/Long.
- **Status:** ✅ Concluído

### 10. Avaliar Quartz.NET para background jobs complexos
- **ADR:** `docs/ADR_BACKGROUND_JOBS.md`
- **Status:** ✅ Implementado (Fase 1+2) — 4 jobs migrados: LogPurge, ReportRetention, AiChatRetention, P2pMaintenance.
- **Próximos:** Fase 3 (AlertScheduler, SlaMonitoring, Reconciliations) quando necessário.

### 11. Adicionar versionamento de API
- **ADR:** `docs/ADR_API_VERSIONING.md`
- **Estratégia:** URL Path (`/api/v1/`) com `Asp.Versioning.Mvc` + redirect 301 de `/api/*` → `/api/v1/*`
- **Implementação:** 
  - `ApiVersioningServiceCollectionExtensions.cs` — configura `UrlSegmentApiVersionReader`, v1 default
  - 50+ rotas de controllers atualizadas para `api/v{version:apiVersion}/...`
  - Middleware de redirect 301 no pipeline para backward compatibility
- **Status:** ✅ Implementado

### 12. Adicionar `ConfigureAwait(false)` onde aplicável
- **Conclusão:** **Não necessário** — ASP.NET Core não usa `SynchronizationContext`, portanto `ConfigureAwait(false)` é redundante. Nenhum risco de deadlock no modelo atual.
- **Referência:** [Stephen Cleary — Don't Block on Async Code](https://blog.stephencleary.com/2012/07/dont-block-on-async-code.html), [ASP.NET Core SynchronizationContext](https://devblogs.microsoft.com/dotnet/configureawait-faq/#asp-net-core)
- **Status:** ✅ Resolvido (não aplicável)

---

## 📊 Progresso Geral

| Prioridade | Total | Concluído | Pendente |
|-----------|-------|-----------|----------|
| 🔴 Alta | 3 | 3 | 0 |
| 🟠 Média | 4 | 3 | 1 (testes) |
| 🟡 Baixa | 5 | 5 | 0 |
| **Total** | **12** | **11** | **1** |

---

> **Nota:** Atualizar este documento conforme os itens forem sendo implementados.
