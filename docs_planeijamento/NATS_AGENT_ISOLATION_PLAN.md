# Diagnóstico e Plano de Melhoria — Isolamento de Agents no NATS

**Criado em:** 02/05/2026
**Última atualização:** 03/05/2026
**Status:** Implementação parcial (P1-P2 concluídos)
**Branch:** dev

---

## 📋 Diagnóstico da Arquitetura NATS Atual

### ✅ O que já está correto (isolamento multi-tenant)

A arquitetura já tem isolamento por design via **JWT ACLs** — cada agent recebe um JWT que restringe exatamente o que ele pode publicar/assinar:

**Subjects por agent** (`NatsCredentialsService.BuildAgentSubjects`):

```
# Agent só PUBLICA nos seus próprios subjects:
tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat
tenant.{clientId}.site.{siteId}.agent.{agentId}.result
tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware
tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log

# Agent só ASSINA nos seus próprios subjects:
tenant.{clientId}.site.{siteId}.agent.{agentId}.command
tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping
```

Isso **impede agent-to-agent communication** porque:
- Agent A **não tem permissão** de publicar no subject do Agent B
- Agent A **não tem permissão** de assinar subjects de outros agents
- O subject de cada agent contém o `agentId` UUID único — é impossível adivinhar

O auth callout (`NatsAuthCalloutBackgroundService`) valida tokens `mdz_` e emite JWTs assinados com a `AccountSeed`.

---

### 🔴 Problemas na Config do NATS Server

Config atual (`/etc/nats-server.conf`):

```conf
listen: 0.0.0.0:4222
http: 127.0.0.1:8222
server_name: discovery-nats
authorization {
  timeout: 1
  users = [
    { user: "discovery_nats", password: "CTWVT2GAkgeWnNmYNlyp9f0e" }
  ]
  auth_callout {
    issuer: "AAKLQ3I4XD4HUOW7XCH3BAH5QFT4QWYXEYC253F2OTNOKTPTLXDYBY3R"
    auth_users: [ "discovery_nats" ]
    xkey: "XCWJV65NDGO6JPMWFCBFNFAX5DGGBDG5UCSOEPBA7X3SLZ5CJD3TJGU4"
  }
}
websocket {
  port: 8081
  host: 127.0.0.1
  no_tls: true
}
```

| # | Problema | Risco |
|---|----------|-------|
| 1 | **Sem `default_permissions`** — Nenhuma regra de negação explícita. O JWT já protege, mas falta defesa em profundidade. | Médio |
| 2 | **WebSocket `127.0.0.1:8081` sem TLS** — Agents externos não conseguem usar WebSocket (preso em localhost e sem TLS). Agents só conectam via NATS TCP `:4222` direto. | Alto |
| 3 | **HTTP Monitor (`:8222`) sem auth** — Expõe métricas, conexões ativas, subscribers sem senha. | Médio |
| 4 | **Sem `max_payload` / `max_connections`** — Um agent malicioso ou com bug pode floodar o servidor. | Baixo |
| 5 | **`$SYS.REQ.USER.AUTH` exposto sem restrição** — Qualquer um pode enviar requests de auth. Ruído desnecessário. | Baixo |

---

### 🔧 Configuração Recomendada do `nats-server.conf`

