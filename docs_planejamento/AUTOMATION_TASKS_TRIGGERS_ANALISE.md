# Análise: Criação de Tarefas de Automação e Triggers — Relatório de Bugs e Plano de Melhoria

**Data:** 2026-08-31
**Escopo:** Fluxo de criação de Automation Tasks (UI → API → policy-sync) e execução no agent (Go), com foco nos triggers `TriggerImmediate`, `TriggerRecurring` (ScheduleCron), `TriggerOnUserLogin`, `TriggerOnAgentCheckIn`.

---

## 1. Visão geral do fluxo atual

```
UI (modal /automation/tasks)
  → POST /automation/tasks (CreateAutomationTaskCommand)
    → AutomationTaskService.CreateAsync (valida payload + triggers, persiste, audita)
Agent (a cada 5 min, ou 30s se offline)
  → POST /api/v1/agent-auth/me/automation/policy-sync (envia KnownPolicyFingerprint)
    → AutomationTaskService.SyncPolicyForAgentAsync
      → ResolveApplicablePolicyTasksAsync (escopo Global/Client/Site/Agent + filtro de tags)
      → BuildPolicyKey → SHA256 → compara fingerprint (UpToDate ⇒ Tasks=[])
Agent (reconcilePolicy)
  → triggerImmediate / triggerRecurring (cron local robfig/cron) / triggerOnUserLogin / triggerOnAgentCheckIn
  → executeTaskAsync → resultado em SQLite + callbacks HTTP ack/result (se CommandId != "")
```

**Arquivos-chave:**

- Servidor: `src/Discovery.Infrastructure/Services/AutomationTaskService.cs`, `src/Discovery.Infrastructure/Cqrs/AutomationTasks/AutomationTaskHandlers.cs`, `src/Discovery.Infrastructure/Cqrs/AgentAuth/Handlers/AgentAutomationHandlers.cs`, `src/Discovery.Core/DTOs/AutomationPolicySyncDtos.cs`
- Agent: `C:\Projetos\Discovery\src\app\core\automation\{service.go, service_helpers.go, client.go, types.go, executor.go}`

---

## 2. Pontos encontrados (bugs e fragilidades)

### 🔴 B1 — Resultados de execuções automáticas nunca chegam ao servidor (crítico)

O DTO do policy-sync (`AgentAutomationTaskPolicyDto`) **não possui `CommandId`**. No agent, ack/result só são enviados `if entry.CommandID != ""` (`service.go:579,630`). Como tasks disparadas por immediate/checkin/userlogin/recurring não têm `CommandId`, **nenhum ack nem resultado é reportado** — o servidor nunca sabe se a task rodou, falhou ou está pendente. Fica invisível no dashboard.

### 🔴 B2 — `TriggerOnUserLogin` é "fake"

Não há detecção real de login (nenhum `WTSRegisterSessionNotification`/evento de sessão no agent). O trigger dispara **uma vez por processo do agent**, no primeiro policy-sync após o boot (marcador `_sys:userlogin:handled:<processStartAt>`). Não reage a logins reais de usuário, nem diferencia usuários/sessões. Semântica atual ≠ semântica esperada pelo usuário na UI.

### 🟠 B3 — `TriggerOnAgentCheckIn` não dispara a cada check-in

O heartbeat (30s) é independente do automation. O trigger roda **uma vez por fingerprint de policy** (marcador `checkin:<fingerprint>:<taskId>`), ou seja, só no primeiro sync após a task aparecer. Se a expectativa é "executar a cada check-in do agent", não acontece.

> **✅ RESOLVIDO (2026-08-31):** semântica definida como "executar a cada **inventário completo** do agent (~6h)". Implementado no agent: novo método `TriggerAgentCheckInTasks(ctx)` (`service.go`), chamado ao final de `runPeriodicInventorySync` (`app.go`), com dedup por janela mínima de 1h via marcador SQLite `checkin:cycle:<taskId>`. O disparo original por fingerprint (1x ao aplicar policy) foi mantido no `reconcilePolicy`.

### 🟠 B4 — Cron não validada no servidor

