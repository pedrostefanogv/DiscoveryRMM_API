# Guia de Integração do Agent Discovery RMM

**Última atualização:** 05/05/2026

---

## Fontes Canônicas

- [CONTRATO_COMUNICACAO_REALTIME.md](../docs_nats_planejamento/CONTRATO_COMUNICACAO_REALTIME.md)
- [NATS_SUBJECTS_ACL.md](../docs_nats_planejamento/NATS_SUBJECTS_ACL.md)

---

## Sumário

1. [Fluxo de Inicialização do Agent](#1-fluxo-de-inicialização-do-agent)
2. [Endpoints HTTP](#2-endpoints-http)
   - 2.1 [Registro (agent-install)](#21-registro-agent-install)
   - 2.2 [Configuração (agent-auth/me/configuration)](#22-configuração)
   - 2.3 [Bootstrap P2P (agent-auth/me/p2p/bootstrap)](#23-bootstrap-p2p)
   - 2.4 [Sync Manifest (agent-auth/me/sync-manifest)](#24-sync-manifest)
3. [Autenticação](#3-autenticação)
4. [NATS — Subjects e ACLs](#4-nats--subjects-e-acls)
   - 4.1 [Subjects publicados pelo agent](#41-subjects-publicados-pelo-agent)
   - 4.2 [Subjects assinados pelo agent](#42-subjects-assinados-pelo-agent)
   - 4.3 [Comando em massa (fan-out)](#43-comando-em-massa-fan-out)
   - 4.4 [Global Pong (liveness do servidor)](#44-global-pong-liveness-do-servidor)
   - 4.5 [Discovery P2P por Site](#45-discovery-p2p-por-site)
5. [Formatos das Mensagens](#5-formatos-das-mensagens)
6. [Tabela de Resumo dos Canais](#6-tabela-de-resumo-dos-canais)
7. [Checklist de Conformidade do Agent](#7-checklist-de-conformidade-do-agent)

---

## 1. Fluxo de Inicialização do Agent

```text
Agent                          Discovery API                  NATS Server
 │                                    │                            │
 ├── 1. GET /api/v1/agent-install/    │                            │
 │     (obter deploy token)           │                            │
 │◄───────────────────────────────────┤                            │
 │                                    │                            │
 ├── 2. POST /api/v1/agent-install/register  ──────────►          │
 │     Authorization: Bearer {deploy_token}                       │
 │     Body: { hostname, displayName, ... }                       │
 │◄──── { agentId, token: "mdz_...", clientId, siteId }          │
 │                                    │                            │
 ├── 3. GET /api/v1/agent-auth/me/configuration  ──────────►     │
 │     Headers: Authorization: Bearer mdz_{token}                  │
 │              X-Agent-ID: {agentId}                              │
 │◄──── { natsServerHost, natsUseWssExternal, natsAuthMode, ... }│
 │                                    │                            │
 ├── 4. Connect NATS ─────────────────────────────────────────────►│
 │     URL: nats://host:4222 (interno) ou wss://host:443/nats (externo)│
 │     Auth: token mdz_{token} via auth callout                    │
 │◄──── JWT de autorização com ACL dinâmica ─────────────────────┤
 │                                    │                            │
 ├── 5. SUB subjects do AgentIdentity ◄───────────────────────────┤
 │      - tenant.{c}.site.{s}.agent.{a}.command                   │
 │      - tenant.{c}.site.{s}.agents.command                      │
 │      - tenant.{c}.agents.command                               │
 │      - tenant.global.agents.command                            │
 │      - tenant.global.pong                                      │
 │      - tenant.{c}.site.{s}.agent.{a}.sync.ping                 │
 │      - tenant.{c}.site.{s}.p2p.discovery                       │
 │                                    │                            │
 ├── 6. POST /api/v1/agent-auth/me/p2p/bootstrap ──────────►     │
 │     Body: { peerId, addrs[], port }                            │
 │◄──── { peers: [ { peerId, addrs[], port } ] }                  │
 │                                    │                            │
 ├── 7. PUB heartbeat/result/hardware/remote-debug.log ─────────►│
 │                                    │                            │
 ├── 8. Processar fan-out + global pong ◄────────────────────────┤
 │     (idempotência por dispatchId/idempotencyKey + liveness)    │
 ```

---

## 2. Endpoints HTTP

### 2.1 Registro (agent-install)

**Endpoint:** `POST /api/v1/agent-install/register`

**Headers:**
```http
Authorization: Bearer {deploy_token}
Content-Type: application/json
```

**Request Body:**
```json
{
	"hostname": "AGENT-01",
	"displayName": "Servidor Principal",
	"operatingSystem": "windows",
	"osVersion": "10.0.19045",
	"agentVersion": "1.0.0",
	"macAddress": "00:1A:2B:3C:4D:5E"
}
```

**Resposta 200:**
```json
{
	"agentId": "f57f6f1b-2f97-4d55-a6ad-1a95e6c12abc",
	"clientId": "a1b2c3d4-...",
	"siteId": "e5f6a7b8-...",
	"token": "mdz_a1b2c3d4e5f6...",
	"tokenId": "uuid-do-token",
	"expiresAt": "2026-05-13T12:00:00Z"
}
```

### 2.2 Configuração

**Endpoint:** `GET /api/v1/agent-auth/me/configuration`

**Headers:**
```http
Authorization: Bearer mdz_{token}
X-Agent-ID: {agentId}
```

**Resposta 200 (trecho relevante):**
```json
{
	"recoveryEnabled": true,
	"discoveryEnabled": true,
	"p2pFilesEnabled": false,
	"siteId": "e5f6a7b8-...",
	"clientId": "a1b2c3d4-...",
	"natsServerHost": "nats.discoveryrmm.com",
	"natsUseWssExternal": true,
	"natsAuthMode": "agent_token",
	"natsAuthCalloutEnabled": true,
	"agentHeartbeatIntervalSeconds": 30,
	"agentOnlineGraceSeconds": 120,
	"enforceTlsHashValidation": false,
	"handshakeEnabled": false,
	"apiTlsCertHash": null,
	"natsTlsCertHash": null
}
```

### 2.3 Bootstrap P2P

**Endpoint:** `POST /api/v1/agent-auth/me/p2p/bootstrap`

**Headers:**
```http
Authorization: Bearer mdz_{token}
X-Agent-ID: {agentId}
Content-Type: application/json
```

**Request Body:**
```json
{
	"agentId": "f57f6f1b-2f97-4d55-a6ad-1a95e6c12abc",
	"peerId": "12D3KooWAbCdEfGhIjKlMnOpQrStUvWxYz",
	"addrs": ["192.168.1.50", "10.0.0.12"],
	"port": 41080
}
```

**Campos:**
| Campo | Tipo | Obrigatório | Descrição |
|-------|------|-------------|-----------|
| agentId | string | sim | UUID do agent (deve coincidir com X-Agent-ID; valor do body é ignorado na decisão final) |
| peerId | string | sim | Peer ID libp2p (máx. 128 chars) |
| addrs | string[] | sim | IPs IPv4 roteáveis do host (sem porta) |
| port | int | sim | Porta TCP/QUIC libp2p (range típico: 41080–41120) |

**Resposta 200:**
```json
{
	"peers": [
		{
			"peerId": "12D3KooWPeerA",
			"addrs": ["192.168.1.51"],
			"port": 41080
		},
		{
			"peerId": "12D3KooWPeerB",
			"addrs": ["10.0.0.22"],
			"port": 41081
		}
	]
}
```

**Observações:**
- Até **3 peers** retornados (aleatórios, mesmo clientId, exclui o próprio agent, online nos últimos 10 min).
- Resposta vazia se não houver peers disponíveis.
- Este endpoint também dispara publicação de snapshot de discovery via NATS (com debounce por site).

### 2.4 Sync Manifest

**Endpoint:** `GET /api/v1/agent-auth/me/sync-manifest`

**Headers:**
```http
Authorization: Bearer mdz_{token}
X-Agent-ID: {agentId}
```

**Resposta 200:** Lista de recursos que o agent deve sincronizar periodicamente (ex.: políticas de automação, scripts, catálogo de apps).

---

## 3. Autenticação

Todos os endpoints `/api/v1/agent-auth/*` exigem:

1. **Header `Authorization: Bearer mdz_{token}`** — token do agent obtido no registro.
2. **Header `X-Agent-ID: {agentId}`** — UUID do agent, validado contra o token.

O middleware rejeita:
- Token ausente ou mal formatado → 401
- Token inválido/expirado → 401
- X-Agent-ID ausente → 401
- X-Agent-ID em formato inválido → 400
- X-Agent-ID não corresponde ao token → 401

**Descontinuação de legado:**
- O endpoint `POST /api/v1/agent-auth/me/nats-credentials` foi removido.
- O agent não deve tentar obter JWT/NKey por endpoint dedicado.
- O fluxo válido é `natsAuthMode = agent_token` com auth callout do broker.

---

## 4. NATS — Subjects e ACLs

### 4.1 Subjects publicados pelo agent

```text
tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat
tenant.{clientId}.site.{siteId}.agent.{agentId}.result
tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware
tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log
```

### 4.2 Subjects assinados pelo agent

```text
tenant.{clientId}.site.{siteId}.agent.{agentId}.command
tenant.{clientId}.site.{siteId}.agents.command
tenant.{clientId}.agents.command
tenant.global.agents.command
tenant.global.pong
tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping
tenant.{clientId}.site.{siteId}.p2p.discovery
```

### 4.3 Comando em massa (fan-out)

**Subjects canônicos de dispatch:**
- Site: `tenant.{clientId}.site.{siteId}.agents.command`
- Cliente: `tenant.{clientId}.agents.command`
- Global: `tenant.global.agents.command`

**Regras operacionais:**
1. Mensagem fan-out deve conter `dispatchId` e `idempotencyKey`.
2. O agent deve deduplicar por `dispatchId`/`idempotencyKey` em janela TTL.
3. O agent deve ignorar dispatch expirado (`expiresAtUtc`).
4. Resultado de execução deve incluir `dispatchId` em `.result` para consolidação no servidor.

### 4.4 Global Pong (liveness do servidor)

**Subject:** `tenant.global.pong`

**Uso no agent:**
1. Tratar como sinal de disponibilidade do servidor.
2. Não executar ação destrutiva ao receber pong.
3. Se `serverOverloaded=true`, aplicar backoff em tráfego não essencial.

### 4.5 Discovery P2P por Site

**Subject:** `tenant.{clientId}.site.{siteId}.p2p.discovery`

**Quem publica:** Servidor (Discovery API)  
**Quem assina:** Agents do site (via claims do AgentIdentity)

**O que o agent deve fazer ao receber snapshot:**
1. Ignorar `sequence` menor que a última recebida.
2. Filtrar localmente o próprio `agentId` da lista de peers.
3. Armazenar em cache por `ttlSeconds`.
4. Conectar aos peers usando `peerId`, `addrs` e `port`.

---

## 5. Formatos das Mensagens

### Heartbeat (Agent → Servidor)

Subject: `tenant.{c}.site.{s}.agent.{a}.heartbeat`

```json
{
	"agentId": "uuid",
	"clientId": "uuid",
	"siteId": "uuid",
	"hostname": "AGENT-01",
	"ipAddress": "192.168.1.50",
	"agentVersion": "1.0.0",
	"timestampUtc": "2026-05-05T10:00:00Z",
	"cpuPercent": 17.3,
	"memoryPercent": 42.1,
	"diskPercent": 58.2,
	"processCount": 120
}
```

### Command Dispatch (Servidor → Agent)

Unicast: `tenant.{c}.site.{s}.agent.{a}.command`  
Fan-out: `tenant.{c}.site.{s}.agents.command` | `tenant.{c}.agents.command` | `tenant.global.agents.command`

```json
{
	"dispatchId": "guid",
	"commandId": "guid",
	"commandType": "string",
	"targetScope": "agent|site|client|global",
	"targetClientId": "guid|null",
	"targetSiteId": "guid|null",
	"issuedAtUtc": "2026-05-05T10:00:00Z",
	"expiresAtUtc": "2026-05-05T10:30:00Z",
	"idempotencyKey": "string",
	"payload": "json-string"
}
```

### Command Result (Agent → Servidor)

Subject: `tenant.{c}.site.{s}.agent.{a}.result`

```json
{
	"dispatchId": "guid",
	"commandId": "guid",
	"agentId": "guid",
	"exitCode": 0,
	"output": "Success",
	"errorMessage": null
}
```

### Global Pong (Servidor → Agent)

Subject: `tenant.global.pong`

```json
{
	"eventType": "pong",
	"serverTimeUtc": "2026-05-05T10:00:00Z",
	"serverOverloaded": false
}
```

### P2P Discovery Snapshot (Servidor → Agent)

Subject: `tenant.{c}.site.{s}.p2p.discovery`

```json
{
	"version": 1,
	"clientId": "a1b2c3d4-...",
	"siteId": "e5f6a7b8-...",
	"generatedAtUtc": "2026-05-03T12:34:56.000Z",
	"ttlSeconds": 120,
	"sequence": 184,
	"peers": [
		{
			"agentId": "2d8e...",
			"peerId": "12D3KooWPeerA",
			"addrs": ["192.168.1.51", "10.0.0.22"],
			"port": 41080,
			"lastHeartbeatAtUtc": "2026-05-03T12:34:40.000Z"
		}
	]
}
```

---

## 6. Tabela de Resumo dos Canais

| Canal | URL | Auth | Propósito |
|-------|-----|------|----------|
| HTTP API | `https://{host}/api/v1/...` | Bearer mdz_ + X-Agent-ID | Registro, configuração, bootstrap P2P e sync manifest |
| NATS TCP | `nats://{host}:4222` | Token mdz_ + auth callout | Mensageria real-time nativa (heartbeat, command, result, hardware, discovery, remote-debug, global pong) |
| NATS WSS | `wss://{host}:443/nats` | Token mdz_ + auth callout | Conexão externa via WebSocket NATS para agentes fora da LAN |

---

## 7. Checklist de Conformidade do Agent

1. Não chamar endpoint legado de credenciais NATS.
2. Conectar ao broker com token mdz_ (modo `agent_token`).
3. Assinar os 7 subjects de subscribe do AgentIdentity (incluindo fan-out e `tenant.global.pong`).
4. Publicar apenas os 4 subjects permitidos (`heartbeat`, `result`, `hardware`, `remote-debug.log`).
5. Deduplicar comandos fan-out por `dispatchId`/`idempotencyKey`.
6. Incluir `dispatchId` em `.result` quando comando for de campanha.
7. Tratar `serverOverloaded` do global pong com backoff de tráfego não essencial.
8. Processar `p2p.discovery` com controle de `sequence` e `ttlSeconds`.