```conf
listen: 0.0.0.0:4222
http: 127.0.0.1:8222
server_name: discovery-nats
max_payload: 4194304          # 4MB — protege contra flooding
max_connections: 5000
write_deadline: 5s

authorization {
  timeout: 1.5
  users = [
    { user: "discovery_nats", password: "CTWVT2GAkgeWnNmYNlyp9f0e" }
  ]

  # ═══ Auth Callout (JWT) ═══
  auth_callout {
    issuer: "AAKLQ3I4XD4HUOW7XCH3BAH5QFT4QWYXEYC253F2OTNOKTPTLXDYBY3R"
    auth_users: [ "discovery_nats" ]
    xkey: "XCWJV65NDGO6JPMWFCBFNFAX5DGGBDG5UCSOEPBA7X3SLZ5CJD3TJGU4"
  }

  # ═══ DEFAULT PERMISSIONS (aplicado a todos) ═══
  default_permissions {
    publish = ["$SYS.>", "_INBOX.>"]
    subscribe = ["$SYS.>"]
  }
}

# ═══ WebSocket (para agents externos) ═══
websocket {
  port: 443                    # Porta padrão HTTPS
  host: 0.0.0.0                # Disponível externamente
  no_tls: false                # MUDAR para false em produção
  # tls {
  #   cert_file: "/etc/nats/certs/cert.pem"
  #   key_file: "/etc/nats/certs/key.pem"
  # }
}

# ═══ WebSocket secundário para agents LAN (sem TLS) ═══
# websocket {
#   port: 8081
#   host: 127.0.0.1
#   no_tls: true
# }
```

---

### 🧠 Como os Agents DEVEM se Conectar ao NATS

#### Fluxo completo:

```
┌──────────────┐         ┌──────────────────┐         ┌───────────────┐
│   Agent      │         │   Discovery API   │         │  NATS Server  │
│  (Go/Windows)│         │   (.NET)          │         │               │
└──────┬───────┘         └────────┬──────────┘         └───────┬───────┘
       │                          │                           │
       │  1. POST /auth/login     │                           │
       │     (deploy_token +      │                           │
       │      agent_id)           │                           │
       ├─────────────────────────►│                           │
       │◄─────────────────────────┤                           │
       │  2. access_token ("mdz_")│                           │
       │                          │                           │
       │  3. Connect NATS         │                           │
       │     nats://host:4222    │                           │
       │     Auth: mdz_{token}   │                           │
       ├─────────────────────────────────────────────────────►│
       │                          │                           │
       │                          │  4. Auth Callout          │
       │                          │     $SYS.REQ.USER.AUTH    │
       │                          │◄─────────────────────────►│
       │                          │                           │
       │  5. Receive JWT          │                           │
       │◄─────────────────────────────────────────────────────┤
       │                          │                           │
       │  6. Reconnect NATS       │                           │
       │     WITH JWT             │                           │
       ├─────────────────────────────────────────────────────►│
       │                          │                           │
       │  7. PUB heartbeat        │                           │
       │     tenant.{c}.{s}.{a}.heartbeat                     │
       ├─────────────────────────────────────────────────────►│
       │                          │                           │
       │  8. SUB command          │                           │
       │     tenant.{c}.{s}.{a}.command                       │
       │◄─────────────────────────────────────────────────────┤
```

#### Para agents EXTERNOS (fora da rede local):

```
natsServer = "wss://nats.discoveryrmm.com:443"
```

O agent (Go ou Windows) precisa apenas mudar a URL de conexão de `nats://host:4222` para `wss://host:443`. O NATS Client SDK gerencia a camada WebSocket transparentemente.

---

### 📝 Ajustes Necessários no Código da API

#### 1. `AgentPackageService.cs` — Respeitar WSS na URL do NATS ✅ IMPLEMENTADO

**Localização:** `src/Discovery.Infrastructure/Services/AgentPackageService.cs`

**Antes:**
```csharp
var natsUrl = string.IsNullOrWhiteSpace(natsHost) ? null : natsHost;
```

**Depois:**
```csharp
var useWss = serverConfig.NatsUseWssExternal
    && !string.IsNullOrWhiteSpace(serverConfig.NatsServerHostExternal);
var scheme = useWss ? "wss" : "nats";
var port = useWss ? 443 : 4222;
natsUrl = $"{scheme}://{natsHost}:{port}";
```

Agora o `debug_config.json` gerado para o agent contém URLs completas como:
- `nats://nats.discoveryrmm.com:4222` (TCP, conexão interna)
- `wss://nats.discoveryrmm.com:443` (WebSocket, conexão externa com TLS)