`ValidateTask` (`AutomationTaskService.cs:639-645`) só exige que `ScheduleCron` não seja vazio quando `TriggerRecurring`. **Não valida sintaxe**. Cron inválida: o agent loga "cron invalido" e pula (`service.go:733-739`) — a task fica **ativa porém inoperante, silenciosamente**, sem feedback ao servidor/usuário. Além disso:

- O agent usa parser de **5 campos** (robfig/cron padrão) e **fuso horário local do processo** — a UI não informa isso ao usuário ao criar a task.
- O servidor usa Quartz (`CronExpression`, 7 campos com segundos) em `ReportScheduleDispatchJob` — **dois dialetos de cron diferentes** no mesmo produto, fonte de confusão.

### 🟠 B5 — Fingerprint de policy não reflete o conteúdo real

`BuildPolicyKey` (`AutomationTaskService.cs:716-730`) inclui apenas `Id | LastUpdatedAt | ActionType | ScriptId | PackageId`. **Exclui** triggers, `ScheduleCron`, tags, `CommandPayload`, `InstallationType`, `RequiresApproval`, `IsActive`. Hoje funciona "por acidente" porque todo update bumpa `LastUpdatedAt` no repository — mas é frágil: qualquer caminho de escrita que esqueça o bump deixa o agent com policy stale (o servidor responde `UpToDate=true` e o agent reusa a lista antiga).

### 🟡 B6 — `TriggerImmediate` não dispara nada no servidor na criação

Criar uma task com `TriggerImmediate` apenas persiste; a execução só ocorre no **próximo policy-sync do agent (até 5 min depois)**. Não há push imediato (NATS) nem job. UX: usuário clica "Criar" esperando execução imediata e nada visível acontece por minutos.

### 🟡 B7 — `RequiresApproval` tratado de forma inconsistente no agent

Tasks com approval não são agendadas no cron (correto), mas `triggerImmediate`/`checkin`/`userLogin` ainda chamam `executeTaskAsync`, que falha com exit code 10 no executor — gera registros de execução "falha" espúrios em vez de simplesmente não disparar.

### 🟡 B8 — Sem visibilidade de execução por task no servidor

Não existe tabela/endpoint de "execuções da task" agregando resultados do agent (depende do B1). O preview de targets existe (`PreviewTargetAgentsAsync`), mas não há "última execução / status por agent".

### 🟡 B9 — UI do modal de criação

- `ScheduleCron` fica **desabilitado** e não há validação/ajuda de sintaxe nem indicação de timezone/5-campos.
- Triggers são selects Sim/Não independentes, sem feedback de que **pelo menos um** é obrigatório (erro só aparece no submit, como exceção do servidor → provável 500 genérico em vez de 400 com mensagem amigável).
- `ScriptId` só é relevante para `RunScript`, `PackageId`/`InstallationType` para ações de pacote, `CommandPayload` para comando custom — o formulário não mostra/esconde campos conforme o `ActionType` selecionado.

### 🟡 B10 — Observações menores

- `ValidateTask` lança `InvalidOperationException` → provavelmente vira HTTP 500 em vez de 400 (verificar mapeamento no controller/handler).
- `ForceAutomationSync` reusa `CommandType.SystemInfo` com payload JSON embutido (hack frágil).
- `UpdateStatusAsync` do repositório de comandos sobrescreve status sem verificar estado atual (sem idempotência server-side para resultados duplicados).
- Tags são resolvidas só no servidor (correto), mas mudança de label de um agent não invalida fingerprint — aceitável, porém vale documentar.

---

## 3. Plano de correção e melhoria (proposto, para revisão)

### Fase 1 — Correções de bugs (prioridade alta)

