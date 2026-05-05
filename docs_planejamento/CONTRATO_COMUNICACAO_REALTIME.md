# 📐 Contrato de Comunicação em Tempo Real — DiscoveryRMM API

> **Versão:** 3.0.0 | **Data:** 2026-05-05
> **Público:** Servidor (C# — `C:\Projetos\DiscoveryRMM_API`), Agent (Go — `C:\Projetos\Discovery`), Frontend (JS/TS — `C:\Projetos\DiscoveryRMM_Site`)
> **Propósito:** Especificação canônica executável — todos os componentes DEVEM validar entrada e saída contra este documento.
> **Status:** ⚠️ Período de transição até 2026-06-01 — servidor aceita formato legado com warnings. Após essa data, mensagens não-conformes são rejeitadas.

---

## 📋 Changelog

| Versão | Data | Mudanças |
|---|---|---|
| **3.0.0** | 2026-05-05 | 🔵 Consolidação: +Seção 2.10 (Tabela de Equivalência de Campos), +Seção 2.11 (Equivalência de eventType), +Seção 8 (Plano de Convergência em 3 Fases), +Seção 13 (Arquitetura Alvo — Fluxo Único), +Seção 14 (Plano de Ação Imediato). Reconciliação dos 3 artefatos (contrato canônico × dashboard em produção × agent em produção). |
| 2.0.0 | 2026-05-05 | 🔴 Contrato executável: +Seção 6 (Especificações por componente), +Seção 7 (Regras de Validação), +Seção 9 (Checklist de Conformidade), +Princípios 14-18, período de transição documentado, Comandos Especiais com validação de payload |
| 1.4.0 | 2026-05-05 | Princípios 12-13, Seção 2.8 (payloads canônicos), 2.8.6 (AgentConnected/AgentDisconnected), 2.9 (.hardware), 3.3.1 (remote-debug payloads), Seção 5 (dispatch real) |
| 1.3.0 | — | Envelope canônico 2.7.1, divergências do agent mapeadas |
| 1.2.0 | — | DashboardEvent como único canal, Seção 3.5 (consumo frontend) |
| 1.0.0 | — | Contrato inicial |

---

## 1. Princípios

| # | Regra |
|---|-------|
| **0** | **Single Source of Truth:** Este arquivo no repositório da API é a única especificação válida. Comportamento divergente no agent, dashboard ou servidor é bug — não feature — e deve ser tratado como tal no backlog. |
| 1 | NATS é o transporte primário para servidor ↔ agent |
| 2 | SignalR é o transporte primário para servidor ↔ dashboard |
| 3 | SignalR é fallback para agent ↔ servidor quando NATS indisponível |
| 4 | JSON camelCase em todos os transportes |
| 5 | Subjects NATS em minúsculas, separados por ponto |
| 6 | Eventos Dashboard chegam ao frontend via evento SignalR `"DashboardEvent"` |
| 7 | Notificações ao agent chegam via `ExecuteCommand` com `commandType: "notification"` |
| 8 | NÃO enviar `NotificationReceived` para o grupo `agent-{id}` |
| 9 | Mesmo DTO/shape em NATS e SignalR — não duplicar nem renomear campos por transporte |
| 10 | Serialização SignalR: PascalCase em C# → **camelCase no wire** (JsonHubProtocol padrão) |
| 11 | Datas sempre em ISO-8601 UTC (`DateTime` C# → string `2026-05-05T10:00:00Z`) |
| 12 | Campo de tempo sempre nomeado `timestampUtc` — nunca `timestamp` |
| 13 | `eventType` em PascalCase em todos os transportes (NATS e SignalR) |
| 14 | Dashboard DEVE ignorar eventos diretos legados; apenas `DashboardEvent` é aceito como canal de eventos |
| 15 | Nomes de campo são **exatos** — sem aliases. Se o contrato diz `agentId`, apenas `agentId` é válido (não `id`, `agentID`) |
| 16 | Toda violação de contrato DEVE gerar log de warning com prefixo `[CONTRACT_VIOLATION]` |
| 17 | Período de transição: servidor aceita formato legado com warnings até 2026-06-01; após, rejeita mensagens não-conformes |
| 18 | Cada componente valida na borda de entrada — nunca confiar que o emissor está conforme |
| 19 | O Dashboard tem **um único entry point** para eventos de agentes: `DashboardEvent` no SignalR `/hubs/agent`. Qualquer outro caminho (NATS direto, eventos SignalR diretos) é proibido. |
| 20 | A bridge `NatsSignalRBridge` é o ponto de controle central — toda mensagem de dashboard passa por ela. |

---

## 2. NATS — Contrato de Subjects

### Nomenclatura

```
tenant.{clientId}.site.{siteId}.agent.{agentId}.{messageType}
tenant.{clientId}.site.{siteId}.p2p.discovery
tenant.{clientId}.dashboard.events
tenant.unscoped.dashboard.events
```

### 2.1 Heartbeat

| Propriedade | Valor |
|---|---|
| **Subject** | `tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat` |
| **Direção** | Agent → Servidor |
| **DTO** | `AgentHeartbeat` |

| Campo | Tipo C# | Obrigatório |
|---|---|---|
| `agentId` | `Guid` | ✅ |
| `clientId` | `Guid?` | ❌ |
| `siteId` | `Guid?` | ❌ |
| `ipAddress` | `string?` | ❌ |
| `hostname` | `string?` | ❌ |
| `agentVersion` | `string?` | ❌ |
| `timestampUtc` | `DateTime?` | ❌ |
| `cpuPercent` | `double?` | ❌ |
| `memoryPercent` | `double?` | ❌ |
| `memoryTotalGb` | `double?` | ❌ |
| `memoryUsedGb` | `double?` | ❌ |
| `diskPercent` | `double?` | ❌ |
| `diskTotalGb` | `double?` | ❌ |
| `diskUsedGb` | `double?` | ❌ |
| `p2pPeers` | `int?` | ❌ |
| `uptimeSeconds` | `long?` | ❌ |
| `processCount` | `int?` | ❌ |

### 2.2 Comando

| Propriedade | Valor |
|---|---|
| **Subject** | `tenant.{clientId}.site.{siteId}.agent.{agentId}.command` |
| **Direção** | Servidor → Agent |

```json
{ "commandId": "Guid", "commandType": "string", "payload": "{\"command\":\"Get-Date\",\"timeoutSec\":30}" }
```

### 2.3 Resultado

| Propriedade | Valor |
|---|---|
| **Subject** | `tenant.{clientId}.site.{siteId}.agent.{agentId}.result` |
| **Direção** | Agent → Servidor |

```json
{ "commandId": "Guid", "exitCode": 0, "output": "string", "errorMessage": "string" }
```

### 2.4 Sync Ping

| Propriedade | Valor |
|---|---|
| **Subject** | `tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping` |
| **Direção** | Servidor → Agent |

| Campo | Tipo | Obrigatório |
|---|---|---|
| `eventId` | `Guid` | ✅ |
| `agentId` | `Guid` | ✅ |
| `eventType` | `string` | ✅ (`"sync.invalidated"`) |
| `resource` | `string` | ✅ |
| `scopeType` | `string` | ✅ |
| `scopeId` | `Guid?` | ❌ |
| `installationType` | `string?` | ❌ |
| `revision` | `string` | ✅ |
| `reason` | `string?` | ❌ |
| `changedAtUtc` | `DateTime` | ✅ |
| `correlationId` | `string?` | ❌ |

### 2.5 Remote Debug Log

| Propriedade | Valor |
|---|---|
| **Subject** | `tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log` |
| **Direção** | Agent → Servidor |

| Campo | Tipo | Obrigatório |
|---|---|---|
| `sessionId` | `Guid` | ✅ |
| `agentId` | `Guid?` | ❌ |
| `message` | `string?` | ❌ |
| `level` | `string?` | ❌ |
| `timestampUtc` | `DateTime?` | ❌ |
| `sequence` | `long?` | ❌ |

### 2.6 P2P Discovery

| Propriedade | Valor |
|---|---|
| **Subject** | `tenant.{clientId}.site.{siteId}.p2p.discovery` |
| **Direção** | Servidor → Agents |

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
| **Subject** | `tenant.{clientId}.site.{siteId}.dashboard.events` |
| **Subject (sem tenant)** | `tenant.unscoped.dashboard.events` |
| **Publishers** | Servidor (C#) **e** Agent (Go) |
| **Subscriber** | [`NatsSignalRBridge`](../src/Discovery.Api/Services/NatsSignalRBridge.cs) → SignalR `DashboardEvent` |
| **Consumidor final** | Apenas SignalR `DashboardEvent` — o Dashboard **NÃO** assina `dashboard.events` diretamente no NATS |

#### 2.7.1 Envelope canônico (único aceito — v3.0.0)

| Campo | Tipo | Obrigatório | Validação |
|---|---|---|---|
| `eventType` | `string` | ✅ | Enum fechado (6 valores, PascalCase — ver 2.7.2). Qualquer valor não listado → `[CONTRACT_VIOLATION]` + descarte (após 2026-06-01) |
| `data` | `object?` | ❌ | Shape conforme seção 2.8. Se presente, deve corresponder ao `eventType` |
| `timestampUtc` | `string` | ✅ | ISO-8601 UTC (`2026-05-05T10:00:00Z`). Campo `timestamp` (sem `Utc`) → `[CONTRACT_VIOLATION]` |
| `clientId` | `Guid?` | ❌ | Usado pela bridge para rotear `dashboard:client:{id}`. Deve estar no nível raiz |
| `siteId` | `Guid?` | ❌ | Usado pela bridge para rotear `dashboard:site:{id}`. Deve estar no nível raiz |

#### 2.7.2 `eventType` canônicos (enum fechado)

| `eventType` | Origem | Quando | Payload (`data`) |
|---|---|---|---|
| `AgentHeartbeat` | Servidor | Ao processar heartbeat (NATS ou SignalR) | [2.8.1](#281-agentheartbeat) |
| `AgentStatusChanged` | Servidor | Agent ficou Online ou Offline | [2.8.2](#282-agentstatuschanged) |
| `CommandCompleted` | Servidor | Ao processar resultado de comando | [2.8.3](#283-commandcompleted) |
| `AgentHardwareReported` | Servidor | Ao processar coleta de hardware | [2.8.4](#284-agenthardwarereported) |
| `AgentConnected` | Agent | Auditoria de conexão NATS | [2.8.6](#286-agentconnected--agentdisconnected-auditoria-do-agent) |
| `AgentDisconnected` | Agent | Auditoria de desconexão NATS | [2.8.6](#286-agentconnected--agentdisconnected-auditoria-do-agent) |

> ⚠️ **Valores NÃO aceitos:** `agent_connected`, `agent_disconnected`, `command_result`, `agentheartbeat`, `agentoffline`, ou qualquer outro não listado acima. Estes são legado do agent e devem ser rejeitados.

#### 2.7.3 Divergências atuais do agent (alinhar)

O agent (Go) hoje publica neste subject usando shape **não conforme**. Pontos a corrigir no agent:

| Item | Agent atual | Padrão exigido |
|---|---|---|
| Campo de tempo | `timestamp` | `timestampUtc` |
| `eventType` | `agent_connected`, `agent_disconnected`, `command_result` | `AgentConnected`, `AgentDisconnected` (e não duplicar `command_result` — já coberto pelo `.result` + `CommandCompleted` do servidor) |
| `clientId`/`siteId` | apenas dentro de `data` | também no envelope (necessário para rotear grupos no SignalR) |

> Enquanto a correção do agent não for feita, a bridge aplica fallback temporário (ver Seção 12, "Fallbacks temporários"). Após 2026-06-01, mensagens não-conformes serão descartadas.

### 2.8 Payloads canônicos do `DashboardEvent` (contrato único NATS + SignalR)

O `data` de cada `eventType` é o **mesmo objeto** quando publicado em NATS (`tenant...dashboard.events`) e quando emitido em SignalR (`/hubs/agent` → `DashboardEvent`). O servidor publica em NATS e a [`NatsSignalRBridge`](../src/Discovery.Api/Services/NatsSignalRBridge.cs) re-emite no SignalR sem alterar o shape.

#### 2.8.1 `AgentHeartbeat`

| Campo | Tipo | Obrigatório | Origem |
|---|---|---|---|
| `agentId` | `Guid` | ✅ | identidade autenticada |
| `status` | `string` | ✅ | sempre `"Online"` neste evento |
| `clientId` | `Guid?` | ❌ | heartbeat → ou cache de tenant |
| `siteId` | `Guid?` | ❌ | heartbeat → ou cache de tenant |
| `ipAddress` | `string?` | ❌ | DTO `AgentHeartbeat` |
| `hostname` | `string?` | ❌ | DTO `AgentHeartbeat` |
| `agentVersion` | `string?` | ❌ | DTO `AgentHeartbeat` |
| `timestampUtc` | `DateTime?` | ❌ | DTO `AgentHeartbeat` |
| `cpuPercent` | `double?` | ❌ | DTO `AgentHeartbeat` |
| `memoryPercent` | `double?` | ❌ | DTO `AgentHeartbeat` |
| `memoryTotalGb` | `double?` | ❌ | DTO `AgentHeartbeat` |
| `memoryUsedGb` | `double?` | ❌ | DTO `AgentHeartbeat` |
| `diskPercent` | `double?` | ❌ | DTO `AgentHeartbeat` |
| `diskTotalGb` | `double?` | ❌ | DTO `AgentHeartbeat` |
| `diskUsedGb` | `double?` | ❌ | DTO `AgentHeartbeat` |
| `p2pPeers` | `int?` | ❌ | DTO `AgentHeartbeat` |
| `uptimeSeconds` | `long?` | ❌ | DTO `AgentHeartbeat` |
| `processCount` | `int?` | ❌ | DTO `AgentHeartbeat` |

> Os nomes acima refletem a serialização final no wire (camelCase). No código C# os mesmos campos aparecem em PascalCase.

#### 2.8.2 `AgentStatusChanged`

| Campo | Tipo | Obrigatório | Valores |
|---|---|---|---|
| `agentId` | `Guid` | ✅ | identidade do agent |
| `status` | `string` | ✅ | `"Online"` \| `"Offline"` |

#### 2.8.3 `CommandCompleted`

| Campo | Tipo | Obrigatório |
|---|---|---|
| `commandId` | `Guid` | ✅ |
| `exitCode` | `int` | ✅ |
| `output` | `string?` | ❌ |
| `errorMessage` | `string?` | ❌ |

#### 2.8.4 `AgentHardwareReported`

| Campo | Tipo | Obrigatório |
|---|---|---|
| `agentId` | `Guid` | ✅ |

#### 2.8.5 Envelope final entregue ao frontend

Independente de NATS ou SignalR, o frontend deve assumir o envelope:

```ts
interface DashboardEvent<T = unknown> {
  eventType: string;        // arguments[0] — enum fechado (6 valores)
  data: T | null;           // arguments[1] — shape conforme 2.8.1..2.8.6
  timestampUtc: string;     // arguments[2] — ISO-8601 UTC
}
```

#### 2.8.6 `AgentConnected` / `AgentDisconnected` (auditoria do agent)

Eventos publicados pelo **agent (Go)** ao conectar/desconectar do NATS. Roteados ao frontend pela bridge no mesmo envelope `DashboardEvent`.

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `agentId` | `Guid` | ✅ | |
| `clientId` | `Guid?` | ❌ | redundante com envelope, mantido por conveniência |
| `siteId` | `Guid?` | ❌ | redundante com envelope, mantido por conveniência |
| `transport` | `string` | ❌ | `"nats"` \| `"signalr"` |
| `server` | `string?` | ❌ | URL do broker (apenas em `AgentConnected`) |
| `reason` | `string?` | ❌ | apenas em `AgentDisconnected` |

### 2.9 Subjects auxiliares e legado

| Subject | Status | Observação |
|---|---|---|
| `tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware` | Ingestão ativa no servidor | O servidor já está inscrito e converte mensagens em `DashboardEvent` com `eventType = "AgentHardwareReported"`. Publicação pelo agent é opcional/compatibilidade. |

### 2.10 Tabela Canônica de Equivalência de Campos (NOVA — v3.0.0)

Esta tabela substitui a noção de "aliases aceitáveis" por um mapeamento explícito de **campo legado → campo canônico**, com prazo de remoção. Durante o período de transição (até 2026-06-01), campos legados são normalizados com warning `[CONTRACT_VIOLATION]`. Após essa data, apenas o campo canônico é aceito.

| Campo canônico | Tipo | Legado aceito (transitório) | Remover após | Observação |
|---|---|---|---|---|
| `agentId` | `string` (UUID) | `id`, `agentID` | 2026-06-01 | Identificador único do agente |
| `timestampUtc` | `string` (ISO-8601) | `timestamp`, `timeStamp` | 2026-06-01 | Sempre UTC |
| `cpuPercent` | `number` | `cpu` | 2026-06-01 | 0–100 |
| `memoryPercent` | `number` | `memory` | 2026-06-01 | 0–100 |
| `diskPercent` | `number` | `disk` | 2026-06-01 | 0–100 |
| `hostname` | `string` | `hostName`, `machineName` | 2026-06-01 | Nome da máquina |
| `agentVersion` | `string` | `version`, `agent_version` | 2026-06-01 | Semver |
| `memoryTotalGb` | `number` | `memoryTotal` | 2026-06-01 | GB |
| `memoryUsedGb` | `number` | `memoryUsed` | 2026-06-01 | GB |
| `diskTotalGb` | `number` | `diskTotal` | 2026-06-01 | GB |
| `diskUsedGb` | `number` | `diskUsed` | 2026-06-01 | GB |
| `p2pPeers` | `number` | `p2pPeersCount` | 2026-06-01 | Quantidade |
| `uptimeSeconds` | `number` | `uptime` | 2026-06-01 | Segundos |
| `processCount` | `number` | `processes` | 2026-06-01 | Quantidade |
| `ipAddress` | `string` | `lastIpAddress`, `ip` | 2026-06-01 | Último IP observado |

### 2.11 Tabela de Equivalência de `eventType` (NOVA — v3.0.0)

| Canônico (PascalCase) | Legado (snake_case ou outro) | Quem publica | Ação na bridge (transição) | Ação na bridge (pós 2026-06-01) |
|---|---|---|---|---|
| `AgentHeartbeat` | — | Servidor | Roteia normalmente | Roteia normalmente |
| `AgentStatusChanged` | — | Servidor | Roteia normalmente | Roteia normalmente |
| `CommandCompleted` | `command_result` | Servidor (canônico) / Agent (legado) | Se legado, normalizar + warning | Rejeitar legado |
| `AgentHardwareReported` | — | Servidor | Roteia normalmente | Roteia normalmente |
| `AgentConnected` | `agent_connected` | Agent (Go) | Normalizar + warning | Rejeitar legado |
| `AgentDisconnected` | `agent_disconnected` | Agent (Go) | Normalizar + warning | Rejeitar legado |

---

## 3. SignalR — Contrato de Hubs

### 3.1 Hub `/hubs/agent`

> SignalR é posicional: nomes de argumento do servidor (C#) são **definitivos**. Apelidos usados internamente pelo agent (`cmdId`, `cmdType`, `errText`) são aceitáveis em código cliente, mas o contrato canonical usa os nomes da assinatura C#: `commandId`, `commandType`, `payload`, `errorMessage`.

#### Agent → Servidor (invoke)

| Método | Parâmetros (nome canônico C#) | Apelido Agent (Go) |
|---|---|---|
| `RegisterAgent` | `agentId:Guid, ipAddress:string?` | — |
| `Heartbeat` | `agentId:Guid, ipAddress:string?` (legado, sem métricas) | — |
| `HeartbeatV2` | `heartbeat:AgentHeartbeat` | — |
| `CommandResult` | `commandId:Guid, exitCode:int, output:string?, errorMessage:string?` | `cmdId`, `errText` |
| `SecureHandshakeAsync` | `observedTlsHash:string` | — |
| `PushRemoteDebugLog` | `sessionId:Guid, level:string?, message:string?, timestampUtc:DateTime?, sequence:long?` | — |

#### Servidor → Agent (client event)

| Evento | Payload |
|---|---|
| `ExecuteCommand` | `(commandId:Guid, commandType:string, payload:string)` |
| `SyncPing` | `(SyncInvalidationPingMessage)` |
| `HandshakeChallenge` | `(nonce:string, expectedTlsHash:string)` |
| `HandshakeAck` | `(success:bool, message:string)` |

#### Dashboard → Servidor (invoke)

| Método | Grupo |
|---|---|
| `JoinDashboard` | `dashboard:global` |
| `JoinClientDashboard(clientId)` | `dashboard:client:{id}` |
| `JoinSiteDashboard(clientId, siteId)` | `dashboard:site:{id}` |

#### Servidor → Dashboard (client event)

| Evento | Payload |
|---|---|
| `DashboardEvent` | `(eventType:string, data:object?, timestampUtc:DateTime)` |

> ⚠️ **Proibido:** O servidor NÃO DEVE emitir eventos diretos `AgentStatusChanged`, `CommandCompleted`, ou `AgentHeartbeat` como eventos SignalR independentes. Apenas `DashboardEvent` é o canal válido.

### 3.2 Hub `/hubs/notifications`

| Método | Evento |
|---|---|
| `SubscribeAll`, `SubscribeTopic`, `SubscribeUser`, `SubscribeAgent`, `SubscribeKey` | → Servidor |
| `Unsubscribe*` | → Servidor |
| `NotificationReceived` | ← Cliente |

### 3.3 Hub `/hubs/remote-debug`

| Método | Evento |
|---|---|
| `JoinSession(sessionId)` | → Servidor |
| `LeaveSession(sessionId)` | → Servidor |
| `CloseSession(sessionId, reason?)` | → Servidor |
| `RemoteDebugSessionJoined` | ← Cliente |
| `RemoteDebugLog` | ← Cliente |
| `RemoteDebugSessionEnded` | ← Cliente |

#### 3.3.1 Payloads canônicos do remote-debug

`RemoteDebugSessionJoined`:

- `sessionId: Guid`
- `agentId: Guid`
- `startedAtUtc: DateTime`
- `expiresAtUtc: DateTime`
- `preferredTransport: string`
- `fallbackTransport: string`
- `natsSubject: string`
- `signalRMethod: string`

`RemoteDebugLog`:

- `sessionId: Guid`
- `agentId: Guid`
- `level: string`
- `message: string`
- `timestampUtc: DateTime`
- `sequence: long`
- `transport: string` (`nats` ou `signalr`)

`RemoteDebugSessionEnded`:

- `sessionId: Guid`
- `endedAtUtc: DateTime`
- `reason: string`

### 3.4 Como Enviar Dados via SignalR

Sim. Os mesmos dados operacionais de realtime podem trafegar via SignalR (principalmente no fallback do agent), mantendo o mesmo shape de DTO definido neste contrato.

#### 3.4.1 Envelope de Invocação (JSON Hub Protocol)

Para invocação de método em SignalR, o frame padrão usado é:

```json
{
	"type": 1,
	"target": "NomeDoMetodo",
	"arguments": []
}
```

#### 3.4.2 Agent → Servidor (exemplos)

**RegisterAgent**

```json
{
	"type": 1,
	"target": "RegisterAgent",
	"arguments": [
		"d2719a7d-43bb-4e7e-bbe6-18dce7bf1db7",
		"192.168.1.50"
	]
}
```

**HeartbeatV2**

```json
{
	"type": 1,
	"target": "HeartbeatV2",
	"arguments": [
		{
			"agentId": "d2719a7d-43bb-4e7e-bbe6-18dce7bf1db7",
			"clientId": "11111111-1111-1111-1111-111111111111",
			"siteId": "22222222-2222-2222-2222-222222222222",
			"ipAddress": "192.168.1.50",
			"hostname": "HOMOLOG-WIN-01",
			"agentVersion": "1.0.0",
			"timestampUtc": "2026-05-05T10:00:00Z",
			"cpuPercent": 17.3,
			"memoryPercent": 42.1,
			"memoryTotalGb": 16.0,
			"memoryUsedGb": 6.7,
			"diskPercent": 58.2,
			"diskTotalGb": 512.0,
			"diskUsedGb": 298.0,
			"p2pPeers": 3,
			"uptimeSeconds": 15,
			"processCount": 120
		}
	]
}
```

**CommandResult**

```json
{
	"type": 1,
	"target": "CommandResult",
	"arguments": [
		"9f53ef6d-2f10-4a8e-9fdf-8d579854f1e6",
		0,
		"ok-from-test",
		""
	]
}
```

**SecureHandshakeAsync**

```json
{
	"type": 1,
	"target": "SecureHandshakeAsync",
	"arguments": [
		"AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899"
	]
}
```

**PushRemoteDebugLog**

```json
{
	"type": 1,
	"target": "PushRemoteDebugLog",
	"arguments": [
		"e8ac9fd8-a6b2-4bb8-9ac8-95ce25ca6b3d",
		"info",
		"[agent] heartbeat enviado com sucesso",
		"2026-05-05T10:10:20Z",
		14
	]
}
```

#### 3.4.3 Servidor → Agent (exemplos)

**ExecuteCommand (evento recebido pelo agent)**

```json
{
	"type": 1,
	"target": "ExecuteCommand",
	"arguments": [
		"9f53ef6d-2f10-4a8e-9fdf-8d579854f1e6",
		"powershell",
		"{\"command\":\"Get-Date\",\"timeoutSec\":30}"
	]
}
```

**SyncPing (evento recebido pelo agent)**

```json
{
	"type": 1,
	"target": "SyncPing",
	"arguments": [
		{
			"eventId": "7bb7f3d1-4d6e-48d3-a2f8-41fbe7f26666",
			"agentId": "d2719a7d-43bb-4e7e-bbe6-18dce7bf1db7",
			"eventType": "sync.invalidated",
			"resource": "configuration",
			"scopeType": "site",
			"scopeId": "22222222-2222-2222-2222-222222222222",
			"installationType": "winget",
			"revision": "rev-20260505-001",
			"reason": "policy_changed",
			"changedAtUtc": "2026-05-05T10:02:00Z",
			"correlationId": "corr-555"
		}
	]
}
```

**HandshakeChallenge / HandshakeAck**

```json
{
	"type": 1,
	"target": "HandshakeChallenge",
	"arguments": ["nonce-1", "expected-hash"]
}
```

```json
{
	"type": 1,
	"target": "HandshakeAck",
	"arguments": [true, "ok"]
}
```

#### 3.4.4 Dashboard via SignalR (exemplos)

**JoinDashboard**

```json
{
	"type": 1,
	"target": "JoinDashboard",
	"arguments": []
}
```

**Evento DashboardEvent recebido pelo frontend**

```json
{
	"type": 1,
	"target": "DashboardEvent",
	"arguments": [
		"AgentHeartbeat",
		{
			"agentId": "d2719a7d-43bb-4e7e-bbe6-18dce7bf1db7",
			"status": "Online",
			"hostname": "HOMOLOG-WIN-01",
			"cpuPercent": 17.3,
			"memoryPercent": 42.1,
			"diskPercent": 58.2,
			"processCount": 120
		},
		"2026-05-05T10:00:00Z"
	]
}
```

#### 3.4.5 Envio pelo Servidor (C#) — chamadas recomendadas

```csharp
// Comando para agent (SignalR)
await _hubContext.Clients.Group($"agent-{agentId}")
		.SendAsync("ExecuteCommand", commandId, commandType, payloadJson, cancellationToken);

// Sync ping para agent (SignalR)
await _hubContext.Clients.Group($"agent-{agentId}")
		.SendAsync("SyncPing", ping, cancellationToken);

// Evento para dashboard (SignalR)
await _hubContext.Clients.Group("dashboard:global")
		.SendAsync("DashboardEvent", eventType, data, timestampUtc, cancellationToken);
```

> Observação: em produção, comando e sync ping devem manter fallback para NATS conforme as regras da seção 5.

### 3.5 Contrato de Consumo da Interface Web (Dashboard)

Esta seção descreve o comportamento esperado da interface web para inscrição e consumo dos eventos de realtime.

#### 3.5.1 Fluxo de inscrição esperado

| Ordem | Hub | Ação | Obrigatório | Observação |
|---|---|---|---|---|
| 1 | `/hubs/notifications` | Conectar com usuário autenticado | ✅ | Sem `UserId`, o hub aborta a conexão |
| 2 | `/hubs/notifications` | `SubscribeUser(userId)` | ✅ | Canal principal de notificações por usuário |
| 3 | `/hubs/notifications` | `SubscribeTopic(topic)` | Opcional | Só executar quando `topic` estiver definido |
| 4 | `/hubs/agent` | Conectar | ✅ | Canal de eventos de dashboard |
| 5 | `/hubs/agent` | `JoinDashboard()` | ✅ (visão global) | Entra no grupo `dashboard:global` |
| 6 | `/hubs/agent` | `JoinClientDashboard(clientId)` | Opcional | Para escopo de cliente |
| 7 | `/hubs/agent` | `JoinSiteDashboard(clientId, siteId)` | Opcional | Para escopo de site |

> Quando `topic` vier `undefined`, a ausência de `SubscribeTopic` é comportamento esperado.

#### 3.5.2 Eventos consumidos no frontend

**NotificationReceived** (hub `/hubs/notifications`)

Campos obrigatórios no consumo:

- `id`
- `eventType`
- `topic`
- `severity`
- `title`
- `message`
- `isRead`
- `createdAt`

Campos opcionais:

- `recipientUserId`
- `recipientAgentId`
- `recipientKey`
- `payloadJson`
- `readAt`
- `createdBy`

Observação: o payload emitido pelo servidor corresponde ao `dto` montado em `NotificationService`.

**DashboardEvent** (hub `/hubs/agent`) — **ÚNICO canal de eventos de dashboard (v3.0.0)**

Formato canônico:

- `arguments[0]`: `eventType` (`string`) — enum fechado (6 valores, PascalCase)
- `arguments[1]`: `data` (`object?`) — shape conforme seção 2.8
- `arguments[2]`: `timestampUtc` (`string` ISO-8601 UTC)

O frontend DEVE:

1. Normalizar internamente para `{ eventType, data, timestampUtc }` via `normalizeDashboardEvent()`
2. Rejeitar qualquer evento que não seja `DashboardEvent` (logar `console.warn` com `[CONTRACT_VIOLATION]`)
3. Usar nomes de campo exatos: `agentId` (não `id`/`agentID`), `cpuPercent` (não `cpu`), `hostname` (não `hostName`/`machineName`)
4. Validar `timestampUtc` como ISO-8601 — nunca aceitar `timestamp`
5. NÃO assinar `dashboard.events` diretamente no NATS (se existir código de NATS direto, deve ser removido até 2026-06-01)

**Eventos diretos legados — REMOVIDOS (v2.0.0 / reforçado em v3.0.0)**

Os seguintes eventos NÃO devem mais ter listeners no frontend:

- ❌ `AgentStatusChanged` (evento direto — usar `DashboardEvent` com `eventType = "AgentStatusChanged"`)
- ❌ `CommandCompleted` (evento direto — usar `DashboardEvent` com `eventType = "CommandCompleted"`)
- ❌ `AgentHeartbeat` (evento direto — usar `DashboardEvent` com `eventType = "AgentHeartbeat"`)

#### 3.5.3 Matriz de assinatura "estado alvo"

| Hub | Inscrição | Estado esperado |
|---|---|---|
| `/hubs/notifications` | `SubscribeUser(userId)` | Ativo |
| `/hubs/notifications` | `SubscribeTopic(topic)` | Ativo apenas quando `topic` definido |
| `/hubs/agent` | `JoinDashboard()` | Ativo para feed global |
| `/hubs/agent` | `JoinClientDashboard(clientId)` | Opcional |
| `/hubs/agent` | `JoinSiteDashboard(clientId, siteId)` | Opcional |

#### 3.5.4 Especificação de normalização frontend

```ts
// TIPO CANÔNICO — única fonte de verdade (v3.0.0)
interface DashboardEvent<T = unknown> {
  eventType: string;        // enum fechado (6 valores, PascalCase)
  data: T | null;           // shape conforme seção 2.8
  timestampUtc: string;     // ISO-8601 UTC
}

// Interface exata do heartbeat — 18 campos, sem aliases
interface AgentHeartbeatData {
  agentId: string;          // NÃO aceitar "id" ou "agentID"
  status: string;           // "Online" | "Offline"
  clientId?: string | null;
  siteId?: string | null;
  ipAddress?: string | null;
  hostname?: string | null;  // NÃO aceitar "hostName" ou "machineName"
  agentVersion?: string | null;
  timestampUtc?: string | null;
  cpuPercent?: number | null; // NÃO aceitar "cpu"
  memoryPercent?: number | null;
  memoryTotalGb?: number | null;
  memoryUsedGb?: number | null;
  diskPercent?: number | null;
  diskTotalGb?: number | null;
  diskUsedGb?: number | null;
  p2pPeers?: number | null;
  uptimeSeconds?: number | null;
  processCount?: number | null;
}

// Normalizador central — chamado tanto do listener SignalR quanto de qualquer outro entry point
// Após 2026-06-01, deve ser estrito: rejeitar campos com alias
function normalizeDashboardEvent(rawEvent: unknown): DashboardEvent | null {
  // 1. Validar envelope (eventType, data, timestampUtc)
  // 2. Validar eventType contra enum canônico (6 valores)
  // 3. Validar timestampUtc como ISO-8601
  // 4. Para eventType === 'AgentHeartbeat', validar 18 campos canônicos
  // 5. Retornar null para eventos não-conformes + console.warn('[CONTRACT_VIOLATION]')
}
```

---

## 4. Comandos Especiais

### 4.1 Remote Debug

**Envelope:**
```json
{
  "commandType": "remotedebug",
  "payload": {
    "action": "start|stop",
    "sessionId": "Guid",
    "logLevel": "info|debug|warn|error",
    "expiresAtUtc": "ISO8601",
    "stream": {
      "signalRHub": "/hubs/remote-debug",
      "signalRMethod": "PushRemoteDebugLog",
      "natsSubject": "tenant.{c}.site.{s}.agent.{a}.remote-debug.log",
      "natsWssUrl": "wss://..."
    }
  }
}
```

**Validação de payload:**

| Campo | Obrigatório | Valores |
|---|---|---|
| `action` | ✅ | `"start"` \| `"stop"` |
| `sessionId` | ✅ | Guid |
| `logLevel` | ❌ (default: `"info"`) | `"info"` \| `"debug"` \| `"warn"` \| `"error"` |
| `expiresAtUtc` | ✅ (quando `action = "start"`) | ISO-8601 UTC |
| `stream.signalRHub` | ✅ | `/hubs/remote-debug` |
| `stream.signalRMethod` | ✅ | `PushRemoteDebugLog` |
| `stream.natsSubject` | ✅ | Conforme nomenclatura NATS |
| `stream.natsWssUrl` | ❌ | WebSocket URL |

### 4.2 Alerta PSADT

**Envelope:**
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

**Validação de payload:**

| Campo | Obrigatório | Valores |
|---|---|---|
| `alertId` | ✅ | string não-vazia |
| `type` | ✅ | `"modal"` \| `"toast"` |
| `title` | ✅ | string não-vazia |
| `message` | ✅ | string não-vazia |
| `timeoutSeconds` | ❌ (default: 120) | inteiro > 0 |
| `icon` | ❌ (default: `"info"`) | `"info"` \| `"warning"` \| `"error"` \| `"question"` |
| `actions` | ❌ | array de `{label, value}` |
| `defaultAction` | ❌ | string |

### 4.3 Notificação

**Envelope:**
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

**Validação de payload:**

| Campo | Obrigatório | Valores |
|---|---|---|
| `notificationId` | ✅ | string não-vazia |
| `idempotencyKey` | ✅ | string não-vazia (dedup) |
| `title` | ✅ | string não-vazia |
| `message` | ✅ | string não-vazia (pode ser vazia para `mode = "interactive"`) |
| `mode` | ❌ (default: `"notify_only"`) | `"notify_only"` \| `"interactive"` |
| `severity` | ❌ (default: `"medium"`) | `"low"` \| `"medium"` \| `"high"` \| `"critical"` |
| `eventType` | ✅ | string não-vazia (categoria da notificação) |
| `layout` | ❌ (default: `"toast"`) | `"toast"` \| `"modal"` \| `"banner"` |
| `timeoutSeconds` | ❌ (default: 8) | inteiro > 0 |
| `metadata` | ❌ | object |

### 4.4 Self-Update

**Envelope:**
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

**Validação de payload:**

| Campo | Obrigatório | Valores |
|---|---|---|
| `action` | ✅ | `"check-update"` \| `"install"` \| `"rollback"` |
| `version` | ❌ (obrigatório para `install`/`rollback`) | string semver |
| `url` | ❌ (obrigatório para `install`) | URL HTTPS |

---

## 5. Regras de Integração

| # | Regra |
|---|-------|
| 1 | Notificações ao agent: enviar como `ExecuteCommand(commandType:"notification")`, NÃO como `NotificationReceived` |
| 2 | Envio de comando: tentar NATS `.command` primeiro, fallback SignalR `ExecuteCommand` |
| 3 | Envio de sync ping: tentar NATS `.sync.ping` primeiro; se houver sessão SignalR ativa, também enviar `SyncPing` |
| 4 | Remote Debug NATS: usar subject `remote-debug.log` (hífen + `.log`) |
| 5 | Dashboard Events: NATS → NatsSignalRBridge → SignalR `DashboardEvent`. O Dashboard NUNCA assina NATS diretamente. |
| 6 | Validação na borda: cada componente valida mensagens recebidas — nunca confiar no emissor |
| 7 | Log de violação: toda mensagem fora do contrato gera `[CONTRACT_VIOLATION] component=X field=Y expected=Z received=W source=<nats|signalr>` |

---

## 6. Especificações de Correção por Componente

### 6.1 Agent (Go) — `C:\Projetos\Discovery`

| # | Arquivo | Mudança | Prioridade | Validação |
|---|---|---|---|---|
| 6.1.1 | `runtime_nats.go` — publish `dashboard.events` | `timestamp` → `timestampUtc` no envelope raiz | 🔴 P0 | Toda mensagem em `dashboard.events` usa `timestampUtc` |
| 6.1.2 | `runtime_nats.go` — eventType | `agent_connected` → `AgentConnected`, `agent_disconnected` → `AgentDisconnected` | 🔴 P0 | `eventType` PascalCase, valor da lista canônica |
| 6.1.3 | `runtime_nats.go` — dashboard events | **Remover** publish de `command_result` em `dashboard.events` | 🟡 P1 | `.result` já processado pelo servidor → `CommandCompleted` |
| 6.1.4 | `runtime_nats.go` — envelope | Promover `clientId` e `siteId` para nível raiz (fora de `data`) | 🔴 P0 | Envelope tem `clientId`/`siteId` irmãos de `eventType`, `data`, `timestampUtc` |
| 6.1.5 | `runtime_nats.go` | Adicionar validação de saída: `eventType` deve ser PascalCase da lista canônica | 🟡 P1 | Log de warning se `eventType` não reconhecido |
| 6.1.6 | `runtime_nats.go` — heartbeat | Verificar que `processCount` está sendo enviado no heartbeat | 🟢 P2 | Campo `processCount` presente no payload |

### 6.2 Servidor (C#) — `C:\Projetos\DiscoveryRMM_API`

| # | Arquivo | Mudança | Prioridade | Validação |
|---|---|---|---|---|
| 6.2.1 | `NatsSignalRBridge.cs` | Adicionar validação de envelope com log `[CONTRACT_VIOLATION]` | 🔴 P0 | Log estruturado com `agentId`, `eventType`, campo violado |
| 6.2.2 | `NatsSignalRBridge.cs` | Normalização temporária: `timestamp` → `timestampUtc` com warning | 🔴 P0 | Remover após 2026-06-01 |
| 6.2.3 | `NatsSignalRBridge.cs` | Validar `eventType` contra enum canônico (6 valores) | 🟡 P1 | Descartar `eventType` não reconhecidos |
| 6.2.4 | `NatsSignalRBridge.cs` | Fallback temporário: extrair `clientId`/`siteId` de `data` se ausentes no envelope | 🟡 P1 | Remover após 2026-06-01 |
| 6.2.5 | `AgentHub.cs` | Confirmar que **não** há `SendAsync` de eventos diretos (`AgentStatusChanged`, `CommandCompleted`, `AgentHeartbeat`) | 🔴 P0 | Grep por `SendAsync.*AgentStatusChanged\|CommandCompleted\|AgentHeartbeat` exceto `DashboardEvent` |
| 6.2.6 | `AgentHub.cs` | `OnConnectedAsync`: validar que `RegisterAgent` foi chamado antes de aceitar heartbeats | 🟢 P2 | Rejeitar heartbeats de conexões não registradas |

### 6.3 Dashboard (Frontend) — `C:\Projetos\DiscoveryRMM_Site`

| # | Componente | Mudança | Prioridade | Validação |
|---|---|---|---|---|
| 6.3.1 | SignalR listeners | **Remover** listeners de eventos diretos: `AgentStatusChanged`, `CommandCompleted`, `AgentHeartbeat` | 🔴 P0 | Apenas `DashboardEvent` tem listener ativo em `/hubs/agent` |
| 6.3.2 | Event normalizer | Criar função `normalizeDashboardEvent(rawEvent) → DashboardEvent \| null` | 🔴 P0 | Unificar parsing NATS + SignalR |
| 6.3.3 | Heartbeat parser | Remover aliases: aceitar apenas `agentId` (não `id`/`agentID`), `cpuPercent` (não `cpu`), `hostname` (não `hostName`/`machineName`) | 🔴 P0 | Tipagem TypeScript estrita com os 18 campos exatos |
| 6.3.4 | Tipos TypeScript | Alinhar `AgentHeartbeatData` com 18 campos canônicos (adicionar `processCount`) | 🟡 P1 | Interface completa conforme 3.5.4 |
| 6.3.5 | `timestampUtc` | Aceitar **apenas** `timestampUtc`; remover fallback `timestamp`/`timeStamp` | 🟡 P1 | ISO-8601 parsing estrito |
| 6.3.6 | NATS listener | Reutilizar `normalizeDashboardEvent()` — não duplicar lógica de parsing. **Remover assinatura NATS direta até 2026-06-01.** | 🔴 P0 | Um único caminho de código: SignalR `DashboardEvent` |
| 6.3.7 | Console warnings | `console.warn('[CONTRACT_VIOLATION]', detalhes)` para qualquer mensagem não-conforme | 🟡 P1 | Detecta vazamentos de eventos legados durante transição |
| 6.3.8 | Fluxo de conexão | Garantir ordem: notifications → `SubscribeUser` → agent → `JoinDashboard` | 🟢 P2 | Verificar via console.log |

---

## 7. Regras de Validação Obrigatória

### 7.1 Validação de `timestampUtc`

| Regra | Ação em violação |
|---|---|
| Campo deve ser nomeado `timestampUtc` (não `timestamp`) | `[CONTRACT_VIOLATION] field=timestampUtc expected='timestampUtc' received='timestamp'` → normalizar com warning (transitório) |
| Valor deve ser ISO-8601 UTC (`2026-05-05T10:00:00Z`) | `[CONTRACT_VIOLATION] field=timestampUtc expected='ISO-8601' received='<valor>'` → descartar (ou `default(DateTime)`) |
| Deve estar no envelope raiz do `DashboardEvent` | `[CONTRACT_VIOLATION] field=timestampUtc expected='present' received='missing'` → descartar evento |

### 7.2 Validação de `eventType`

| Regra | Ação em violação |
|---|---|
| Deve ser PascalCase (não snake_case) | `[CONTRACT_VIOLATION] field=eventType expected='PascalCase' received='<valor>'` → normalizar com warning (transitório: `agent_connected` → `AgentConnected`) |
| Deve ser um dos 6 valores canônicos | `[CONTRACT_VIOLATION] field=eventType expected='enum(6)' received='<valor>'` → descartar evento |
| Não duplicar `command_result` — servidor já emite `CommandCompleted` | `[CONTRACT_VIOLATION] field=eventType received='command_result'` → descartar (coberto por `.result`) |

### 7.3 Validação de Envelope

| Regra | Ação em violação |
|---|---|
| `clientId` e `siteId` devem estar no nível raiz | `[CONTRACT_VIOLATION] field=envelope.clientId expected='root_level' received='data_only'` → extrair de `data` com warning (transitório) |
| `agentId` deve estar presente em heartbeats | `[CONTRACT_VIOLATION] field=data.agentId expected='Guid' received='missing'` → descartar evento |
| `commandId` deve estar presente em resultados de comando | `[CONTRACT_VIOLATION] field=data.commandId expected='Guid' received='missing'` → descartar evento |

### 7.4 Validação de Integridade

| Regra | Ação em violação |
|---|---|
| Heartbeat sem `agentId` → inválido | Descartar |
| `AgentStatusChanged` sem `status` → inválido | Descartar |
| `CommandCompleted` sem `commandId` → inválido | Descartar |
| `AgentConnected` sem `transport` → aceitar com warning | Warning apenas |

### 7.5 Validação de Campos com Aliases (NOVA — v3.0.0)

| Regra | Período de transição (até 2026-06-01) | Após 2026-06-01 |
|---|---|---|
| Campo `id` ou `agentID` em vez de `agentId` | Normalizar para `agentId` + `[CONTRACT_VIOLATION]` warning | Rejeitar — campo `agentId` é obrigatório |
| Campo `timestamp` ou `timeStamp` em vez de `timestampUtc` | Normalizar para `timestampUtc` + warning | Rejeitar |
| Campo `cpu` em vez de `cpuPercent` | Normalizar + warning | Rejeitar |
| Campo `hostName` ou `machineName` em vez de `hostname` | Normalizar + warning | Rejeitar |
| Demais aliases (ver Seção 2.10) | Normalizar + warning | Rejeitar |

### 7.6 Modelo de Log de Violação

```
[CONTRACT_VIOLATION] component=<Agent|Server|Dashboard> field=<nome> expected=<valor> received=<valor> source=<nats|signalr>
```

Exemplos:
```
[CONTRACT_VIOLATION] component=Server field=timestampUtc expected='timestampUtc' received='timestamp' source=nats
[CONTRACT_VIOLATION] component=Server field=eventType expected='PascalCase' received='agent_connected' source=nats
[CONTRACT_VIOLATION] component=Dashboard field=agentId expected='agentId' received='id' source=signalr
[CONTRACT_VIOLATION] component=Dashboard field=hostname expected='hostname' received='hostName' source=signalr
```

---

## 8. Plano de Convergência em 3 Fases (NOVO — v3.0.0)

### Fase 1 — Baseline Documentado (Imediato — maio/2026)

**Objetivo:** Nenhum comportamento alterado, mas **toda violação é visível** em logs.

| Ação | Responsável | Artefato |
|---|---|---|
| Publicar contrato v3.0.0 com tabelas de equivalência (Seções 2.10 e 2.11) | Servidor | `CONTRATO_COMUNICACAO_REALTIME.md` |
| Adicionar `console.warn('[CONTRACT_VIOLATION]')` no Dashboard para aliases | Frontend | `normalizeDashboardEvent()` |
| Adicionar logging `[CONTRACT_VIOLATION]` no Agent ao publicar eventos | Agent (Go) | `runtime_nats.go` |
| Mapear todas as divergências como issues no backlog | Todos | GitHub Issues |

**Marco:** Logs limpos de surpresas — toda violação é conhecida e rastreada.

### Fase 2 — Correção do Agent (Prazo: 2026-05-20)

**Objetivo:** Agent publica 100% conforme contrato. Bridge pode começar a remover fallbacks.

| # | Ação | Arquivo | Validação |
|---|---|---|---|
| 1 | `timestamp` → `timestampUtc` | `runtime_nats.go` | Grep no agente por `"timestamp"` (não `timestampUtc`) retorna 0 |
| 2 | `agent_connected` → `AgentConnected` | `runtime_nats.go` | Idem para snake_case |
| 3 | `agent_disconnected` → `AgentDisconnected` | `runtime_nats.go` | Idem |
| 4 | Remover `command_result` de `dashboard.events` | `runtime_nats.go` | Código removido ou comentado |
| 5 | `clientId`/`siteId` na raiz do envelope | `runtime_nats.go` | JSON Schema validation |
| 6 | Validação de saída contra enum canônico | `runtime_nats.go` | Log se desconhecido |

**Marco:** Zero `[CONTRACT_VIOLATION]` originados do agent.

### Fase 3 — Correção do Dashboard + Hardening (Prazo: 2026-06-01)

**Objetivo:** Sistema 100% conforme. Toda mensagem não-conforme é rejeitada na borda.

| # | Ação | Arquivo/Componente | Validação |
|---|---|---|---|
| 1 | Remover listeners de eventos diretos | `signalR.ts` / hooks | `AgentStatusChanged`, `CommandCompleted`, `AgentHeartbeat` sem listeners |
| 2 | Remover assinatura NATS direta | `natsService.ts` | Dashboard não conecta ao NATS |
| 3 | `normalizeDashboardEvent()` estrito | `dashboardEvents.ts` | Rejeita aliases, loga `[CONTRACT_VIOLATION]` |
| 4 | Tipos TypeScript canônicos | `types/dashboard.ts` | 18 campos exatos, sem unions flexíveis |
| 5 | Remover fallbacks de normalização da bridge | `NatsSignalRBridge.cs` | Código `// REMOVE_AFTER_2026-06-01` deletado |
| 6 | Bridge rejeita (não normaliza) eventos não-conformes | `NatsSignalRBridge.cs` | `[CONTRACT_VIOLATION]` como Error, evento descartado |

**Marco:** Sistema 100% conforme. Toda mensagem não-conforme é rejeitada na borda.

---

## 9. Checklist de Conformidade

### 9.1 Agent (Go)

| # | Item | Status |
|---|---|---|
| A1 | Heartbeat envia `timestampUtc` (não `timestamp`) | ⬜ |
| A2 | Dashboard events usam `eventType` PascalCase (`AgentConnected`/`AgentDisconnected`) | ⬜ |
| A3 | `command_result` NÃO é publicado em `dashboard.events` | ⬜ |
| A4 | `clientId` e `siteId` estão no envelope raiz | ⬜ |
| A5 | `eventType` de saída validado contra enum canônico | ⬜ |
| A6 | Heartbeat inclui `processCount` | ⬜ |
| A7 | Todos os 18 campos do heartbeat são enviados | ⬜ |
| A8 | `HeartbeatV2` SignalR usa o mesmo DTO do NATS | ⬜ |
| A9 | Log `[CONTRACT_VIOLATION]` implementado na borda de saída | ⬜ |

### 9.2 Servidor (C#)

| # | Item | Status |
|---|---|---|
| S1 | `NatsSignalRBridge` valida envelope e loga `[CONTRACT_VIOLATION]` | ⬜ |
| S2 | Normalização temporária de `timestamp` → `timestampUtc` ativa | ⬜ |
| S3 | Normalização temporária de `eventType` snake_case → PascalCase ativa | ⬜ |
| S4 | Fallback temporário de `clientId`/`siteId` de `data` ativo | ⬜ |
| S5 | `eventType` não reconhecidos são descartados | ⬜ |
| S6 | `AgentHub` NÃO emite eventos diretos legados | ⬜ |
| S7 | `DashboardEvent` é o único evento enviado ao dashboard | ⬜ |
| S8 | Heartbeat sem `RegisterAgent` prévio é rejeitado | ⬜ |
| S9 | `NotificationReceived` NÃO é enviado ao grupo `agent-{id}` | ⬜ |
| S10 | Código de normalização temporária tem flag `// REMOVE_AFTER_2026-06-01` | ⬜ |
| S11 | Normalização de aliases de campos (Seção 2.10) ativa com warning | ⬜ |

### 9.3 Dashboard (Frontend)

| # | Item | Status |
|---|---|---|
| D1 | Listeners de eventos diretos (`AgentStatusChanged`, `CommandCompleted`, `AgentHeartbeat`) removidos | ⬜ |
| D2 | Função `normalizeDashboardEvent()` implementada e usada em todos os entry points | ⬜ |
| D3 | Aliases removidos: só `agentId`, `cpuPercent`, `hostname` | ⬜ |
| D4 | Interface `AgentHeartbeatData` com 18 campos canônicos | ⬜ |
| D5 | Apenas `timestampUtc` aceito (sem fallback) | ⬜ |
| D6 | NATS listener **removido** — único entry point é SignalR `DashboardEvent` | ⬜ |
| D7 | `console.warn('[CONTRACT_VIOLATION]')` para mensagens não-conformes | ⬜ |
| D8 | Fluxo de conexão segue ordem: notifications → subscribeUser → agent → joinDashboard | ⬜ |
| D9 | Dashboard NÃO depende de conexão NATS direta (apenas SignalR) | ⬜ |
| D10 | Eventos com `eventType` não reconhecido são ignorados com warn | ⬜ |
| D11 | Tipos TypeScript refletem exatamente as tabelas da Seção 2.8 | ⬜ |
| D12 | Testes de integração cobrem o ciclo completo: heartbeat → DashboardEvent → UI update | ⬜ |

---

## 10. Resumo das Correções no Servidor (v1.4.0)

| # | Arquivo | Mudança |
|---|---|---|
| 1 | `src/Discovery.Core/DTOs/AgentHeartbeat.cs` | Adicionado `int? ProcessCount = null` |
| 2 | `src/Discovery.Api/Services/NotificationService.cs` | Removido `NotificationReceived` do grupo `agent-{id}` |
| 3 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | Seção 2.8 adicionada — payloads canônicos unificados NATS + SignalR (única fonte de verdade) |
| 4 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 3.5.2 alinhado ao emissor real (`AgentHub.PublishHeartbeatToDashboardAsync` e `PublishAgentStatusChangedAsync`) |
| 5 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 2.7 reescrita: agent reconhecido como publisher de `dashboard.events` + divergências de shape mapeadas |
| 6 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 2.8.6 adicionada — `AgentConnected` / `AgentDisconnected` (auditoria do agent) |
| 7 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 2.9 adicionada — `.hardware` documentado como subject auxiliar |
| 8 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | Princípios 12 e 13 adicionados |
| 9 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 3.1 alinhado ao runtime |
| 10 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 3.3.1 adicionada — payloads remote-debug |
| 11 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | Seção 5 corrigida — fluxo real de dispatch |

---

## 11. Ações pendentes no agent (Go)

| # | Arquivo (referência) | Ação | Prioridade |
|---|---|---|---|
| 1 | `runtime_nats.go` (publish em `dashboard.events`) | Renomear `timestamp` → `timestampUtc` | 🔴 P0 |
| 2 | `runtime_nats.go` | Renomear `eventType`: `agent_connected`→`AgentConnected`, `agent_disconnected`→`AgentDisconnected` | 🔴 P0 |
| 3 | `runtime_nats.go` | Remover publish de `command_result` em `dashboard.events` | 🟡 P1 |
| 4 | `runtime_nats.go` | Promover `clientId` e `siteId` para o envelope (além de `data`) | 🔴 P0 |

---

## 12. Período de Transição

| Fase | Período | Comportamento do Servidor |
|---|---|---|
| **Transição** | 2026-05-05 → 2026-06-01 | Aceitar ambos os formatos. Logar `[CONTRACT_VIOLATION]` como **warning**. Normalizar automaticamente (`timestamp`→`timestampUtc`, snake_case→PascalCase, `clientId`/`siteId` de `data`→envelope, aliases de campos conforme Seção 2.10). |
| **Endurecimento** | Após 2026-06-01 | Rejeitar mensagens não-conformes. `[CONTRACT_VIOLATION]` vira **error** e o evento é descartado. Remover código de normalização temporária. |

### Fallbacks temporários (remover após 2026-06-01)

| Código | Descrição |
|---|---|
| `NormalizeTimestamp()` | Se `timestamp` presente e `timestampUtc` ausente, copiar valor com warning |
| `NormalizeEventType()` | Se snake_case, converter para PascalCase (`agent_connected` → `AgentConnected`) com warning |
| `ExtractTenantFromData()` | Se `clientId`/`siteId` ausentes no envelope, extrair de `data` com warning |
| `NormalizeFieldAliases()` | Se campo legado presente (ex: `cpu`) e canônico ausente (`cpuPercent`), copiar com warning (ver Seção 2.10) |

---

## 13. Arquitetura Alvo — Fluxo Único de Eventos (NOVO — v3.0.0)

```
Agent (Go)                    Servidor (C#)                   Dashboard (TS)
    │                             │                                │
    │── heartbeat ────────────────►│                                │
    │   (NATS .heartbeat)         │── DashboardEvent ─────────────►│
    │                             │   (SignalR /hubs/agent)        │
    │                             │                                │
    │── dashboard.events ────────►│                                │
    │   (NATS, AgentConnected)    │── DashboardEvent ─────────────►│
    │                             │   (SignalR /hubs/agent)        │
    │                             │                                │
    │   ⚠️ NUNCA direto ao       │                                │
    │   Dashboard (sem bypass)    │   O Dashboard NÃO assina       │
    │                             │   NATS diretamente             │
```

**Regra de ouro:** O Dashboard tem **um único entry point** para eventos de agentes: `DashboardEvent` no SignalR `/hubs/agent`. Qualquer outro caminho (NATS direto, eventos SignalR diretos) é **proibido** e será removido.

### Matriz de Canais de Entrega — Definitiva

| Evento | NATS Subject | SignalR Hub | Quem Publica | Quem Consome |
|---|---|---|---|---|
| `AgentHeartbeat` | `tenant.{c}.site.{s}.dashboard.events` | `/hubs/agent` → `DashboardEvent` | Servidor | Dashboard (via SignalR) |
| `AgentStatusChanged` | `tenant.{c}.site.{s}.dashboard.events` | `/hubs/agent` → `DashboardEvent` | Servidor | Dashboard (via SignalR) |
| `CommandCompleted` | `tenant.{c}.site.{s}.dashboard.events` | `/hubs/agent` → `DashboardEvent` | Servidor | Dashboard (via SignalR) |
| `AgentHardwareReported` | `tenant.{c}.site.{s}.dashboard.events` | `/hubs/agent` → `DashboardEvent` | Servidor | Dashboard (via SignalR) |
| `AgentConnected` | `tenant.{c}.site.{s}.dashboard.events` | `/hubs/agent` → `DashboardEvent` | Agent (Go) | Dashboard (via SignalR) |
| `AgentDisconnected` | `tenant.{c}.site.{s}.dashboard.events` | `/hubs/agent` → `DashboardEvent` | Agent (Go) | Dashboard (via SignalR) |

### Matriz de Subjects NATS — Completa

| Subject | Direção | Payload | Usado por |
|---|---|---|---|
| `tenant.{c}.site.{s}.agent.{a}.heartbeat` | Agent → Servidor | `AgentHeartbeat` (17 campos) | Agent, Servidor |
| `tenant.{c}.site.{s}.agent.{a}.command` | Servidor → Agent | `{commandId, commandType, payload}` | Servidor, Agent |
| `tenant.{c}.site.{s}.agent.{a}.result` | Agent → Servidor | `{commandId, exitCode, output, errorMessage}` | Agent, Servidor |
| `tenant.{c}.site.{s}.agent.{a}.sync.ping` | Servidor → Agent | `SyncInvalidationPingMessage` | Servidor, Agent |
| `tenant.{c}.site.{s}.agent.{a}.remote-debug.log` | Agent → Servidor | `{sessionId, agentId, message, level, timestampUtc, sequence}` | Agent, Servidor |
| `tenant.{c}.site.{s}.agent.{a}.hardware` | Agent → Servidor | Hardware report (legado/compatibilidade) | Agent (opcional), Servidor |
| `tenant.{c}.site.{s}.p2p.discovery` | Servidor → Agent | `{sequence, ttlSeconds, peers[]}` | Servidor, Agent |
| `tenant.{c}.site.{s}.dashboard.events` | Agent/Servidor → Bridge | `DashboardEventEnvelope` | Agent, Servidor, Bridge |
| `tenant.unscoped.dashboard.events` | Servidor → Bridge | `DashboardEventEnvelope` (sem tenant) | Servidor, Bridge |

### Matriz de Métodos SignalR — Agent ↔ Servidor (completa)

| Direção | Método | Parâmetros (nome canônico C#) | Apelido Agent (Go) |
|---|---|---|---|
| Agent → Servidor | `RegisterAgent` | `agentId:Guid, ipAddress:string?` | — |
| Agent → Servidor | `Heartbeat` | `agentId:Guid, ipAddress:string?` (legado) | — |
| Agent → Servidor | `HeartbeatV2` | `heartbeat:AgentHeartbeat` | — |
| Agent → Servidor | `CommandResult` | `commandId:Guid, exitCode:int, output:string?, errorMessage:string?` | `cmdId`, `errText` |
| Agent → Servidor | `SecureHandshakeAsync` | `observedTlsHash:string` | — |
| Agent → Servidor | `PushRemoteDebugLog` | `sessionId:Guid, level:string?, message:string?, timestampUtc:DateTime?, sequence:long?` | — |
| Servidor → Agent | `ExecuteCommand` | `commandId:Guid, commandType:string, payload:string` | `cmdId`, `cmdType` |
| Servidor → Agent | `SyncPing` | `ping:SyncInvalidationPingMessage` | — |
| Servidor → Agent | `HandshakeChallenge` | `nonce:string, expectedTlsHash:string` | `expectedHash` |
| Servidor → Agent | `HandshakeAck` | `success:bool, message:string` | — |
| Servidor → Dashboard | `DashboardEvent` | `eventType:string, data:object?, timestampUtc:DateTime` | — |

---

## 14. Plano de Ação Imediato (Próximos Passos) (NOVO — v3.0.0)

### 🔴 Bloqueantes (precisam ser feitos antes de qualquer outra coisa)

| # | Ação | Quem | Esforço | Seção |
|---|---|---|---|---|
| 1 | **Agent: corrigir `timestamp` → `timestampUtc`** | Agent (Go) | 15 min | 6.1.1 |
| 2 | **Agent: corrigir `eventType` snake_case → PascalCase** | Agent (Go) | 10 min | 6.1.2 |
| 3 | **Agent: promover `clientId`/`siteId` para raiz do envelope** | Agent (Go) | 15 min | 6.1.4 |
| 4 | **Agent: remover `command_result` de `dashboard.events`** | Agent (Go) | 5 min | 6.1.3 |

### 🟡 Alta prioridade

| # | Ação | Quem | Esforço | Seção |
|---|---|---|---|---|
| 5 | **Dashboard: criar `normalizeDashboardEvent()` estrito** | Frontend | 2h | 6.3.2 |
| 6 | **Dashboard: remover listeners de eventos diretos** | Frontend | 30 min | 6.3.1 |
| 7 | **Dashboard: remover aliases dos tipos TypeScript** | Frontend | 1h | 6.3.3 |
| 8 | **Servidor: adicionar validação `[CONTRACT_VIOLATION]` na bridge** | Servidor | 1h | 6.2.1 |

### 🟢 Média prioridade

| # | Ação | Quem | Esforço | Seção |
|---|---|---|---|---|
| 9 | **Dashboard: remover assinatura NATS direta** | Frontend | 2h | 6.3.6 |
| 10 | **Servidor: `AgentHub` — garantir que não emite eventos diretos** | Servidor | 30 min | 6.2.5 |
| 11 | **Servidor: rejeitar heartbeat sem `RegisterAgent` prévio** | Servidor | 1h | 6.2.6 |
| 12 | **Testes de integração: ciclo completo heartbeat → DashboardEvent → UI** | QA/Todos | 4h | 9.3 (D12) |

---

## 15. Recomendações Finais (NOVO — v3.0.0)

1. **Não adie a correção do agent.** As 4 correções bloqueantes (itens 1–4 da Seção 14) podem ser feitas em menos de 1 hora e eliminam a maioria dos `[CONTRACT_VIOLATION]`. Sem elas, após 2026-06-01 o sistema quebra.

2. **Trate o frontend com cuidado.** O dashboard em produção é flexível demais (aceita aliases, escuta múltiplos canais). A correção deve ser feita em sincronia com o servidor — se o servidor parar de emitir eventos diretos antes do frontend estar pronto, a UI congela.

3. **Mantenha o contrato como código.** Sugiro adicionar validação automatizada (JSON Schema ou TypeScript types gerados) que rode na CI. Se uma mensagem não conforma, o build falha.

4. **A bridge é o ponto de controle.** `NatsSignalRBridge` é o componente mais crítico — é onde a normalização acontece e onde as violações são detectadas. Invista em testes unitários para ela.

5. **Documente o estado real.** Este documento (v3.0.0) documenta tanto o estado desejado quanto o estado atual e o caminho entre eles. As Seções 2.10, 2.11 e 8 são o mapa de migração.

6. **Sincronize as releases.** A Fase 3 (Dashboard + Hardening) deve ser deployada no mesmo dia que o endurecimento do servidor (2026-06-01). Combine uma janela de deploy conjunto para evitar quebras.

---

> **Fim do documento. Versão 3.0.0 — 2026-05-05.**
> **Próxima revisão prevista:** 2026-06-01 (pós-endurecimento, para remover seções de transição e declarar contrato como estável).
```

---

Documento completo gerado. Principais adições em relação ao v2.0.0:

- **Seção 0** (Princípio Zero — Single Source of Truth)
- **Seção 2.10** (Tabela Canônica de Equivalência de Campos — 15 aliases mapeados com prazo de remoção)
- **Seção 2.11** (Tabela de Equivalência de eventType — legado → canônico)
- **Seção 7.5** (Validação de Campos com Aliases — comportamento na transição vs pós-endurecimento)
- **Seção 8** (Plano de Convergência em 3 Fases — com marcos e responsáveis)
- **Seção 13** (Arquitetura Alvo — Fluxo Único de Eventos, Matriz de Canais, Matriz NATS completa, Matriz SignalR completa)
- **Seção 14** (Plano de Ação Imediato — 12 itens com esforço estimado)
- **Seção 15** (Recomendações Finais — 6 pontos)
- **Princípios 19 e 20** (Dashboard entry point único + bridge como ponto de controle)
- **Checklist expandido** (A9, S11, D6 atualizado)