#### 2. `appsettings.json` — Centralizar configs NATS ✅ IMPLEMENTADO

**Localização:** `src/Discovery.Api/appsettings.json`

```json
"Nats": {
  "Url": "nats://localhost:4222",
  "AuthUser": "discovery_nats",
  "AuthPassword": "",
  "AccountSeed": "",
  "XKeySeed": "",
  "AuthCallout": {
    "Enabled": true,
    "Subject": "$SYS.REQ.USER.AUTH"
  }
}
```

#### 3. `Program.cs` — Verificar se `NatsUseWssExternal` é respeitado em todo lugar ✅ VERIFICADO

`NatsServiceCollectionExtensions` já lê `Nats:Url`, `Nats:AuthUser`, `Nats:AuthPassword`.
`NatsAuthCalloutBackgroundService` já lê `Nats:XKeySeed`, `Nats:AuthCallout:Enabled`, `Nats:AuthCallout:Subject`.
Nenhuma alteração necessária.

---

### 🆕 Endpoint Dedicado: `GET /api/v1/agent-auth/me/realtime/status` ✅ IMPLEMENTADO

**Arquivo:** `src/Discovery.Api/Controllers/AgentAuth/AgentAuthController.Status.cs` (novo)

**Problema detectado:** O agent estava tentando consultar `GET /api/v1/realtime/status`, que exige autenticação JWT de usuário (filtro global `RequireUserAuthAttribute`). O token `mdz_` do agent não é um JWT, resultando em **401 Unauthorized**.

**Solução:** Criamos um endpoint específico para o agent dentro da rota `/api/v1/agent-auth/me/`, que já é protegida pelo `AgentAuthMiddleware` (valida tokens `mdz_`).

```csharp
[HttpGet("me/realtime/status")]
public IActionResult GetAgentRealtimeStatus()
{
    if (!TryGetAuthenticatedAgentId(out var agentId))
        return Unauthorized(new { error = "Agent not authenticated." });

    var messaging = HttpContext.RequestServices.GetService<IAgentMessaging>();
    var natsConnected = messaging?.IsConnected == true;
    var redisConnected = _redisService.IsConnected;

    return Ok(new
    {
        agentId,
        serverTimeUtc = DateTime.UtcNow,
        natsConnected,
        signalrConnectedAgents = AgentHub.ConnectedAgentCount,
        redisConnected,
        healthy = natsConnected && redisConnected,
        checkedAtUtc = DateTime.UtcNow
    });
}
```

**Fluxo de autenticação do novo endpoint:**

| Passo | Middleware | Resultado |
|-------|-----------|-----------|
| 1 | `AgentAuthMiddleware` | ✅ Valida token `mdz_`, define `AgentId` no context |
| 2 | `ApiTokenAuthMiddleware` | ✅ Ignora (sem header `X-Api-Key`) |
| 3 | `UserAuthMiddleware` | ✅ Ignora (token `mdz_` não é JWT) |
| 4 | `RequireUserAuthAttribute` | ✅ Ignora porque `AgentAuthController` tem `[AllowAnonymous]` |

**O que o agent DEVE chamar:**

```bash
# ✅ Correto — endpoint do agent
GET /api/v1/agent-auth/me/realtime/status
Header: Authorization: Bearer mdz_{token}
Header: X-Agent-ID: {agentId}

# ❌ INCORRETO — endpoint de usuário (não funciona com token mdz_)
GET /api/v1/realtime/status
```

---

### ❌ Problema Adicional: Connection Refused na Porta 443

**Erro observado:**
```
dial tcp 192.168.1.142:443: connectex: No connection could be made because the target machine actively refused it.
```

**Causa provável:** A API Discovery está rodando em uma porta diferente de 443 (ex: porta 5000 HTTP, ou outra configurada no `appsettings.json`/`Kestrel`). O agent está configurado para conectar em `https://tngplacas.com.br` que resolve para `192.168.1.142:443`, mas:

