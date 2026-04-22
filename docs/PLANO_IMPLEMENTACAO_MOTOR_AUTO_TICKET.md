# Motor Auto-Ticket por Alertas — Plano Consolidado (rev. 2026-04-17)

## Objetivo

Implementar um motor declarativo que cria chamados automaticamente a partir de **eventos de monitoramento** (métricas de automação, health checks de agent), com deduplicação robusta para evitar tickets repetidos do mesmo tipo de alerta no mesmo agente/janela de tempo.

---

## Contexto do codebase (estado atual verificado)

### O que já existe e será reusado

| Artefato | Localização | O que faz | Limitação relevante |
|---|---|---|---|
| `AgentAlertDefinition` | `Discovery.Core/Entities` | Alerta de notificação PSADT (Toast/Modal para endpoint do usuário). Já possui `TicketId` e escopo (Agent/Site/Client/Label). Migration M095. | **É notificação UI, não evento de monitoramento.** Não possui `AlertCode`, `Severity`, `MetricValue`. |
| `AlertToTicketService` | `Discovery.Infrastructure/Services` | Cria ticket a partir de `AgentAlertDefinition`. Dedup básico (verifica `alert.TicketId`). | Sem suporte a: prioridade/departamento/categoria dinâmicos por label, janela de tempo, dedup transacional. |
| `AgentAlertsController` | `Discovery.Api/Controllers` | CRUD + dispatch de alertas PSADT. Já injeta `IAlertToTicketService`. | Ponto de integração futuro; não confundir com ingestão de eventos de monitoramento. |
| `AlertDispatchService` | `Discovery.Api/Services` | Despacha notificações PSADT para agents via NATS/SignalR. | **Não faz parte do pipeline do motor**; é para entrega de UI alerts. |
| `TicketAlertRules` | M096 | Regras que disparam notificações PSADT ao mudar estado do workflow. | **Conceito diferente** de `AutoTicketRule`; não confundir. |
| `AgentAutoLabelingService` | `Discovery.Infrastructure/Services` | Auto-label de agents por inventário. | Fonte de labels para o motor de regras. |
| `AutomationTaskDefinition` / `AutomationExecutionReport` | `Discovery.Core` / `Discovery.Infrastructure` | Tarefas e resultados de automação com `MetadataJson` livre. | Sem avaliação de threshold ainda. Ponto de entrada para F2. |
| `IAlertToTicketService` | `Discovery.Core/Interfaces` | Interface do serviço acima. | Será chamada pelo orquestrador em F1.2. |

### Última migration existente: **M106** (SLA Calendars). Próxima: **M107**.

---

## Separação de conceitos (ponto crítico)

```
PSADT Notification (existente)          Monitoring Event (novo — motor)
───────────────────────────────         ────────────────────────────────
AgentAlertDefinition                    AgentMonitoringEvent (novo)
AlertDispatchService                    AutoTicketOrchestratorService (novo)
TicketAlertRules                        AutoTicketRule (novo)
Fluxo: admin cria alerta →              Fluxo: automação/health → evento →
       agent recebe UI dialog           motor → dedup → ticket (se regra)
```

O motor de auto-ticket **não altera** o pipeline de notificações PSADT existente. Ambos podem coexistir e são ortogonais.

---

## Arquitetura alvo (incremental)

### 1. Ingestão de evento de monitoramento
- **Origens**: resultado de automação (`CompleteAutomationExecution` em `AgentAuthController`), health-check periódico do agent, endpoint dedicado `POST /api/monitoring-events`.
- Normalizar em `AgentMonitoringEvent`: `clientId`, `siteId`, `agentId`, `alertCode`, `severity`, `metricKey`, `metricValue`, `payloadJson`, `labelsSnapshot`, `occurredAt`.

### 2. Motor de regras (`AutoTicketRuleEngineService`)
- Avalia políticas declarativas `AutoTicketRule` em ordem de precedência.
- Escopo: Global → Client → Site.
- Match por: `AlertCode`, `Severity` range, Labels Any/All/Exclude, `PayloadPredicateJson`.
- Retorna `RuleDecision` (regra vencedora + ação + parâmetros de ticket).