| #   | Item                               | Ação                                                                                                                                                                                                                                                                  | Onde                        |
| --- | ---------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------- |
| 1   | **B1** — resultados não reportados | Incluir `CommandId` gerado pelo servidor no policy-sync (ou aceitar reporte por `TaskId+TriggerType+ExecutionId` do agent). Criar/ativar tabela `AutomationTaskExecution` e handlers de ack/result reais (hoje são stubs no-op em `AgentAutomationHandlers.cs:75-90`) | Servidor + agent            |
| 2   | **B4** — validar cron no servidor  | Validar `ScheduleCron` com parser de 5 campos (mesmo dialeto do agent, ex. `Cronos` ou `NCrontab`) em `ValidateTask`; retornar 400 com mensagem clara. Padronizar dialeto (5 campos, sem segundos) em todo o produto                                                  | `AutomationTaskService.cs`  |
| 3   | **B5** — fingerprint semântico     | Incluir em `BuildPolicyKey`: triggers, `ScheduleCron`, `IncludeTagsJson/ExcludeTagsJson`, `CommandPayload`, `InstallationType`, `RequiresApproval`, `IsActive` (manter `LastUpdatedAt` como garantia extra)                                                           | `AutomationTaskService.cs`  |
| 4   | **B7** — approval consistente      | No agent, não disparar `executeTaskAsync` para triggers immediate/checkin/userlogin quando `RequiresApproval` (pular com log, como já feito no cron)                                                                                                                  | `service.go`                |
| 5   | **B10** — erros 400 em vez de 500  | Mapear `InvalidOperationException` de validação para `Result`/400 nos handlers CQRS de create/update                                                                                                                                                                  | `AutomationTaskHandlers.cs` |

### Fase 2 — Melhorias de semântica dos triggers (média)

| #   | Item                        | Ação                                                                                                                                                                                                                                                     |
| --- | --------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 6   | **B2** — login real         | Implementar detecção de sessão no agent Windows (`WTSRegisterSessionNotification` em goroutine dedicada → canal que dispara tasks `TriggerOnUserLogin` por evento, com dedup por `sessionId+user+taskId+janela`). Manter fallback atual para não-Windows |
| 7   | **B3** — check-in real      | Decidir semântica: (a) renomear para "executar uma vez ao aplicar policy" ou (b) ligar ao heartbeat real (com dedup por janela mínima, ex. 1×/hora por agent) — **decisão de produto necessária**                                                        |
| 8   | **B6** — immediate com push | Ao criar/editar task com `TriggerImmediate` ativo, publicar comando NATS `force-sync` (tipo dedicado, não `SystemInfo`) para os agents do escopo → execução em segundos, não minutos                                                                     |
| 9   | **B8** — visibilidade       | Endpoint `GET /automation/tasks/{id}/executions` (paginação cursor) alimentado pela tabela do item 1; coluna "última execução/status" na listagem da UI                                                                                                  |

### Fase 3 — UX do modal de criação (média/baixa)

| #   | Item                         | Ação                                                                                                                                                 |
| --- | ---------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| 10  | **B9** — formulário dinâmico | Mostrar `ScriptId` só para RunScript; `PackageId/InstallationType` para ações de pacote; `CommandPayload` para comando custom                        |
| 11  | Triggers como checkboxes     | Substituir 4 selects Sim/Não por checkboxes com validação client-side ("selecione ao menos um")                                                      |
| 12  | Cron com ajuda               | Habilitar `ScheduleCron` quando `TriggerRecurring`; preview da próxima execução (parse client-side); indicar timezone do agent e formato de 5 campos |
| 13  | Feedback pós-criação         | Após criar com `TriggerImmediate`, indicar "execução será disparada nos agents do escopo"                                                            |

---

## 4. Riscos e observações para a revisão

- **Item 1 (B1)** é o de maior valor: sem ele, nenhuma execução automática é auditável pelo servidor. Requer migração (nova tabela) e mudança de contrato agent↔servidor — planejar versionamento do policy-sync.
- **Itens 6 e 7** mudam semântica de triggers existentes: tasks já criadas podem passar a executar com mais frequência (ex.: check-in real). Avaliar impacto com a base atual antes de ativar.
- **Item 3** muda o fingerprint → todos os agents farão sync completo na primeira rodada após deploy (picos de tráfego esperados; considerar rollout gradual ou janela).
- O agent Go vive em repositório separado (`DiscoveryRMM_Agent` / workspace `C:\Projetos\Discovery`) — as mudanças dos itens 1, 4, 6 precisam ser coordenadas entre os dois repos e releases.

---

## 5. Ordem de execução sugerida

1. Fase 1 itens 2, 3, 5 (servidor apenas, baixo risco, alto valor).
2. Fase 1 item 1 (contrato + migração — maior esforço, maior retorno).
3. Fase 2 item 8 (push imediato via NATS).
4. Fase 3 (UX do modal) em paralelo, sem dependência de backend.
5. Fase 2 itens 6–7 após decisão de produto sobre a semântica esperada.
