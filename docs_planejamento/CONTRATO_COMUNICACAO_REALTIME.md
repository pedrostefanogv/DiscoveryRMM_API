# Contrato de Comunicacao em Tempo Real - DiscoveryRMM API

> Versao: 4.0.0 | Data: 2026-05-05
> Publico: Servidor (C# - C:\Projetos\DiscoveryRMM_API), Agent (Go - C:\Projetos\Discovery), Frontend (JS/TS - C:\Projetos\DiscoveryRMM_Site)
> Proposito: Especificacao canonica executavel. Todos os componentes DEVEM validar entrada e saida contra este documento.
> Status: periodo de transicao ate 2026-06-01. Durante a transicao, formato legado gera warning. Apos essa data, mensagem nao conforme eh rejeitada.

---

## 0. Changelog

| Versao | Data | Mudancas |
|---|---|---|
| 4.0.0 | 2026-05-05 | Contrato consolidado em NATS de ponta a ponta (nativo e WebSocket), remocao do fluxo de hub legado, secao dedicada de conexao browser via NATS WS, checklist e plano de acao revisados para canal unico por subjects. |
| 3.0.0 | 2026-05-05 | Consolidacao de envelope canonico, tabelas de equivalencia e plano de convergencia. |
| 2.0.0 | 2026-05-05 | Contrato executavel com validacao por componente e checklist. |
| 1.x | - | Versoes iniciais. |

---

## 1. Principios

| # | Regra |
|---|---|
| 0 | Single Source of Truth: este arquivo no repositorio da API e a unica especificacao valida. |
| 1 | NATS e o barramento unico de realtime para todos os componentes. |
| 2 | NATS nativo e obrigatorio para servidor, agent e servicos backend. |
| 3 | NATS via WebSocket e obrigatorio para browser/dashboard. |
| 4 | JSON camelCase em todos os payloads no wire. |
| 5 | Subjects NATS em minusculas, separados por ponto. |
| 6 | Eventos de dashboard devem trafegar apenas em `dashboard.events` (nativo ou WS). |
| 7 | Notificacoes ao agent chegam por `.command` com `commandType = "notification"`. |
| 8 | Mesmo DTO/shape em NATS nativo e NATS WS (sem duplicacao por transporte). |
| 9 | Campo de tempo padrao: `timestampUtc` em ISO-8601 UTC. |
| 10 | `eventType` em PascalCase e enum fechado. |
| 11 | Nomes de campo sao exatos (sem aliases em modo estrito). |
| 12 | Toda violacao de contrato gera log com prefixo `[CONTRACT_VIOLATION]`. |
| 13 | Cada componente valida na borda de entrada e de saida. |
| 14 | Nenhum componente pode criar canal paralelo fora do NATS para eventos de dashboard. |
| 15 | Durante a transicao, normalizacao temporaria e permitida com warning. |
| 16 | Apos 2026-06-01, modo estrito: mensagem nao conforme deve ser descartada. |

---

## 2. NATS - Contrato de Subjects

### Nomenclatura

```text
tenant.{clientId}.site.{siteId}.agent.{agentId}.{messageType}
tenant.{clientId}.site.{siteId}.p2p.discovery
tenant.{clientId}.site.{siteId}.dashboard.events
tenant.unscoped.dashboard.events
```

### 2.1 Heartbeat

| Propriedade | Valor |
|---|---|
| Subject | `tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat` |
| Direcao | Agent -> Servidor |
| DTO | `AgentHeartbeat` |

| Campo | Tipo C# | Obrigatorio |
|---|---|---|
| `agentId` | `Guid` | sim |
| `clientId` | `Guid?` | nao |
| `siteId` | `Guid?` | nao |
| `ipAddress` | `string?` | nao |
| `hostname` | `string?` | nao |
| `agentVersion` | `string?` | nao |
| `timestampUtc` | `DateTime?` | nao |
| `cpuPercent` | `double?` | nao |
| `memoryPercent` | `double?` | nao |
| `memoryTotalGb` | `double?` | nao |
| `memoryUsedGb` | `double?` | nao |
| `diskPercent` | `double?` | nao |
| `diskTotalGb` | `double?` | nao |
| `diskUsedGb` | `double?` | nao |
| `p2pPeers` | `int?` | nao |
| `uptimeSeconds` | `long?` | nao |
| `processCount` | `int?` | nao |

### 2.2 Comando

| Propriedade | Valor |
|---|---|
| Subject | `tenant.{clientId}.site.{siteId}.agent.{agentId}.command` |
| Direcao | Servidor -> Agent |

```json
{ "commandId": "Guid", "commandType": "string", "payload": "{\"command\":\"Get-Date\",\"timeoutSec\":30}" }
```

### 2.3 Resultado

| Propriedade | Valor |
|---|---|
| Subject | `tenant.{clientId}.site.{siteId}.agent.{agentId}.result` |
| Direcao | Agent -> Servidor |

```json
{ "commandId": "Guid", "exitCode": 0, "output": "string", "errorMessage": "string" }
```

### 2.4 Sync Ping

| Propriedade | Valor |
|---|---|
| Subject | `tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping` |
| Direcao | Servidor -> Agent |

| Campo | Tipo | Obrigatorio |
|---|---|---|
| `eventId` | `Guid` | sim |
| `agentId` | `Guid` | sim |
| `eventType` | `string` | sim (`"sync.invalidated"`) |
| `resource` | `string` | sim |
| `scopeType` | `string` | sim |
| `scopeId` | `Guid?` | nao |
| `installationType` | `string?` | nao |
| `revision` | `string` | sim |
| `reason` | `string?` | nao |
| `changedAtUtc` | `DateTime` | sim |
| `correlationId` | `string?` | nao |

### 2.5 Remote Debug Log

| Propriedade | Valor |
|---|---|
| Subject | `tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log` |
| Direcao | Agent -> Servidor |

| Campo | Tipo | Obrigatorio |
|---|---|---|
| `sessionId` | `Guid` | sim |
| `agentId` | `Guid?` | nao |
| `message` | `string?` | nao |
| `level` | `string?` | nao |
| `timestampUtc` | `DateTime?` | nao |
| `sequence` | `long?` | nao |

### 2.6 P2P Discovery

| Propriedade | Valor |
|---|---|
| Subject | `tenant.{clientId}.site.{siteId}.p2p.discovery` |
| Direcao | Servidor -> Agents |

| Campo | Tipo |
|---|---|
| `sequence` | `int` |
| `ttlSeconds` | `int` |
| `peers[].agentId` | `string` |
| `peers[].peerId` | `string` |
| `peers[].addrs` | `string[]` |
| `peers[].port` | `int` |

### 2.7 Dashboard Events

| Propriedade | Valor |
|---|---|
| Subject principal | `tenant.{clientId}.site.{siteId}.dashboard.events` |
| Subject sem tenant | `tenant.unscoped.dashboard.events` |
| Publishers | Servidor (C#) e Agent (Go) |
| Subscribers | Dashboard (NATS WS) e consumidores internos (NATS nativo) |
| Consumidor final de UI | Apenas NATS WS assinando `dashboard.events` |

#### 2.7.1 Envelope canonico (unico aceito)

| Campo | Tipo | Obrigatorio | Validacao |
|---|---|---|---|
| `eventType` | `string` | sim | Enum fechado (6 valores, PascalCase) |
| `data` | `object?` | nao | Shape conforme secao 2.8 |
| `timestampUtc` | `string` | sim | ISO-8601 UTC |
| `clientId` | `Guid?` | nao | Raiz do envelope |
| `siteId` | `Guid?` | nao | Raiz do envelope |

#### 2.7.2 `eventType` canonicos (enum fechado)

| `eventType` | Origem | Quando | Payload (`data`) |
|---|---|---|---|
| `AgentHeartbeat` | Servidor | Ao processar heartbeat | `AgentHeartbeatData` |
| `AgentStatusChanged` | Servidor | Agent ficou Online/Offline | `AgentStatusData` |
| `CommandCompleted` | Servidor | Ao processar resultado de comando | `CommandCompletedData` |
| `AgentHardwareReported` | Servidor | Ao processar hardware | `AgentHardwareData` |
| `AgentConnected` | Agent | Auditoria de conexao no broker | `AgentConnectionAuditData` |
| `AgentDisconnected` | Agent | Auditoria de desconexao no broker | `AgentConnectionAuditData` |

Valores legados como `agent_connected`, `agent_disconnected`, `command_result`, `timestamp`, `id` e similares sao transitorios e entram em regra de normalizacao da secao 10.

### 2.8 Payloads canonicos de DashboardEvent

#### 2.8.1 `AgentHeartbeat`

| Campo | Tipo | Obrigatorio |
|---|---|---|
| `agentId` | `Guid` | sim |
| `status` | `string` | sim (`"Online"`) |
| `clientId` | `Guid?` | nao |
| `siteId` | `Guid?` | nao |
| `ipAddress` | `string?` | nao |
| `hostname` | `string?` | nao |
| `agentVersion` | `string?` | nao |
| `timestampUtc` | `DateTime?` | nao |
| `cpuPercent` | `double?` | nao |
| `memoryPercent` | `double?` | nao |
| `memoryTotalGb` | `double?` | nao |
| `memoryUsedGb` | `double?` | nao |
| `diskPercent` | `double?` | nao |
| `diskTotalGb` | `double?` | nao |
| `diskUsedGb` | `double?` | nao |
| `p2pPeers` | `int?` | nao |
| `uptimeSeconds` | `long?` | nao |
| `processCount` | `int?` | nao |

#### 2.8.2 `AgentStatusChanged`

| Campo | Tipo | Obrigatorio | Valores |
|---|---|---|---|
| `agentId` | `Guid` | sim | identidade do agent |
| `status` | `string` | sim | `"Online"` ou `"Offline"` |

#### 2.8.3 `CommandCompleted`

| Campo | Tipo | Obrigatorio |
|---|---|---|
| `commandId` | `Guid` | sim |
| `exitCode` | `int` | sim |
| `output` | `string?` | nao |
| `errorMessage` | `string?` | nao |

#### 2.8.4 `AgentHardwareReported`

| Campo | Tipo | Obrigatorio |
|---|---|---|
| `agentId` | `Guid` | sim |

#### 2.8.5 Envelope entregue ao frontend

```ts
interface DashboardEvent<T = unknown> {
  eventType: string;
  data: T | null;
  timestampUtc: string;
  clientId?: string | null;
  siteId?: string | null;
}
```

#### 2.8.6 `AgentConnected` / `AgentDisconnected`

| Campo | Tipo | Obrigatorio | Observacao |
|---|---|---|---|
| `agentId` | `Guid` | sim | |
| `clientId` | `Guid?` | nao | redundante com envelope |
| `siteId` | `Guid?` | nao | redundante com envelope |
| `transport` | `string` | nao | `"nats"` ou `"nats_ws"` |
| `server` | `string?` | nao | URL do broker (apenas em connect) |
| `reason` | `string?` | nao | apenas em disconnect |

### 2.9 Subjects auxiliares e legado

| Subject | Status | Observacao |
|---|---|---|
| `tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware` | ingestao ativa no servidor | servidor converte para `AgentHardwareReported` em `dashboard.events` |

### 2.10 Equivalencia canonica de campos (transitoria)

| Campo canonico | Tipo | Legado aceito (ate 2026-06-01) |
|---|---|---|
| `agentId` | `string` UUID | `id`, `agentID` |
| `timestampUtc` | `string` ISO-8601 | `timestamp`, `timeStamp` |
| `cpuPercent` | `number` | `cpu` |
| `memoryPercent` | `number` | `memory` |
| `diskPercent` | `number` | `disk` |
| `hostname` | `string` | `hostName`, `machineName` |
| `agentVersion` | `string` | `version`, `agent_version` |
| `memoryTotalGb` | `number` | `memoryTotal` |
| `memoryUsedGb` | `number` | `memoryUsed` |
| `diskTotalGb` | `number` | `diskTotal` |
| `diskUsedGb` | `number` | `diskUsed` |
| `p2pPeers` | `number` | `p2pPeersCount` |
| `uptimeSeconds` | `number` | `uptime` |
| `processCount` | `number` | `processes` |
| `ipAddress` | `string` | `lastIpAddress`, `ip` |

### 2.11 Equivalencia de `eventType` (transitoria)

| Canonico | Legado | Acao ate 2026-06-01 | Acao apos 2026-06-01 |
|---|---|---|---|
| `CommandCompleted` | `command_result` | normalizar + warning | rejeitar |
| `AgentConnected` | `agent_connected` | normalizar + warning | rejeitar |
| `AgentDisconnected` | `agent_disconnected` | normalizar + warning | rejeitar |

---

## 3. NATS via WebSocket - Contrato para Dashboard

### 3.1 Endpoint e autenticacao

| Item | Regra |
|---|---|
| URL | `wss://<broker-ou-gateway>/nats` |
| Auth | token JWT com claims de subscribe/publicacao por subject |
| TLS | obrigatorio em producao |
| Keepalive | cliente deve responder `PING/PONG` do protocolo NATS |
| Erro de auth | conexao deve ser encerrada imediatamente |

### 3.2 Fluxo de inscricao esperado (frontend)

| Ordem | Acao | Obrigatorio | Observacao |
|---|---|---|---|
| 1 | Abrir conexao NATS WS autenticada | sim | sem token valido, abortar |
| 2 | Assinar subject de escopo desejado | sim | global, client ou site |
| 3 | Processar somente payload no envelope canonico | sim | usar `normalizeDashboardEvent()` |
| 4 | Trocar assinatura ao mudar escopo | opcional | `UNSUB` no subject anterior |
| 5 | Reconnect com backoff + re-subscribe | sim | manter idempotencia no estado da UI |

### 3.3 Subjects de dashboard por escopo

| Escopo | Subject sugerido |
|---|---|
| Global | `tenant.*.site.*.dashboard.events` |
| Cliente | `tenant.{clientId}.site.*.dashboard.events` |
| Site | `tenant.{clientId}.site.{siteId}.dashboard.events` |
| Sem tenant | `tenant.unscoped.dashboard.events` |

### 3.4 Exemplo de comandos no protocolo NATS WS

```text
CONNECT {"lang":"ts","protocol":1,"verbose":false,"pedantic":false,"auth_token":"<jwt>"}
SUB tenant.11111111-1111-1111-1111-111111111111.site.*.dashboard.events 1
PING
PONG
```

Exemplo de payload recebido em `MSG`:

```json
{
  "eventType": "AgentHeartbeat",
  "data": {
    "agentId": "d2719a7d-43bb-4e7e-bbe6-18dce7bf1db7",
    "status": "Online",
    "hostname": "HOMOLOG-WIN-01",
    "cpuPercent": 17.3,
    "memoryPercent": 42.1,
    "diskPercent": 58.2,
    "processCount": 120
  },
  "timestampUtc": "2026-05-05T10:00:00Z",
  "clientId": "11111111-1111-1111-1111-111111111111",
  "siteId": "22222222-2222-2222-2222-222222222222"
}
```

### 3.5 Contrato de consumo do frontend

O frontend DEVE:

1. Ter um unico entry point para eventos: mensagem NATS em `dashboard.events`.
2. Normalizar para `{ eventType, data, timestampUtc, clientId, siteId }`.
3. Rejeitar evento nao conforme com `console.warn('[CONTRACT_VIOLATION]', detalhes)`.
4. Usar nomes de campo exatos (`agentId`, `cpuPercent`, `hostname`, `timestampUtc`).
5. Ignorar `eventType` fora do enum canonico.

---

## 4. Comandos Especiais

### 4.1 Remote Debug

Envelope:

```json
{
  "commandType": "remotedebug",
  "payload": {
    "action": "start|stop",
    "sessionId": "Guid",
    "logLevel": "info|debug|warn|error",
    "expiresAtUtc": "ISO8601",
    "stream": {
      "natsSubject": "tenant.{c}.site.{s}.agent.{a}.remote-debug.log",
      "natsWssUrl": "wss://...",
      "encoding": "json"
    }
  }
}
```

Validacao minima de payload:

| Campo | Obrigatorio | Valores |
|---|---|---|
| `action` | sim | `"start"` ou `"stop"` |
| `sessionId` | sim | Guid |
| `logLevel` | nao (default `"info"`) | `"info"`, `"debug"`, `"warn"`, `"error"` |
| `expiresAtUtc` | sim quando `action = "start"` | ISO-8601 UTC |
| `stream.natsSubject` | sim | subject valido |
| `stream.natsWssUrl` | nao | URL WS valida |
| `stream.encoding` | nao (default `"json"`) | `"json"` |

### 4.2 Alerta PSADT

```json
{
  "commandType": "showpsadtalert",
  "payload": {
    "alertId": "string",
    "type": "modal|toast",
    "title": "string",
    "message": "string",
    "timeoutSeconds": 120,
    "icon": "info|warning|error|question",
    "actions": [{"label": "Sim", "value": "yes"}],
    "defaultAction": "no"
  }
}
```

### 4.3 Notificacao

```json
{
  "commandType": "notification",
  "payload": {
    "notificationId": "string",
    "idempotencyKey": "string",
    "title": "string",
    "message": "string",
    "mode": "notify_only|interactive",
    "severity": "low|medium|high|critical",
    "eventType": "string",
    "layout": "toast|modal|banner",
    "timeoutSeconds": 8,
    "metadata": {}
  }
}
```

### 4.4 Self-Update

```json
{
  "commandType": "update",
  "payload": {
    "action": "check-update|install|rollback",
    "version": "string?",
    "url": "string?"
  }
}
```

---

## 5. Regras de Integracao

| # | Regra |
|---|---|
| 1 | Notificacoes ao agent: enviar por `.command` com `commandType = "notification"`. |
| 2 | Envio de comando: somente NATS `.command`. |
| 3 | Envio de sync ping: somente NATS `.sync.ping`. |
| 4 | Remote debug: usar `.remote-debug.log`. |
| 5 | Dashboard events: publicar e consumir em `dashboard.events` (NATS nativo/WS). |
| 6 | Cada componente valida mensagens recebidas e emitidas. |
| 7 | Violacao de contrato deve registrar `[CONTRACT_VIOLATION]` com `source=<nats|nats_ws>`. |
| 8 | E proibido criar caminho de evento fora do NATS para atualizar dashboard. |

---

## 6. Especificacoes de Correcao por Componente

### 6.1 Agent (Go) - C:\Projetos\Discovery

| # | Arquivo | Mudanca | Prioridade |
|---|---|---|---|
| 6.1.1 | `runtime_nats.go` | `timestamp` -> `timestampUtc` no envelope | P0 |
| 6.1.2 | `runtime_nats.go` | `agent_connected`/`agent_disconnected` -> PascalCase canonico | P0 |
| 6.1.3 | `runtime_nats.go` | Remover publish de `command_result` em `dashboard.events` | P1 |
| 6.1.4 | `runtime_nats.go` | Promover `clientId` e `siteId` para raiz do envelope | P0 |
| 6.1.5 | `runtime_nats.go` | Validacao de saida contra enum de `eventType` | P1 |
| 6.1.6 | `runtime_nats.go` | Garantir `processCount` no heartbeat | P2 |

### 6.2 Servidor (C#) - C:\Projetos\DiscoveryRMM_API

| # | Componente | Mudanca | Prioridade |
|---|---|---|---|
| 6.2.1 | Pipeline de ingestao NATS | Validar envelope com log `[CONTRACT_VIOLATION]` | P0 |
| 6.2.2 | Pipeline de normalizacao | `timestamp` -> `timestampUtc` temporario com warning | P0 |
| 6.2.3 | Pipeline de normalizacao | snake_case -> PascalCase temporario com warning | P1 |
| 6.2.4 | Pipeline de normalizacao | extrair `clientId`/`siteId` de `data` quando ausentes (temporario) | P1 |
| 6.2.5 | Publicador de dashboard events | publicar somente envelope canonico em `dashboard.events` | P0 |
| 6.2.6 | Gateway NATS WS | reforcar auth por claim/subject no subscribe | P1 |

### 6.3 Dashboard (Frontend) - C:\Projetos\DiscoveryRMM_Site

| # | Componente | Mudanca | Prioridade |
|---|---|---|---|
| 6.3.1 | Cliente realtime | usar apenas cliente NATS WS | P0 |
| 6.3.2 | Event normalizer | implementar `normalizeDashboardEvent(rawEvent)` estrito | P0 |
| 6.3.3 | Tipos TS | aceitar somente campos canonicos (sem aliases) | P0 |
| 6.3.4 | Parsing de timestamp | aceitar somente `timestampUtc` ISO-8601 | P1 |
| 6.3.5 | Assinaturas por escopo | usar subjects globais/client/site e trocar via `UNSUB/SUB` | P1 |
| 6.3.6 | Observabilidade | `console.warn('[CONTRACT_VIOLATION]')` para nao conformidade | P1 |

---

## 7. Regras de Validacao Obrigatoria

### 7.1 `timestampUtc`

| Regra | Acao em violacao |
|---|---|
| Campo deve ser `timestampUtc` | warning + normalizacao (transicao) |
| Valor deve ser ISO-8601 UTC | descartar quando invalido |
| Deve estar no envelope raiz | descartar quando ausente |

### 7.2 `eventType`

| Regra | Acao em violacao |
|---|---|
| Deve ser PascalCase | warning + normalizacao (transicao) |
| Deve estar no enum fechado | descartar |
| `command_result` nao deve ser duplicado no dashboard | descartar |

### 7.3 Envelope

| Regra | Acao em violacao |
|---|---|
| `clientId`/`siteId` na raiz | extrair de `data` com warning (transicao) |
| Heartbeat exige `agentId` | descartar |
| CommandCompleted exige `commandId` | descartar |

### 7.4 Integridade

| Regra | Acao |
|---|---|
| `AgentStatusChanged` sem `status` | descartar |
| `AgentConnected` sem `transport` | aceitar com warning |

### 7.5 Aliases

| Regra | Ate 2026-06-01 | Apos 2026-06-01 |
|---|---|---|
| `id`/`agentID` no lugar de `agentId` | normalizar + warning | rejeitar |
| `timestamp`/`timeStamp` no lugar de `timestampUtc` | normalizar + warning | rejeitar |
| `cpu` no lugar de `cpuPercent` | normalizar + warning | rejeitar |
| `hostName`/`machineName` no lugar de `hostname` | normalizar + warning | rejeitar |

### 7.6 Modelo de log

```text
[CONTRACT_VIOLATION] component=<Agent|Server|Dashboard> field=<nome> expected=<valor> received=<valor> source=<nats|nats_ws>
```

---

## 8. Plano de Convergencia em 3 Fases

### Fase 1 - Baseline documentado (imediato)

Objetivo: toda violacao fica visivel em log.

| Acao | Responsavel |
|---|---|
| Publicar contrato 4.0.0 | Servidor |
| Adicionar warning de contrato no dashboard | Frontend |
| Adicionar warning de contrato no agent | Agent |
| Mapear divergencias como issues | Todos |

### Fase 2 - Correcao do Agent (ate 2026-05-20)

Objetivo: agent publica 100% conforme.

| Acao | Validacao |
|---|---|
| `timestampUtc` correto | grep sem `timestamp` legado no publish |
| `eventType` canonico | ausencia de snake_case no publish |
| remover `command_result` em dashboard.events | codigo removido |
| `clientId`/`siteId` no envelope raiz | schema ok |

### Fase 3 - Hardening dashboard/servidor (ate 2026-06-01)

Objetivo: modo estrito ativo, rejeitando nao conformidade.

| Acao | Validacao |
|---|---|
| frontend estrito em `normalizeDashboardEvent` | rejeita aliases |
| remover fallbacks do servidor | sem normalizacao temporaria |
| gateway NATS WS com auth estrita | subscribe sem permissao bloqueado |

---

## 9. Checklist de Conformidade

### 9.1 Agent

| Item |
|---|
| Heartbeat envia `timestampUtc` |
| `eventType` em PascalCase canonico |
| Nao publica `command_result` em `dashboard.events` |
| `clientId`/`siteId` na raiz do envelope |
| `processCount` presente no heartbeat |
| Log `[CONTRACT_VIOLATION]` na borda de saida |

### 9.2 Servidor

| Item |
|---|
| Pipeline NATS valida envelope e loga violacao |
| Fallbacks transitorios ativos somente ate 2026-06-01 |
| Eventos invalidos sao descartados |
| Publicacao do dashboard usa envelope canonico |
| Gateway NATS WS valida autenticacao/autorizacao por subject |

### 9.3 Dashboard

| Item |
|---|
| Usa somente NATS WS para realtime |
| `normalizeDashboardEvent()` aplicado em toda mensagem |
| Sem aliases de campos |
| Apenas `timestampUtc` aceito |
| Subjects por escopo (global/client/site) funcionando |
| Warn de contrato em mensagens invalidas |

---

## 10. Periodo de Transicao

| Fase | Periodo | Comportamento |
|---|---|---|
| Transicao | 2026-05-05 a 2026-06-01 | aceitar legado com warning e normalizacao |
| Endurecimento | apos 2026-06-01 | rejeitar nao conformidade e remover normalizacao |

Fallbacks temporarios (remover apos 2026-06-01):

| Codigo | Descricao |
|---|---|
| `NormalizeTimestamp()` | `timestamp` para `timestampUtc` |
| `NormalizeEventType()` | snake_case para PascalCase |
| `ExtractTenantFromData()` | extrai `clientId`/`siteId` de `data` |
| `NormalizeFieldAliases()` | converte aliases de campo para canonicos |

---

## 11. Arquitetura Alvo - Fluxo Unico por NATS

```text
Agent (Go)                Broker NATS                 Servidor (C#)               Dashboard (TS)
    |                          |                           |                             |
    |-- .heartbeat ----------->|-------------------------->|                             |
    |-- .result -------------> |-------------------------->|                             |
    |-- .dashboard.events ---->|-------------------------->|                             |
    |                          |<--------------------------|-- publish .dashboard.events |
    |                          |----------------------------------------------------------->|
    |                          |                       (NATS WS subscribe por escopo)      |
```

Regra de ouro: o dashboard recebe eventos exclusivamente por assinatura NATS WS em `dashboard.events`.

### 11.1 Matriz de canais de entrega

| Evento | Subject | Transporte de entrega para UI | Quem publica |
|---|---|---|---|
| `AgentHeartbeat` | `tenant.{c}.site.{s}.dashboard.events` | NATS WS | Servidor |
| `AgentStatusChanged` | `tenant.{c}.site.{s}.dashboard.events` | NATS WS | Servidor |
| `CommandCompleted` | `tenant.{c}.site.{s}.dashboard.events` | NATS WS | Servidor |
| `AgentHardwareReported` | `tenant.{c}.site.{s}.dashboard.events` | NATS WS | Servidor |
| `AgentConnected` | `tenant.{c}.site.{s}.dashboard.events` | NATS WS | Agent |
| `AgentDisconnected` | `tenant.{c}.site.{s}.dashboard.events` | NATS WS | Agent |

### 11.2 Matriz completa de subjects

| Subject | Direcao | Payload |
|---|---|---|
| `tenant.{c}.site.{s}.agent.{a}.heartbeat` | Agent -> Servidor | `AgentHeartbeat` |
| `tenant.{c}.site.{s}.agent.{a}.command` | Servidor -> Agent | comando |
| `tenant.{c}.site.{s}.agent.{a}.result` | Agent -> Servidor | resultado de comando |
| `tenant.{c}.site.{s}.agent.{a}.sync.ping` | Servidor -> Agent | `SyncInvalidationPingMessage` |
| `tenant.{c}.site.{s}.agent.{a}.remote-debug.log` | Agent -> Servidor | log de sessao |
| `tenant.{c}.site.{s}.agent.{a}.hardware` | Agent -> Servidor | hardware report |
| `tenant.{c}.site.{s}.p2p.discovery` | Servidor -> Agent | descoberta p2p |
| `tenant.{c}.site.{s}.dashboard.events` | Agent/Servidor -> Consumidores | `DashboardEvent` |
| `tenant.unscoped.dashboard.events` | Servidor -> Consumidores | `DashboardEvent` |

---

## 12. Plano de Acao Imediato

### Bloqueantes

| # | Acao | Quem |
|---|---|---|
| 1 | Corrigir `timestamp` -> `timestampUtc` no agent | Agent |
| 2 | Corrigir `eventType` legado -> canonico no agent | Agent |
| 3 | Promover `clientId`/`siteId` para raiz do envelope | Agent |
| 4 | Remover `command_result` de `dashboard.events` | Agent |

### Alta prioridade

| # | Acao | Quem |
|---|---|---|
| 5 | Implementar `normalizeDashboardEvent()` estrito | Frontend |
| 6 | Consolidar cliente realtime em NATS WS | Frontend |
| 7 | Adicionar validacao `[CONTRACT_VIOLATION]` no servidor | Servidor |
| 8 | Endurecer auth de subscribe por subject no gateway WS | Servidor |

### Media prioridade

| # | Acao | Quem |
|---|---|---|
| 9 | Remover todos os fallbacks temporarios apos prazo | Servidor |
| 10 | Teste de integracao fim-a-fim heartbeat -> dashboard.events -> UI | QA/Todos |

---

## 13. Recomendacoes Finais

1. Priorizar as quatro correçoes bloqueantes do agent para zerar a maior parte das violacoes rapidamente.
2. Tratar o frontend em modo estrito antes do endurecimento de 2026-06-01.
3. Automatizar validacao de contrato em CI (schema + testes de integracao).
4. Garantir autorizacao por subject no NATS WS para evitar vazamento cross-tenant.
5. Fazer deploy coordenado de agent, servidor e dashboard na virada para modo estrito.

---

Fim do documento. Versao 4.0.0 - 2026-05-05.