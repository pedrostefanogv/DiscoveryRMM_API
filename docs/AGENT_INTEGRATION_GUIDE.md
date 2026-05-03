# Guia de Integração do Agent Discovery RMM

**Última atualização:** 03/05/2026

---

## Sumário

1. [Fluxo de Inicialização do Agent](#1-fluxo-de-inicialização-do-agent)
2. [Endpoints HTTP](#2-endpoints-http)
   - 2.1 [Registro (agent-install)](#21-registro-agent-install)
   - 2.2 [Configuração (agent-auth/me/configuration)](#22-configuração)
   - 2.3 [Bootstrap P2P (agent-auth/me/p2p/bootstrap)](#23-bootstrap-p2p)
   - 2.4 [Credenciais NATS (agent-auth/me/nats-credentials)](#24-credenciais-nats)
   - 2.5 [Sync Manifest (agent-auth/me/sync-manifest)](#25-sync-manifest)
3. [Autenticação](#3-autenticação)
4. [SignalR AgentHub](#4-signalr)
5. [NATS — Subjects e ACLs](#5-nats)
   - 5.1 [Subjects publicados pelo agent](#51-publish-agent)
   - 5.2 [Subjects assinados pelo agent](#52-subscribe-agent)
   - 5.3 [Discovery P2P por Site](#53-discovery)
6. [Formatos das Mensagens](#6-formatos)
7. [Tabela de Resumo dos Canais](#7-resumo)

---

## 1. Fluxo de Inicialização do Agent

```
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
 │◄──── { natsServerHost, natsUseWssExternal, siteId, clientId,  │
 │         p2pFilesEnabled, agentHeartbeatIntervalSeconds, ... }   │
 │                                    │                            │
 ├── 4. POST /api/v1/agent-auth/me/nats-credentials ──────────►  │
 │     Headers: Authorization: Bearer mdz_{token}                  │
 │              X-Agent-ID: {agentId}                              │
 │◄──── { jwt, nkeySeed, publicKey, expiresAtUtc,                │
 │         publishSubjects[], subscribeSubjects[] }                │
 │                                    │                            │
 ├── 5. Connect NATS ─────────────────────────────────────────────►│
 │     URL: nats://host:4222  (nats/internal)                     │
 │     ou: wss://host:443    (wss/external)                       │
 │     Auth Callback: mdz_{token}                                  │
 │                                    │                            │
 │◄──── JWT com ACLs ────────────────────────────────────────────┤
 │                                    │                            │
 ├── 6. Reconnect NATS WITH JWT ──────────────────────────────────►│
 │                                    │                            │
 ├── 7. SUB tenant.{c}.site.{s}.p2p.discovery ◄──────────────────┤
 │     (recebe snapshot de peers do site)                         │
 │                                    │                            │
 ├── 8. Post /api/v1/agent-auth/me/p2p/bootstrap ──────────►     │
 │     Body: { agentId, peerId, addrs[], port }                   │
 │◄──── { peers: [ { peerId, addrs[], port } ] }                 │
 │                                    │                            │
 ├── 9. PUB heartbeat (a cada N seg) ────────────────────────────►│
 │     tenant.{c}.site.{s}.agent.{a}.heartbeat                    │
 │                                    │                            │
 ├── 10. Connect SignalR AgentHub ───►                             │
 │      wss://host/hubs/agent?access_token=mdz_{token}             │
 │      Invoke RegisterAgent(agentId, ipAddress)                   │
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
| agentId | string | sim | UUID do agent (deve coincidir com X-Agent-ID) |
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
- Este endpoint **também dispara** a publicação do snapshot de discovery via NATS (veja seção 5.3).
- O `agentId` enviado no body é **ignorado** — a autenticação real vem do header/token.

**Códigos de Erro:**
| Status | Significado |
|--------|-------------|
| 401 | Token ausente, inválido, sem X-Agent-ID, ou X-Agent-ID mismatch |
| 400 | X-Agent-ID em formato inválido |
| 403 | Agent em zero-touch pending |
| 404 | Agent não encontrado ou site não encontrado |

### 2.4 Credenciais NATS

**Endpoint:** `POST /api/v1/agent-auth/me/nats-credentials`

**Headers:**
```http
Authorization: Bearer mdz_{token}
X-Agent-ID: {agentId}
```

**Resposta 200:**
```json
{
  "jwt": "eyJhbGciOiJ...JWT_do_NATS",
  "nkeySeed": "SUAPTOZXGMFD...",
  "publicKey": "UCZGK5X...",
  "expiresAtUtc": "2026-05-03T13:34:56Z",
  "publishSubjects": [
    "tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat",
    "tenant.{clientId}.site.{siteId}.agent.{agentId}.result",
    "tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware",
    "tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log"
  ],
  "subscribeSubjects": [
    "tenant.{clientId}.site.{siteId}.agent.{agentId}.command",
    "tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping",
    "tenant.{clientId}.site.{siteId}.p2p.discovery"
  ]
}
```

### 2.5 Sync Manifest

**Endpoint:** `GET /api/v1/agent-auth/me/sync-manifest`

**Headers:**
```http
Authorization: Bearer mdz_{token}
X-Agent-ID: {agentId}
```

**Resposta 200:** Lista de recursos que o agent deve sync periodicamente (ex.: políticas de automação, scripts, catálogo de apps).

---

## 3. Autenticação

Todos os endpoints `/api/v1/agent-auth/*` exigem:

1. **Header `Authorization: Bearer mdz_{token}`** — token do agent (obtido no registro)
2. **Header `X-Agent-ID: {agentId}`** — UUID do agent, validado contra o token

O middleware rejeita:
- Token ausente ou mal formatado → 401
- Token inválido/expirado → 401
- X-Agent-ID ausente → 401
- X-Agent-ID em formato inválido → 400
- X-Agent-ID não corresponde ao token → 401

**(Opcional) TLS Hash Validation:** Quando habilitado, a emissão de NATS credentials exige o header `X-Agent-Tls-Cert-Hash` com o fingerprint TLS observado pelo agent.

---

## 4. SignalR AgentHub

**Hub URL:** `/hubs/agent?access_token=mdz_{token}`

### Handshake Inicial (OnConnectedAsync)
Se `Security:AgentConnection:HandshakeEnabled` estiver ativo:
1. Servidor envia `HandshakeChallenge(nonce, expectedTlsHash)`
2. Agent responde invocando `SecureHandshakeAsync(agentObservedTlsHash)`
3. Servidor valida o TLS hash e responde `HandshakeAck(success, message)`

Se desabilitado, a conexão já pode chamar métodos imediatamente.

### Métodos que o Agent INVOCA (client → server)

| Método | Parâmetros | Frequência | Descrição |
|--------|-----------|-----------|-----------|
| `RegisterAgent(agentId, ipAddress)` | `Guid agentId`, `string? ipAddress` | Ao conectar | Registra agent online, dispara envio de comandos pendentes |
| `Heartbeat(agentId, ipAddress)` | `Guid agentId`, `string? ipAddress` | A cada N segundos (configurável) | Mantém status online (cache em Redis com write-behind) |
| `CommandResult(commandId, exitCode, output, errorMessage)` | `Guid commandId`, `int exitCode`, `string? output`, `string? errorMessage` | Ao concluir comando | Reporta execução de comando |
| `PushRemoteDebugLog(sessionId, level, message, timestampUtc, sequence)` | `Guid sessionId`, `string? level`, `string? message`, `DateTime? timestampUtc`, `long? sequence` | Durante sessão debug | Stream de logs de debug remoto |
| `SecureHandshakeAsync(agentObservedTlsHash)` | `string agentObservedTlsHash` | Uma vez (se habilitado) | Handshake secundário anti-MITM |

### Eventos que o Agent ESCUTA (server → client)

| Evento | Payload | Descrição |
|--------|---------|-----------|
| `ExecuteCommand` | `(Guid commandId, string commandType, string payload)` | Comando a ser executado pelo agent |
| `HandshakeChallenge` | `(string nonce, string expectedTlsHash)` | Desafio do handshake secundário |
| `HandshakeAck` | `(bool success, string message)` | Confirmação do handshake |

---

## 5. NATS — Subjects e ACLs

### 5.1 Subjects que o AGENT PUBLICA

```
tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat
tenant.{clientId}.site.{siteId}.agent.{agentId}.result
tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware
tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log
```

### 5.2 Subjects que o AGENT ASSINA

```
tenant.{clientId}.site.{siteId}.agent.{agentId}.command
tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping
tenant.{clientId}.site.{siteId}.p2p.discovery    ← NOVO
```

### 5.3 Discovery P2P por Site

**Subject:** `tenant.{clientId}.site.{siteId}.p2p.discovery`

**Quem publica:** Apenas o servidor (Discovery API)
**Quem assina:** Todos os agents do site (via JWT ACL)
**Formato:** JSON serializado com `P2pDiscoverySnapshot`

**Exemplo de mensagem recebida no subject:**
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
    },
    {
      "agentId": "3f9a...",
      "peerId": "12D3KooWPeerB",
      "addrs": ["10.10.2.14"],
      "port": 41081,
      "lastHeartbeatAtUtc": "2026-05-03T12:34:30.000Z"
    }
  ]
}
```

**O que o agent deve fazer com essa mensagem:**
1. Ignorar eventos com `sequence` menor que a última recebida (fora de ordem).
2. Filtrar localmente o próprio `agentId` da lista de peers.
3. Armazenar em cache local por `ttlSeconds`.
4. Conectar-se aos peers (libp2p) usando `peerId`, `addrs` e `port`.
5. Atualizar heartbeat para peers conectados e iniciar sync de artifacts.

**Quando o snapshot é publicado:**
- Após cada chamada ao `POST .../p2p/bootstrap` (com debounce de 1,5s por site).
- Debounce coalesce múltiplas chamadas no mesmo site em um único publish.
- Publish é pulado se o snapshot estiver inalterado (hash SHA256 comparado).

---

## 6. Formatos das Mensagens

### Heartbeat (NATS)

Agent → Servidor em `tenant.{c}.site.{s}.agent.{a}.heartbeat`:
```json
{
  "ipAddress": "192.168.1.50",
  "agentVersion": "1.0.0"
}
```

### Command Result (NATS ou SignalR)

Agent → Servidor:
```json
{
  "commandId": "uuid",
  "exitCode": 0,
  "output": "Success",
  "errorMessage": null
}
```

### Comando Recebido (SignalR)

Servidor → Agent via evento `ExecuteCommand`:
```csharp
// Assinatura do evento:
// (Guid commandId, string commandType, string payload)

// Exemplo recebido:
{
  "commandId": "uuid",
  "commandType": "PowerShell",
  "payload": "{ ... }"
}
```

### Hardware Report (NATS)

Agent → Servidor em `tenant.{c}.site.{s}.agent.{a}.hardware`:
```json
{
  "components": [ ... ],
  "collectedAt": "2026-05-03T12:00:00Z"
}
```

---

## 7. Tabela de Resumo dos Canais

| Canal | URL | Auth | Propósito |
|-------|-----|------|-----------|
| HTTP API | `https://{host}/api/v1/...` | Bearer mdz_ + X-Agent-ID | Registro, config, bootstrap, credentials, sync |
| NATS TCP | `nats://{host}:4222` | Auth Callback mdz_ → JWT | Mensageria real-time (heartbeat, result, hardware, discovery) |
| NATS WSS | `wss://{host}:443` | Auth Callback mdz_ → JWT | Conexão externa (agents fora da LAN) |
| SignalR | `wss://{host}/hubs/agent?access_token=mdz_...` | Query param | Comandos, heartbeat alternativo, remote debug |

---

## Ajustes Necessários no Agent

1. **Incluir subject de discovery na assinatura NATS** — o agent deve subscrever `tenant.{clientId}.site.{siteId}.p2p.discovery` após conectar com JWT.
2. **Processar mensagens de discovery** — ao receber snapshot, extrair peers, filtrar próprio agentId, iniciar conexões libp2p.
3. **Tratar `sequence`** — ignorar snapshots com sequence menor que a última recebida.
4. **Usar `ttlSeconds`** para invalidar cache local de peers.
5. **Manter compatibilidade** — continuar chamando `POST /p2p/bootstrap` periodicamente (environment, heartbeat inicial).