### 3. Motor de deduplicação (`AutoTicketDedupService`)
- `DedupKey = clientId:agentId:alertCode:sha256(normalizedPayload):timeBucket`
- `timeBucket` = `floor(occurredAt / dedupWindowMinutes)` — determinístico, sem campos voláteis.
- Lock por índice único em `AlertCorrelationLock`.
- Operação transacional: lock → criar ticket → gravar execução.

### 4. Executor
- Chama `IAlertToTicketService.CreateTicketFromAlertAsync(...)` com overrides de prioridade/categoria/departamento vindos da regra.
- Persiste `AutoTicketRuleExecution` em todos os caminhos.

### 5. Observabilidade
- Métricas OTEL, logs estruturados, trilha de auditoria por evento avaliado.

---

## Modelo de dados — novas tabelas (M107–M109)

### M107 — `auto_ticket_rules` + `auto_ticket_rule_executions` + `alert_correlation_locks`

**`auto_ticket_rules`**
```
id                       uuid PK
name                     varchar(200) NOT NULL
is_enabled               boolean NOT NULL DEFAULT true
priority_order           int NOT NULL DEFAULT 0
scope_level              int NOT NULL  -- 0=Global, 1=Client, 2=Site
scope_id                 uuid NULLABLE
alert_code_filter        varchar(200) NULLABLE  -- ex: "disk.full", "cpu.high"
alert_type_filter        int NULLABLE
severity_min             int NULLABLE
severity_max             int NULLABLE
match_labels_any_json    jsonb NULLABLE
match_labels_all_json    jsonb NULLABLE
exclude_labels_json      jsonb NULLABLE
payload_predicate_json   jsonb NULLABLE
action                   int NOT NULL  -- 0=AlertOnly, 1=CreateTicket, 2=Suppress
target_department_id     uuid NULLABLE
target_workflow_profile_id uuid NULLABLE
target_category          varchar(100) NULLABLE
target_priority          int NULLABLE
dedup_window_minutes     int NOT NULL DEFAULT 60
cooldown_minutes         int NOT NULL DEFAULT 0
created_at               timestamptz NOT NULL DEFAULT now()
updated_at               timestamptz NOT NULL DEFAULT now()
```
Índices: `ix_auto_ticket_rules_scope_enabled (scope_level, is_enabled)`.

**`auto_ticket_rule_executions`**
```
id                   uuid PK
rule_id              uuid NULLABLE FK → auto_ticket_rules
monitoring_event_id  uuid NOT NULL FK → agent_monitoring_events
agent_id             uuid NULLABLE
evaluated_at         timestamptz NOT NULL
decision             int NOT NULL  -- 0=MatchedNoAction, 1=Suppressed, 2=Deduped, 3=Created, 4=Failed, 5=RateLimited
reason               varchar(500) NULLABLE
created_ticket_id    uuid NULLABLE
dedup_key            varchar(500) NULLABLE
dedup_hit            boolean NOT NULL DEFAULT false
payload_snapshot_json jsonb NULLABLE
```
Índices: `ix_auto_ticket_rule_executions_event_id`, `ix_auto_ticket_rule_executions_agent_evaluated`.

**`alert_correlation_locks`**
```
dedup_key    varchar(500) PK (UNIQUE)
expires_at   timestamptz NOT NULL
last_ticket_id uuid NULLABLE
last_alert_at  timestamptz NOT NULL
```
Índice único em `dedup_key`.

### M108 — `agent_monitoring_events`