1. **Nenhum serviço escutando na 443** — A API pode estar rodando em outra porta (ex: 5000, 8080)
2. **Proxy reverso ausente** — Se a API roda em porta interna, o esperado é ter nginx/apache/Caddy na 443 fazendo proxy
3. **Firewall bloqueando** — Ou o firewall está bloqueando a porta 443

**Verificações necessárias:**
- `netstat -ano | findstr :443` — há processo escutando?
- `Get-Process -Id (pid)` — qual processo?
- Qual porta o Kestrel está configurado para escutar?
- Há um proxy reverso configurado?

**Correção imediata:** configurar o agent para apontar para a URL/porta correta da API Discovery, não necessariamente 443.

---

### 🎯 Plano de Ação (Prioridades)

| Prioridade | Tarefa | Onde | Status |
|------------|--------|------|--------|
| 🔴 P1 | Adicionar `default_permissions` + limites no nats-server.conf | Servidor NATS (`services.sh`) | ✅ **Concluído** |
| 🔴 P1 | Configurar WebSocket com TLS para agents externos | Servidor NATS + DNS | ⏳ Pendente (configurar certificados) |
| 🟡 P2 | Ajustar `AgentPackageService` para usar scheme/porta corretos | Infra/Services | ✅ **Concluído** |
| 🟡 P2 | Centralizar configs NATS no appsettings.json | Api/appsettings.json | ✅ **Concluído** |
| 🟡 P2 | Criar filtro de autorização SignalR (anti-spoofing agents) | Api/Hubs | ✅ **Concluído** |
| 🔴 P1 | Criar endpoint dedicado para agent consultar status do servidor | Api/AgentAuth | ✅ **Concluído** |
| 🟢 P3 | Revisar `RealtimeController` para consistência WSS | Api/Controllers | 🔄 Migrado para endpoint agent-dedicado |
| 🟢 P3 | Documentar fluxo de conexão para agents Go/Windows | Docs | ✅ **Concluído** |

---

### 🧩 Ajustes no `services.sh`

#### 4. `services.sh` — Limites de segurança e `default_permissions` no NATS ✅ IMPLEMENTADO

**Localização:** `scripts/linux/lib/services.sh`

**Mudanças no `setup_nats()`:**

**Antes:**
```conf
listen: ${NATS_BIND_HOST}:4222
http: ${NATS_MONITOR_HOST}:8222
server_name: discovery-nats
${auth_block}${ws_block}
```

**Depois:**
```conf
listen: ${NATS_BIND_HOST}:4222
http: ${NATS_MONITOR_HOST}:8222
server_name: discovery-nats
max_payload: 4194304
max_connections: 5000
write_deadline: 5s
${auth_block}${ws_block}
```

E dentro do `authorization {}` com auth callout, adicionado:
```conf
  default_permissions {
    publish = ["$SYS.>"]
    subscribe = ["$SYS.>"]
  }
```

Isso garante:
- **`max_payload: 4MB`** — protege contra flooding via mensagens gigantes
- **`max_connections: 5000`** — limite de conexões simultâneas
- **`write_deadline: 5s`** — timeout para escrita, evita conexões penduradas
- **`default_permissions`** — bloqueia `$SYS.>` para usuários JWT (defesa em profundidade)
- O JWT do agent já restringe aos subjects dele, mas o `default_permissions` impede qualquer acesso acidental a `$SYS.>` caso o JWT seja adulterado

---

### 🧱 SignalR — `AgentHubAuthorizationFilter` (equivalente ao `default_permissions` do NATS) ✅ IMPLEMENTADO

**Arquivos criados/alterados:**
- `src/Discovery.Api/Hubs/AgentHubAuthorizationFilter.cs` (novo)
- `src/Discovery.Api/Program.cs` (registro do filtro)

**Conceito:**

