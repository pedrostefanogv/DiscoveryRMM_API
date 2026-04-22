Plan: Avaliação Geral e Melhorias do Projeto Discovery
TL;DR. Projeto maduro (~40 controllers, ~60 services, Api/Core/Infrastructure/Migrations/Tests com DI auto-registrada, NATS, EF Core + pgvector, MeshCentral). Encontrei 10 eixos de melhoria agrupados em 3 fases: correções rápidas de qualidade → fechar backlogs já documentados → novas capacidades transversais. A maior parte dos ganhos iniciais vem de eliminar anti-padrões sync-over-async, cachear a factory de object storage e fragmentar controllers/serviços grandes.

Fase A — Correções rápidas (dívida imediata)
A1. Eliminar sync-over-async (risco real de deadlock/starvation):

ObjectStorageProviderFactory.cs:39 — duplo .Result em factory scoped
ConfigurationResolver.cs:281 — GetAwaiter().GetResult() em Redis
LoggingActionFilter.cs:118 — bloqueia em todo action
AgentLabelsController.cs:224 — em validação
A2. Cachear ObjectStorageProviderFactory — hoje vai ao banco a cada resolução scoped; usar memoization invalidada por ISyncInvalidationPublisher.

A3. Fragmentar God files preservando rotas: AgentAuthController.cs (~1476 linhas), ReportsController.cs, P2pService.cs (extrair P2pQueryBuilder — switches de scope repetidos 4x).

A4. Concluir remoção de flags legadas (F4.2 já removeu NatsIncludeLegacySubjects; residuais em docs/testes).

Fase B — Fechar backlogs existentes
B1. NATS hardening (F4.1/F4.3/F4.5) — migrar command/heartbeat/result/hardware/sync.ping para subjects canônicos tenant.*.site.*.agent.*; expandir NatsIsolationTests para cross-tenant nos 5 subjects.

B2. Object Storage multi-vendor (F5.2/F5.6) — matriz validada MinIO/AWS/R2/Oracle e data de corte do download-stream marcado [Obsolete] em ReportsController.cs.

B3. Agent Update Fases 2–3 — ainda totalmente pendentes: agent consulta manifest, valida sha256, reporta via me/update/report, backoff; endpoints admin de promoção (beta→stable) e dashboard de rollout.

B4. Custom Fields Fases 4–8 — CRUD admin, endpoints runtime no agent (allowAgentWrite), integração com AutomationTaskService via allowlist por task/script, auditoria, rate limit, testes (inclui E2E TeamViewer ID).

Fase C — Capacidades novas e melhorias transversais
C1. Observabilidade — adicionar OpenTelemetry (EF Core, HttpClient, NATS, SignalR, background services) + endpoints /health/live e /health/ready (DB/Redis/NATS/Storage). Hoje há ILogger e métricas P2P internas, mas sem tracing/metrics padronizados.

C2. Background processing — TODO em AiChatService.cs:451 (fila AI); ReportService.ProcessPendingAsync hoje é sequencial — paralelizar com MaxDegreeOfParallelism de ReportingOptions. Reutilizar padrão SyncPingDispatchBackgroundService.

C3. Security hardening — auditar appsettings*.json (migrar segredos p/ User Secrets/KeyVault), rotação de ApiToken/AgentToken, validar SecurityHeadersMiddleware (CSP/HSTS), escopo de rate limiting por endpoint crítico (login/mfa/agent-register), persistência de keys/ (Data Protection) fora do container.

C4. Cobertura de testes — 23 arquivos de teste para ~100 unidades. Priorizar AutomationTaskServiceTests, P2pServiceTests, ReportServiceTests, ConfigurationResolverTests, MeshCentralApiServiceTests + integration tests via WebApplicationFactory para os controllers críticos.

C5. ADRs e doc hygiene — registrar decisões (NATS canônico, S3 único, Playwright PDF, pgvector, DI auto-registro) via manage_adr do MCP codebase-memo; consolidar roadmaps dispersos nos capability docs.

Verificação (resumo)

dotnet build && dotnet test passa; grep de .Result/.GetAwaiter().GetResult zerado (exceto Wait(TimeSpan.Zero) de semáforos).
Cobertura ≥60% em Discovery.Infrastructure.Services.
NatsIsolationTests expandido para 5 subjects × cross-tenant.
Endpoints /metrics e /health/* respondendo; traces OTLP exportados.
Smoke test E2E do rollout de update (api-step2).
Escopo

Inclui: backend (src), docs, testes.
Exclui: frontend (repo externo), infra de deploy (só ajustes pontuais).
Assunção: Postgres (SQLite já descontinuado em Program.cs); instalador agent permanece Windows-only.
Further Considerations

Sequenciamento: executar Fase A inteira antes de B (recomendado — evita regressão em código a ser fatorado) ou intercalar A+B por domínio (ex.: NATS A+B1 juntos)?
Vendor OpenTelemetry: OTLP genérico (recomendado) / Prometheus+Jaeger self-hosted / SaaS (Datadog/NewRelic)?
Custom Fields runtime: estender AgentAuthController (reuso de token) ou criar AgentRuntimeController dedicado (separação de responsabilidades)?
Plano salvo em /memories/session/plan.md. Indique ajustes ou aprove para handoff de implementação.