```
id              uuid PK
client_id       uuid NOT NULL
site_id         uuid NULLABLE
agent_id        uuid NOT NULL
alert_code      varchar(200) NOT NULL   -- ex: "disk.full", "cpu.high"
severity        int NOT NULL            -- 0=Attention, 1=Warning, 2=Critical
metric_key      varchar(200) NULLABLE
metric_value    decimal NULLABLE
payload_json    jsonb NULLABLE
labels_snapshot jsonb NULLABLE          -- snapshot dos labels no momento do evento
source          int NOT NULL            -- 0=Automation, 1=HealthCheck, 2=Manual
source_ref_id   uuid NULLABLE           -- ex: AutomationExecutionReport.Id
occurred_at     timestamptz NOT NULL
created_at      timestamptz NOT NULL DEFAULT now()
```
Índices: `ix_monitoring_events_agent_code_occurred`, `ix_monitoring_events_client_occurred`.

### M109 — `automation_alert_templates` + `automation_alert_threshold_profiles` (F2)

**`automation_alert_templates`**
```
id                  uuid PK
name                varchar(200) NOT NULL
alert_code          varchar(200) NOT NULL UNIQUE
default_severity    int NOT NULL DEFAULT 1
message_template    varchar(2000) NOT NULL
payload_schema_json jsonb NULLABLE
created_at          timestamptz NOT NULL DEFAULT now()
```

**`automation_alert_threshold_profiles`**
```
id                      uuid PK
template_id             uuid NOT NULL FK → automation_alert_templates
metric_key              varchar(200) NOT NULL
comparator              int NOT NULL  -- 0=GreaterThan, 1=LessThan, 2=Equals
attention_threshold     decimal NULLABLE
warning_threshold       decimal NULLABLE
critical_threshold      decimal NULLABLE
hysteresis_window_min   int NOT NULL DEFAULT 0
created_at              timestamptz NOT NULL DEFAULT now()
```

---

## Algoritmo de deduplicação

```
DedupKey = "{clientId}:{agentId}:{alertCode}:{sha256(normalizedPayload)}:{timeBucket}"

normalizedPayload:
  - Serializar payloadJson com chaves ordenadas
  - Remover campos voláteis: timestamp, nonce, requestId, contadores transitórios
  - Resultado determinístico para mesmo evento semântico

timeBucket = floor(occurredAt.UnixSeconds / (dedupWindowMinutes * 60))
```

### Fluxo do orquestrador

```
1. Recebe AgentMonitoringEvent
2. Carrega labels do agent (snapshot)
3. RuleEngine.Evaluate(event, labels) → RuleDecision
   a. Se nenhuma regra → Decision=MatchedNoAction, persiste execution, return
   b. Se Suppress → Decision=Suppressed, persiste, return
4. BuildDedupKey(event, rule)
5. DedupService.TryAcquireOrGet(dedupKey, dedupWindow)
   a. Lock existente e não expirado → Decision=Deduped, persiste, return
   b. Lock não existe ou expirado → adquirir lock
6. Se ShadowMode → Decision=MatchedNoAction (sem criar ticket), persiste, return
7. AlertToTicketService.CreateTicketFromAlertAsync(..., priority, category, department)
8. DedupService.RegisterCreatedTicket(dedupKey, ticketId)
9. Persistir execution com Decision=Created
```

### Controle de concorrência

- `alert_correlation_locks.dedup_key` tem índice único no banco.
- Em caso de violação de unicidade (race condition): capturar `DbUpdateException`, re-ler lock existente → retornar Decision=Deduped.
- Sem retry infinito; falha transacional resulta em Decision=Failed com log de erro.

---

## Precedência de regras

1. **Ação=Suppress** tem prioridade absoluta sobre qualquer CreateTicket.
2. **Escopo mais específico** ganha: Site > Client > Global.
3. **PriorityOrder DESC** (maior número = maior prioridade dentro do mesmo escopo).
4. **SeverityMax DESC** como desempate final.
5. Regras desabilitadas (`IsEnabled=false`) são ignoradas.

---

## Exemplos de regras por label