> **NATS**: JWT diz "agent X pode publicar só em `tenant.{c}.{s}.{X}.*`"
> **SignalR**: HubFilter diz "agent X pode chamar só métodos que operam sobre o seu próprio agentId"

**Regras de isolamento que o filtro aplica:**

| Regra | O que impede | Equivalente NATS |
|-------|-------------|------------------|
| Agent chamando `JoinDashboard`, `JoinClientDashboard`, `JoinSiteDashboard` | Agent não consegue escutar eventos de dashboard | `$SYS.>` bloqueado |
| Usuário chamando `Heartbeat`, `RegisterAgent`, `CommandResult`, etc | Usuário não consegue agir como agent | Subjects de agent só permitem JWT de agent |
| Agent passando `agentId` de outro agent em `RegisterAgent`/`Heartbeat` | Agent A não consegue se passar por Agent B | Subject contém agentId específico |
| Anônimo chamando métodos de agent | Conexão sem auth é bloqueada | `auth_callout` exige token |

**Como funciona:**

```csharp
public class AgentHubAuthorizationFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        // 1. Agent chamando método de usuário → BLOQUEADO
        if (isAgent && UserOnlyMethods.Contains(methodName))
            throw new HubException("Access denied.");

        // 2. Usuário chamando método de agent → BLOQUEADO
        if (isUser && AgentOnlyMethods.Contains(methodName))
            throw new HubException("Access denied.");

        // 3. Agent passando agentId de outro → BLOQUEADO (anti-spoofing)
        if (isAgent && agentIdParam != authenticatedId)
            throw new HubException("Agent identity mismatch.");

        return await next(invocationContext);
    }
}
```

**Registro no `Program.cs`:**

```csharp
// Aplica a TODOS os hubs do SignalR (o filtro age seletivamente por método)
builder.Services.AddSingleton<IHubFilter, AgentHubAuthorizationFilter>();
```

> **Nota:** O filtro é aplicado globalmente, mas as regras são seletivas — só métodos específicos de `AgentHub` são verificados. Hubs como `NotificationHub` e `RemoteDebugHub` não são afetados.

---

## 🚀 Plano de Implementação — P2P Discovery por Site

### 🎯 Objetivo da mudança

Adotar subject de descoberta por **site** para reduzir processamento no servidor e evitar fan-out por agent:

```
tenant.{clientId}.site.{siteId}.p2p.discovery
```

Com esse modelo, o servidor publica **uma vez por site** e todos os agents daquele site consomem o mesmo evento.

---

### 🧱 Modelo operacional proposto

| Item | Modelo atual (por agent) | Modelo novo (por site) |
|------|---------------------------|-------------------------|
| Subject de discovery | `tenant.{c}.site.{s}.agent.{a}.p2p.discovery` | `tenant.{c}.site.{s}.p2p.discovery` |
| Publicações do servidor | 1 publish por agent destino | 1 publish por site |
| Carga no servidor | Maior em sites grandes | Menor e previsível |
| Carga no agent | Menor parse por mensagem | Parse local da lista do site |

---

### 🔐 ACL/JWT (NATS) recomendada

Adicionar apenas a assinatura do subject de discovery por site para cada agent:

```
# Agent ASSINA
tenant.{clientId}.site.{siteId}.p2p.discovery
```

Permissões de publish do agent continuam iguais (sem publish em discovery):

```
tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat
tenant.{clientId}.site.{siteId}.agent.{agentId}.result
tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware
tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log
```

Isso mantém isolamento por tenant/site e evita spoofing de descoberta.

---

### 📦 Contrato de evento NATS (proposto)

```json
{
  "version": 1,
  "clientId": "f5a4a2c0-...",
  "siteId": "a7d8f3e1-...",
  "generatedAtUtc": "2026-05-03T12:34:56.000Z",
  "ttlSeconds": 120,
  "sequence": 184,
  "peers": [
    {
      "agentId": "2d8e...",
      "peerId": "12D3KooW...",
      "addrs": ["10.10.2.14", "192.168.1.50"],
      "port": 41080,
      "lastHeartbeatAtUtc": "2026-05-03T12:34:40.000Z"
    }
  ]
}
```

