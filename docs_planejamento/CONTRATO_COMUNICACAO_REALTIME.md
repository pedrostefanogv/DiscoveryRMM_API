# 📐 Contrato de Comunicação em Tempo Real — DiscoveryRMM API

> **Versão:** 1.4.0 | **Data:** 2026-05-05
> **Público:** Servidor (C#), Agent (Go), Frontend (JS)
> **Propósito:** Contrato canônico de integração — todos os componentes devem seguir este documento.

---

## 1. Princípios

| # | Regra |
|---|-------|
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

#### 2.7.1 Envelope canônico (único aceito — v1.3.0+)

| Campo | Tipo | Obrigatório | Observação |
|---|---|---|---|
| `eventType` | `string` | ✅ | PascalCase — ver tabela 2.7.2 |
| `data` | `object?` | ❌ | shape conforme seção 2.8 |
| `timestampUtc` | `DateTime` | ✅ | ISO-8601 UTC — **não usar `timestamp`** |
| `clientId` | `Guid?` | ❌ | usado pela bridge para rotear `dashboard:client:{id}` |
| `siteId` | `Guid?` | ❌ | usado pela bridge para rotear `dashboard:site:{id}` |

#### 2.7.2 `eventType` canônicos

| `eventType` | Origem | Quando | Payload |
|---|---|---|---|
| `AgentHeartbeat` | Servidor | Ao processar heartbeat (NATS ou SignalR) | [2.8.1](#281-agentheartbeat) |
| `AgentStatusChanged` | Servidor | Agent ficou Online ou Offline | [2.8.2](#282-agentstatuschanged) |
| `CommandCompleted` | Servidor | Ao processar resultado de comando | [2.8.3](#283-commandcompleted) |
| `AgentHardwareReported` | Servidor | Ao processar coleta de hardware | [2.8.4](#284-agenthardwarereported) |
| `AgentConnected` | Agent | Auditoria de conexão NATS | [2.8.6](#286-agentconnected--agentdisconnected-auditoria-do-agent) |
| `AgentDisconnected` | Agent | Auditoria de desconexão NATS | [2.8.6](#286-agentconnected--agentdisconnected-auditoria-do-agent) |

#### 2.7.3 Divergências atuais do agent (alinhar)

O agent (Go) hoje publica neste subject usando shape **não conforme**. Pontos a corrigir no agent:

| Item | Agent atual | Padrão exigido |
|---|---|---|
| Campo de tempo | `timestamp` | `timestampUtc` |
| `eventType` | `agent_connected`, `agent_disconnected`, `command_result` | `AgentConnected`, `AgentDisconnected` (e não duplicar `command_result` — já coberto pelo `.result` + `CommandCompleted` do servidor) |
| `clientId`/`siteId` | apenas dentro de `data` | também no envelope (necessário para rotear grupos no SignalR) |

> Enquanto a correção do agent não for feita, mensagens com `timestamp` são desserializadas pela bridge com `TimestampUtc = default(DateTime)` e não roteadas por client/site — evento ainda chega ao grupo global, mas com timestamp inválido.

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
  eventType: string;        // arguments[0]
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

---

## 3. SignalR — Contrato de Hubs

### 3.1 Hub `/hubs/agent`

> SignalR é posicional: nomes de argumento do servidor (C#) são **definitivos**. Apelidos usados internamente pelo agent (`cmdId`, `cmdType`, `errText`) são aceitáveis em código cliente, mas o contrato canonical usa os nomes da assinatura C#: `commandId`, `commandType`, `payload`, `errorMessage`.

#### Agent → Servidor (invoke)

| Método | Parâmetros |
|---|---|
| `RegisterAgent` | `agentId:Guid, ipAddress:string?` |
| `Heartbeat` | `agentId:Guid, ipAddress:string?` (legado, sem métricas) |
| `HeartbeatV2` | `heartbeat:AgentHeartbeat` |
| `CommandResult` | `commandId:Guid, exitCode:int, output:string?, errorMessage:string?` |
| `SecureHandshakeAsync` | `observedTlsHash:string` |
| `PushRemoteDebugLog` | `sessionId:Guid, level:string?, message:string?, timestampUtc:DateTime?, sequence:long?` |

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
			"status": "Online"
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

**AgentStatusChanged** (hub `/hubs/agent`)

- Evento canônico: `DashboardEvent` com `eventType = "AgentStatusChanged"`.
- `data`: ver seção [2.8.2](#282-agentstatuschanged).
- Frontend deve descartar formatos legados (eventos diretos `AgentStatusChanged` fora do envelope `DashboardEvent`).

**CommandCompleted** (hub `/hubs/agent`)

- Evento canônico: `DashboardEvent` com `eventType = "CommandCompleted"`.
- `data`: ver seção [2.8.3](#283-commandcompleted).
- Frontend deve descartar formatos legados (eventos diretos `CommandCompleted` fora do envelope `DashboardEvent`).

**AgentHeartbeat** (hub `/hubs/agent`)

- Evento canônico: `DashboardEvent` com `eventType = "AgentHeartbeat"`.
- `data`: ver seção [2.8.1](#281-agentheartbeat) (contém **todos** os campos do DTO + `status`, `clientId`, `siteId`).
- Campo mínimo para identificação: `agentId`.
- Frontend deve descartar formatos legados (eventos diretos `AgentHeartbeat` fora do envelope `DashboardEvent`).

**DashboardEvent** (hub `/hubs/agent`)

Formato canônico — único formato aceito a partir da v1.2.0:

- `arguments[0]`: `eventType` (`string`)
- `arguments[1]`: `data` (`object?` — shape conforme seção 2.8)
- `arguments[2]`: `timestampUtc` (`string` ISO-8601 UTC)

O frontend deve normalizar internamente para `{ eventType, data, timestampUtc }` (ver tipo `DashboardEvent<T>` em [2.8.5](#285-envelope-final-entregue-ao-frontend)).

#### 3.5.3 Matriz de assinatura "estado atual"

| Hub | Inscrição | Estado esperado |
|---|---|---|
| `/hubs/notifications` | `SubscribeUser(userId)` | Ativo |
| `/hubs/notifications` | `SubscribeTopic(topic)` | Ativo apenas quando `topic` definido |
| `/hubs/agent` | `JoinDashboard()` | Ativo para feed global |
| `/hubs/agent` | `JoinClientDashboard(clientId)` | Opcional |
| `/hubs/agent` | `JoinSiteDashboard(clientId, siteId)` | Opcional |

#### 3.5.4 Recomendação de alinhamento frontend

Para evitar divergência de contratos:

1. `DashboardEvent` é o **único** canal para eventos de dashboard — eventos diretos com nome igual a `eventType` (ex.: `AgentStatusChanged` solto) são considerados legado e devem ser ignorados.
2. Normalizar internamente para `DashboardEvent<T>` da seção [2.8.5](#285-envelope-final-entregue-ao-frontend).
3. Tipos do `data` devem refletir as tabelas da seção 2.8 (única fonte de verdade).

---

## 4. Comandos Especiais

### 4.1 Remote Debug
```json
{"commandType":"remotedebug","payload":{"action":"start|stop","sessionId":"Guid","logLevel":"info","expiresAtUtc":"ISO8601","stream":{"signalRHub":"/hubs/remote-debug","signalRMethod":"PushRemoteDebugLog","natsSubject":"tenant.{c}.site.{s}.agent.{a}.remote-debug.log","natsWssUrl":"wss://..."}}}
```

### 4.2 Alerta PSADT
```json
{"commandType":"showpsadtalert","payload":{"alertId":"string","type":"modal|toast","title":"string","message":"string","timeoutSeconds":120,"icon":"info|warning|error|question","actions":[{"label":"Sim","value":"yes"}],"defaultAction":"no"}}
```

### 4.3 Notificação
```json
{"commandType":"notification","payload":{"notificationId":"string","idempotencyKey":"string","title":"string","message":"string","mode":"notify_only|interactive","severity":"low|medium|high|critical","eventType":"string","layout":"toast|modal|banner","timeoutSeconds":8,"metadata":{}}}
```

### 4.4 Self-Update
```json
{"commandType":"update","payload":{"action":"check-update"}}
```

---

## 5. Regras de Integração

| # | Regra |
|---|-------|
| 1 | Notificações ao agent: enviar como `ExecuteCommand(commandType:"notification")`, NÃO como `NotificationReceived` |
| 2 | Envio de comando: tentar NATS `.command` primeiro, fallback SignalR `ExecuteCommand` |
| 3 | Envio de sync ping: tentar NATS `.sync.ping` primeiro; se houver sessão SignalR ativa, também enviar `SyncPing` |
| 4 | Remote Debug NATS: usar subject `remote-debug.log` (hífen + `.log`) |
| 5 | Dashboard Events: NATS → NatsSignalRBridge → SignalR `DashboardEvent` |

---

## 6. Resumo das Correções no Servidor (v1.4.0)

| # | Arquivo | Mudança |
|---|---|---|
| 1 | `src/Discovery.Core/DTOs/AgentHeartbeat.cs` | Adicionado `int? ProcessCount = null` |
| 2 | `src/Discovery.Api/Services/NotificationService.cs` | Removido `NotificationReceived` do grupo `agent-{id}` |
| 3 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | Seção 2.8 adicionada — payloads canônicos unificados NATS + SignalR (única fonte de verdade) |
| 4 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 3.5.2 alinhado ao emissor real (`AgentHub.PublishHeartbeatToDashboardAsync` e `PublishAgentStatusChangedAsync`) |
| 5 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 2.7 reescrita: agent reconhecido como publisher de `dashboard.events` + divergências de shape mapeadas (timestamp→timestampUtc, snake_case→PascalCase, clientId/siteId no envelope) |
| 6 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 2.8.6 adicionada — `AgentConnected` / `AgentDisconnected` (auditoria do agent) |
| 7 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 2.9 adicionada — `.hardware` documentado como subject auxiliar com ingestão ativa no servidor |
| 8 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | Princípios 12 e 13 adicionados (timestampUtc obrigatório, eventType em PascalCase) |
| 9 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 3.1 alinhado ao runtime: `Heartbeat` legado documentado e `ExecuteCommand.payload` como string |
| 10 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | 3.3.1 adicionada com payloads canônicos de `RemoteDebugSessionJoined`, `RemoteDebugLog` e `RemoteDebugSessionEnded` |
| 11 | `docs_planejamento/CONTRATO_COMUNICACAO_REALTIME.md` | Seção 5 corrigida para fluxo real de dispatch: NATS primeiro para comando/sync ping |

## 7. Ações pendentes no agent (Go)

| # | Arquivo (referência) | Ação |
|---|---|---|
| 1 | `runtime_nats.go` (publish em `dashboard.events`) | Renomear `timestamp` → `timestampUtc` |
| 2 | `runtime_nats.go` | Renomear `eventType`: `agent_connected`→`AgentConnected`, `agent_disconnected`→`AgentDisconnected` |
| 3 | `runtime_nats.go` | Remover publish de `command_result` em `dashboard.events` (já coberto por `.result` + servidor emite `CommandCompleted`) |
| 4 | `runtime_nats.go` | Promover `clientId` e `siteId` para o envelope (além de `data`) |