| AlertCode | Label | Action | Priority | Department | Category |
|---|---|---|---|---|---|
| `disk.full` | `servidor` | CreateTicket | Critical | Infra | Capacity |
| `disk.full` | `pc-comum` | CreateTicket | Low | ServiceDesk | Endpoint |
| `cpu.high` | `servidor` | CreateTicket | High | Infra | Performance |
| `cpu.high` | `pc-comum` | AlertOnly | — | — | — |

---

## APIs

### `/api/auto-ticket-rules`
- `GET /` — lista com filtros (scope, enabled, alertCode)
- `POST /` — cria regra
- `PUT /{id}` — atualiza regra
- `DELETE /{id}` — remove regra
- `PATCH /{id}/enable` e `PATCH /{id}/disable`
- `POST /{id}/dry-run` — recebe sample de evento+labels, retorna decision sem persistir
- `GET /{id}/stats?hours=24` — indicadores operacionais da regra (seleção, created, deduped, failed, rate-limited)

### `/api/monitoring-events`
- `POST /` — ingestão manual/externa de evento
- `GET /{id}/auto-ticket-decisions` — histórico de execuções do orquestrador para o evento

### `/api/automation-alert-templates` (F2)
- CRUD completo
- `GET /{id}/thresholds` / `POST /{id}/thresholds` / `PUT /{id}/thresholds/{tid}` / `DELETE /{id}/thresholds/{tid}`

---

## Plano de implementação por fase

### F0 — Fundação segura (1 sprint)

#### F0.1 — Domínio, repositórios e migrations [BLOQUEANTE]
- Criar entidades `AutoTicketRule`, `AutoTicketRuleExecution`, `AlertCorrelationLock`, `AgentMonitoringEvent` em `Discovery.Core/Entities`.
- Criar interfaces `IAutoTicketRuleRepository`, `IAutoTicketRuleExecutionRepository`, `IAlertCorrelationLockRepository`, `IAgentMonitoringEventRepository` em `Discovery.Core/Interfaces`.
- Criar repositórios EF correspondentes em `Discovery.Infrastructure/Repositories`.
- Criar `M107_CreateAutoTicketTables.cs` (rules + executions + locks).
- Criar `M108_CreateAgentMonitoringEvents.cs`.
- **Critério de aceite**: `dotnet build` sem erro; migrations Up/Down sem erro.

#### F0.2 — Normalização e fingerprint [depende de F0.1]
- `MonitoringEventNormalizationService`: payload normalizado (chaves ordenadas, campos voláteis removidos).
- `DedupFingerprintService.BuildDedupKey(event, rule)`: implementação determinística.
- **Critério de aceite**: mesmo evento semântico → mesma chave; eventos distintos → chaves distintas (testes unitários).

#### F0.3 — Engine de regras [depende de F0.1]
- `AutoTicketRuleEngineService.EvaluateAsync(event, labelsSnapshot)`.
- Carregamento de regras por escopo com cache de curta duração (30s).
- Match por AlertCode, Severity, Labels, PayloadPredicate.
- Aplicação de precedência conforme especificação acima.
- **Critério de aceite**: cobertura de testes unitários dos cenários de precedência.

#### F0.4 — Dedup lock transacional [depende de F0.1 e F0.2]
- `AutoTicketDedupService.TryAcquireOrGetAsync(dedupKey, window)`.
- `AutoTicketDedupService.RegisterCreatedTicketAsync(dedupKey, ticketId)`.
- Tratamento de race condition por índice único.
- **Critério de aceite**: teste de concorrência — 2 requests simultâneos com mesma chave geram 1 ticket.

#### F0.5 — Orquestrador [depende de F0.3 e F0.4]
- `AutoTicketOrchestratorService.EvaluateAsync(monitoringEvent)`.
- Persiste `AutoTicketRuleExecution` em todos os caminhos (Created/Deduped/Suppressed/MatchedNoAction/Failed).
- Em shadow mode: avalia e loga, **não cria ticket**.
- **Critério de aceite**: trilha de auditoria completa por evento avaliado.