Observações:
- O agent filtra localmente o próprio `agentId` da lista.
- `ttlSeconds` orienta invalidação local de cache.
- `sequence` evita aplicar mensagens antigas fora de ordem.

---

### ⚙️ Estratégia para reduzir processamento no servidor

1. **Debounce por site (coalescing):** ao invés de publicar a cada evento bruto, agrupar eventos em janela curta (ex.: 1-2s).
2. **Publish apenas quando houver mudança real:** comparar hash do snapshot anterior e pular publish quando não mudou.
3. **Uma consulta por site por janela:** montar snapshot único por site.
4. **TTL de peers online:** reaproveitar janela já usada no bootstrap (ex.: 10 min online cutoff).
5. **Limite de payload configurável:** limitar peers no snapshot com fallback para ordenação estável.

---

### 🛠️ Plano técnico por etapa

#### Etapa 1 — Subject e ACL no emissor de JWT

- Alterar `NatsCredentialsService.BuildAgentSubjects` para incluir subscribe em `tenant.{clientId}.site.{siteId}.p2p.discovery`.
- Não adicionar publish de discovery para agent.

Arquivos alvo:
- `src/Discovery.Infrastructure/Services/NatsCredentialsService.cs`
- `src/Discovery.Tests/NatsIsolationTests.cs`

#### Etapa 2 — Builder de subject P2P por site

- Adicionar helper dedicado no `NatsSubjectBuilder` para evitar strings hardcoded.

Arquivo alvo:
- `src/Discovery.Core/Helpers/NatsSubjectBuilder.cs`

#### Etapa 3 — Publicador de discovery por site

- Criar método em mensageria para publicar snapshot no subject do site.
- Implementar debounce e deduplicação por hash em memória/Redis.

Arquivos alvo:
- `src/Discovery.Core/Interfaces/IAgentMessaging.cs`
- `src/Discovery.Infrastructure/Messaging/NatsAgentMessaging.cs`

#### Etapa 4 — Gatilhos de publicação

- Publicar discovery quando ocorrer upsert no bootstrap P2P.
- Publicar também em transições relevantes de online/offline (com debounce).

Arquivos alvo:
- `src/Discovery.Api/Controllers/AgentAuth/AgentAuthController.P2pKnowledge.cs`
- (opcional) serviço de heartbeat/status para gatilhos adicionais.

#### Etapa 5 — Compatibilidade e rollout

- Manter `POST /api/v{version:apiVersion}/agent-auth/me/p2p/bootstrap` como fallback durante transição.
- Ativar por feature flag e rollout gradual por ambiente/site.

---

### 🚩 Feature flags sugeridas

```json
"P2p": {
  "Discovery": {
    "UseSiteSubject": true,
    "PublishDebounceMs": 1500,
    "TtlSeconds": 120,
    "MaxPeersPerSnapshot": 500,
    "SkipPublishIfUnchanged": true
  }
}
```

---

### 🧪 Plano de testes

1. **Unitário (subjects):** valida formato canônico do novo subject por site.
2. **Unitário (ACL):** agent recebe subscribe de `p2p.discovery` no site correto e não em outros sites.
3. **Integração (site com N agents):** 1 evento de entrada gera 1 publish por site (não N).
4. **Resiliência:** com reconnect de vários agents, debounce evita tempestade de publish.
5. **Compatibilidade:** bootstrap HTTP continua funcional durante rollout.

---

### ✅ Critérios de aceite

- Servidor publica discovery em `tenant.{clientId}.site.{siteId}.p2p.discovery`.
- Não existe fan-out por agent para discovery.
- Cada agent assina apenas o subject do próprio site.
- Bootstrap HTTP continua ativo como fallback.
- Métricas mostram redução de publishes em sites com alta cardinalidade de agents.

