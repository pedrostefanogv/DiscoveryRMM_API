# Plano de Correção — Transferência de Agent entre Sites × Estado do Agent (NATS/Config)

**Data:** 2026-09-02
**Status:** Proposto
**Severidade:** Alta — comunicação servidor→agent quebra silenciosamente após transferência até expiração do JWT NATS

---

## 1. Contexto e Diagnóstico

### 1.1 Fluxo atual da transferência (`AgentTransferService.TransferAsync`)

1. `AgentRepository.TransferSiteAsync` atualiza **apenas** `SiteId` + `UpdatedAt` no banco.
2. Invalidação de caches Redis (listagens, agent individual, inventário).
3. Publica `SyncInvalidationPing` (Resource = `Configuration`) via `IAgentMessaging.PublishSyncPingAsync`.
4. Publica evento de dashboard `AgentTransferred`.

### 1.2 Como o agent descobre os novos IDs

- **Não há push dos novos IDs.** O agent só os obtém ao consultar `GET /api/v1/agent-auth/me/configuration`, que resolve `agent.SiteId` do banco (já correto imediatamente após a transferência).
- O sync ping é o mecanismo para avisá-lo de que deve re-sincronizar — mas ele **nunca chega** (ver 1.3).

### 1.3 Problemas identificados

| #      | Problema                                            | Detalhe                                                                                                                                                                                            | Impacto                                                                                                                                                                                       |
| ------ | --------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **P1** | **Sync ping publicado no subject errado**           | `PublishSyncPingAsync` → `BuildAgentSubjectAsync` resolve `agent.SiteId` **atual do banco** (site **novo**). O agent ainda está subscrito/autorizado (ACL do JWT) nos subjects do site **antigo**. | Ping perdido; agent não é notificado da mudança.                                                                                                                                              |
| **P2** | **Comandos servidor→agent quebrados até reconnect** | `SendCommandAsync` também resolve o subject pelo site atual. O agent não tem subscription/ACL de publish nos subjects do site novo.                                                                | Power actions, comandos remotos, fanout de site/cliente novo não alcançam o agent até o JWT NATS expirar (`NatsAgentJwtTtlMinutes`) e o auth callout reemitir credenciais com subjects novos. |
| **P3** | **Acesso remoto/terminal/arquivos quebrados**       | Subjects `tenant.{c}.site.{s}.agent.{id}.remote.session.>` do JWT antigo não correspondem aos subjects que o viewer/servidor usam (site novo).                                                     | _Permissions Violation_; sessões remotas falham até reconnect do agent.                                                                                                                       |
| **P4** | **Ping best-effort, sem persistência/retry**        | A transferência publica direto via `_messaging.PublishSyncPingAsync`, sem passar pelo `SyncPingDispatchBackgroundService`/`SyncPingDelivery` (debounce + registro de delivery).                    | Se o NATS estiver indisposto no momento, a notificação se perde sem rastro.                                                                                                                   |
| **P5** | **Sem auto-recuperação no agent**                   | O agent não compara `siteId`/`clientId` da config HTTP com os subjects do JWT NATS em uso.                                                                                                         | Janela de incompatibilidade = TTL do JWT (pode ser longo).                                                                                                                                    |

### 1.4 O que já funciona

- ✅ `GET /agent-auth/me/configuration` retorna os IDs novos imediatamente (resolve do banco).
- ✅ Auth callout (`NatsAuthCalloutBackgroundService` → `IssueUserJwtForAgentAsync`) resolve o site **atual** — o reconnect NATS corrige tudo sozinho.
- ✅ Infraestrutura de delivery confiável de ping já existe (`SyncPingDispatchBackgroundService`, `SyncPingDelivery`, repo M067).

---

## 2. Objetivo

Após uma transferência, o agent deve:

1. Ser **notificado de forma confiável** (via subject antigo, que ainda é válido para ele);
2. **Reconectar ao NATS imediatamente** (re-auth callout → JWT com subjects do site novo);
3. **Re-sincronizar a configuração** (IDs de site/cliente, policies de automação, feature flags);
4. Ter **auto-recuperação** caso a notificação se perca (divergência detectada pelo próprio agent).

---

## 3. Solução Proposta

### Estratégia central: **janela de transição com dual-subject + comando de reconnect**

Durante a transferência, o servidor publica no subject **antigo** (único que o agent consegue receber) um comando que o força a (a) re-buscar a config e (b) reconectar ao NATS. O reconnect via auth callout emite JWT com os subjects novos — fechando a janela em segundos, não em TTL.

### Fase 1 — Dual-publish de sync ping na transferência (Backend, ~2h)

**Arquivos:** `IAgentMessaging.cs`, `NatsAgentMessaging.cs`, `AgentTransferService.cs`

1. Adicionar overload em `IAgentMessaging`:

   ```csharp
   Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping,
       Guid? overrideClientId, Guid? overrideSiteId, CancellationToken ct = default);
   ```

   Em `NatsAgentMessaging`, quando `override*` é fornecido, construir o subject diretamente via `NatsSubjectBuilder.AgentSubject(overrideClientId, overrideSiteId, agentId, "sync.ping")` **sem consultar o banco**.