#### F0.6 — API de gestão e dry-run [paralelo com F0.5, depende de F0.1 e F0.3]
- `AutoTicketRulesController` com CRUD + dry-run.
- Endpoint `POST /api/monitoring-events/{id}/evaluate` (avaliação manual).
- **Critério de aceite**: Swagger atualizado, endpoints funcionais.

#### F0.7 — Integração no fluxo de automação [depende de F0.5]
- Hook em `AgentAuthController.CompleteAutomationExecution`: após persistir `AutomationExecutionReport`, publicar `AgentMonitoringEvent` se `MetadataJson` contiver campos de métrica.
- Por padrão: `ShadowMode=true` (sem abrir ticket).
- **Critério de aceite**: decisões registradas sem alterar comportamento atual de automações.

#### F0.8 — Observabilidade e feature flags [paralelo com F0.7]
- Configurações em `appsettings.json`:
  - `AutoTicket:Enabled` (bool)
  - `AutoTicket:ShadowMode` (bool)
  - `AutoTicket:ReopenWindowMinutes` (int, `0` = desabilitado)
  - `AutoTicket:MaxCreatedTicketsPerHourPerAlertCode` (int, `0` = desabilitado)
  - `AutoTicket:CanaryClientIds` (string[])
  - `AutoTicket:CanarySiteIds` (string[])
- Métricas OTEL (já configurado no projeto via `OpenTelemetry`):
  - `auto_ticket_evaluated_total` (counter, tags: decision, alertCode)
  - `auto_ticket_created_total` (counter, tags: alertCode, ruleId)
  - `auto_ticket_deduped_total` (counter)
  - `auto_ticket_failed_total` (counter)
  - `auto_ticket_rate_limited_total` (counter)
  - `auto_ticket_eval_duration_ms` (histogram)
- Logs estruturados com: `monitoringEventId`, `dedupKey`, `ruleId`, `decision`.
- **Critério de aceite**: métricas visíveis no endpoint OTEL; logs com correlação.

---

### F1 — Auto-ticket MVP por alerta + label (1 sprint)

#### F1.1 — Ativação controlada com regras seed
- Migration de seed (ou endpoint de admin) com regras iniciais:
  - `disk.full` + label `servidor` → Critical / Infra / Capacity
  - `disk.full` + label `pc-comum` → Low / ServiceDesk / Endpoint
- Ativar `ShadowMode=false` apenas para `CanaryClientIds` configurados.
- **Critério de aceite**: criação automática ocorre somente no canary; shadow continua nos demais.

#### F1.2 — Criação de ticket real [depende de F0.4 e F0.5]
- Orquestrador chama `IAlertToTicketService.CreateTicketFromAlertAsync` com `priority`, `category` e `departmentId` da regra vencedora.
  - **Nota**: `AlertToTicketService` já existe. Será necessário adicionar overload ou parâmetro `AutoTicketOverrides` para suportar roteamento dinâmico sem quebrar o contrato atual.
- Registra `ticketId` no lock e na execution.
- Fallback: falha na criação → Decision=Failed, não interrompe o pipeline.
- **Critério de aceite**: alertas elegíveis criam ticket único por chave/janela.

#### F1.3 — Testes obrigatórios para go-live
- Unitários: engine de regras, fingerprint, dedup, precedência.
- Integração: evento → regra → dedup → ticket criado; duplicado → sem novo ticket; label diferente → prioridade diferente.
- Concorrência: dois requests simultâneos com mesma chave.
- Regressão: endpoint manual `POST /api/agent-alerts/{id}/create-ticket` continua operacional.
- **Critério de aceite**: suite verde no pipeline CI.

---

### F2 — Automações gerando alertas customizáveis (1–2 sprints)

#### F2.1 — Templates e thresholds
- Criar `AutomationAlertTemplate` e `AutomationAlertThresholdProfile` (migration M109).
- `AutomationThresholdEvaluatorService.Evaluate(metricKey, metricValue, templateId)` → Severity.
- **Critério de aceite**: dado valor de métrica e perfil, retorna severity correto com histerese.

#### F2.2 — Integração output de automação → evento de monitoramento
- Em `CompleteAutomationExecution`: se `MetadataJson` contém `metric_key` + `metric_value` + `alert_code`, chamar `ThresholdEvaluator` → emitir `AgentMonitoringEvent` → `OrchestratorService.EvaluateAsync`.
- **Critério de aceite**: automação com output de métrica acima do threshold Critical cria ticket (em canary).

---

### F3 — Hardening e escala (1 sprint)

- Storm protection: cap de tickets por hora por `dedupKey`, rate-limit por `clientId:alertCode`.
- Antes de criar ticket novo, verificar se já existe **ticket AutoTicket aberto** para o mesmo `agentId + alertCode` e mesmo roteamento (`departmentId`, `workflowProfileId`, `category`); se existir, reutilizar o vínculo e registrar execução como deduplicada.
- Reopen policy: se ticket fechado e dentro de `ReopenWindowMinutes`, reabrir em vez de criar novo. A janela deve ser configurável por `AutoTicket:ReopenWindowMinutes`.
- Dashboard operacional: `GET /api/auto-ticket-rules/{id}/stats` (match rate, dedup rate, created, failed, rate-limited).
- Tune de thresholds por client/site via override de `AutomationAlertThresholdProfile`.

---

## Rollout seguro

| Estágio | Configuração | Duração sugerida |
|---|---|---|
| Shadow | `Enabled=true`, `ShadowMode=true` | 2–3 semanas |
| Canary | `ShadowMode=false`, `CanaryClientIds=[...]` | 1–2 semanas |
| Full | `CanaryClientIds=[]` (todos) | Permanente |
| Kill switch | `Enabled=false` | Emergência |

---

## Riscos e mitigações

| Risco | Mitigação |
|---|---|
| Falso positivo alto | dry-run obrigatório + canary + tuning por regra |
| Ticket storm | dedup lock + cooldown + cap por hora por dedupKey |
| Classificação errada por label inconsistente | fallback para defaults do workflowProfile + auditoria de match |
| Race condition ao criar ticket | índice único em `dedup_key` + tratamento de `DbUpdateException` |
| Quebra do pipeline de notificação PSADT | motor completamente separado; não altera `AgentAlertDefinition` |
| `AlertToTicketService` sem suporte a overrides | adicionar parâmetro `AutoTicketOverrides?` opcional em F1.2 |

---

## Dependências e paralelismo de implementação

```
F0.1 (migrations + domínio)
  ├── F0.2 (fingerprint)
  │     └── F0.4 (dedup lock) ──────────────────────────┐
  ├── F0.3 (engine de regras) ──────────────────────────┤
  │                                                      └── F0.5 (orquestrador)
  └── F0.6 (API CRUD) [paralelo com F0.5]                      ├── F0.7 (integração automação)
                                                               └── F0.8 (observabilidade) [paralelo com F0.7]
                                                                     ↓
                                                               F1.1 → F1.2 → F1.3
                                                                              ↓
                                                                         F2.1 → F2.2
                                                                                   ↓
                                                                              F3 (hardening)
```

---

## Checklist de aprovação antes de iniciar código

- [ ] Confirmar nomenclatura das tabelas (`agent_monitoring_events`, `auto_ticket_rules`, `auto_ticket_rule_executions`, `alert_correlation_locks`)
- [ ] Confirmar que `AgentAlertDefinition` **não** é alterada (motor usa `AgentMonitoringEvent` separado)
- [ ] Aprovar política padrão de duplicado: vincular ao ticket existente sem criar novo
- [ ] Aprovar regras seed iniciais: `disk.full` + `servidor` / `pc-comum`
- [ ] Aprovar estratégia de feature flags: `AutoTicket:Enabled` + `AutoTicket:ShadowMode` + `AutoTicket:CanaryClientIds`
- [ ] Confirmar que `AlertToTicketService` receberá overload de overrides (não quebra contrato atual)