2. Em `AgentTransferService.TransferAsync`, publicar o ping **no subject do site antigo** (`previousSite.ClientId`, `previousSite.Id`) — capturados **antes** de `TransferSiteAsync`. Publicar também no subject novo (best-effort, cobre agents que já reconectaram).

3. Registrar o delivery via `ISyncPingDeliveryRepository.CreateSentAsync` (mesmo padrão do `SyncPingDispatchBackgroundService.DispatchPingAsync`) — resolve **P1** e **P4**.

### Fase 2 — Comando `nats.reconnect` no subject antigo (Backend, ~2h)

**Arquivos:** `AgentTransferService.cs`, `NatsAgentMessaging.cs` (novo método `SendCommandToSubjectAsync`)

1. Adicionar `SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload)` que publica direto no subject informado (sem resolver do banco).

2. Após o dual-publish do ping, enviar comando `nats.reconnect` (ou `config.reload` + `nats.reconnect` combinados) **no subject antigo**, com payload contendo os novos `siteId`/`clientId` — o agent então:
   - Re-busca `/agent-auth/me/configuration` (IDs novos);
   - Reconecta ao NATS → auth callout emite JWT com subjects do site novo.

3. Persistir o comando em `Commands` (fluxo normal de comando) para auditabilidade.

> **Nota:** o comando no subject antigo só chega se o agent estiver online. Agent offline reconecta naturalmente ao voltar (auth callout já emite subjects corretos) — sem ação extra necessária.

### Fase 3 — Auto-recuperação no agent (Agent-side, ~3h)

**Arquivo:** agent (repositório do agent — fora deste backend)

1. Ao receber config HTTP, comparar `siteId`/`clientId` com os subjects do JWT NATS atualmente em uso (ou com os IDs em memória).
2. Em divergência → reconectar NATS imediatamente + re-sincronizar policies de automação (`/automation/policy-sync`), app store e manifest de update.
3. Tratar o comando `nats.reconnect` (Fase 2) como gatilho explícito.

Isso resolve **P5** e dá defesa em profundidade: mesmo que o ping e o comando se percam, o próximo ciclo de sync (manifest recomenda 5 min para Configuration) detecta a divergência.

### Fase 4 — Telemetria e observabilidade (Backend, ~1h)

1. Log estruturado na transferência: `PreviousSubject`, `NewSubject`, `PingPublishedToOld`, `ReconnectCommandSent`.
2. Campo no `AgentTransferResult`/DTO de resposta indicando que o agent foi notificado (para o frontend exibir "Agent será atualizado em instantes").
3. Métrica/contador de `nats.reconnect` commands emitidos por transferência (facilita validar a Fase 3 em produção).

### Fase 5 — Testes (Backend, ~2h)

**Arquivo:** `Discovery.Tests`

1. Teste unitário: `PublishSyncPingAsync` com override usa o subject informado (não o do banco).
2. Teste unitário: `AgentTransferService` publica ping no subject antigo E novo, e envia comando `nats.reconnect` no subject antigo (usar fakes de `IAgentMessaging` existentes em `SitePowerCommandHandlersTests` como referência).
3. Teste de integração de subjects (estender `NatsIsolationTests`): após transferência, subjects emitidos pelo callout correspondem ao site novo.

---

## 4. Ordem de Execução e Esforço

| Fase                         | Escopo  | Esforço | Dependência                  |
| ---------------------------- | ------- | ------- | ---------------------------- |
| 1 — Dual-publish ping        | Backend | ~2h     | —                            |
| 2 — Comando `nats.reconnect` | Backend | ~2h     | Fase 1                       |
| 3 — Auto-recuperação agent   | Agent   | ~3h     | Fase 2 (contrato do comando) |
| 4 — Telemetria               | Backend | ~1h     | Fases 1–2                    |
| 5 — Testes                   | Backend | ~2h     | Fases 1–2                    |

**Total backend: ~7h.** Fases 1+2 podem ser implantadas antes da Fase 3 (o comando é ignorado por versões antigas do agent sem efeito colateral — payload é um comando desconhecido).

---

## 5. Riscos e Mitigações

| Risco                                                       | Mitigação                                                                                                                                    |
| ----------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| Agent antigo não reconhece `nats.reconnect`                 | Comando desconhecido é ignorado; sem crash. Versionar payload (`"version": 1`).                                                              |
| Subject antigo já expirou (JWT venceu exatamente na janela) | Best-effort; Fase 3 cobre via polling de config.                                                                                             |
| Transferência cross-client muda `clientId` também           | Dual-publish cobre ambos os níveis (subject inclui client).                                                                                  |
| Ping duplicado (antigo + novo)                              | Idempotente por natureza (agent apenas re-sincroniza); `EventId` distinto por publish.                                                       |
| Bulk transfer de N agents gera rajada                       | Reusar o debounce do `SyncPingDispatchBackgroundService` enfileirando via `ISyncPingDispatchQueue` com override de subject propagado no DTO. |

---

## 6. Fora de Escopo (decisões futuras)

- Migração de dados históricos (inventário, tickets, automações) entre sites/clientes — a transferência hoje não move dados associativos; apenas re-scopa o agent.
- Revogação forçada de sessão NATS antiga (`TryAcquireNatsSessionAsync` já rejeita conexão duplicada no próximo login, o que é suficiente com o reconnect proativo).
