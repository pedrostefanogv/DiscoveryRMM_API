# Plano de Otimização — Redis, NATS & SignalR

**Criado em:** 30/04/2026
**Status:** Concluido ✅ (Fases 1-5 implementadas)
**Branch:** dev

---

## Diagnóstico da Infraestrutura Atual

O sistema usa Redis, NATS e SignalR estrategicamente, mas há lacunas importantes de otimização, escalabilidade e cobertura de caching.

---

## 📊 Uso Atual do Redis

| Componente | O que faz | Arquivo |
|---|---|---|
| `RedisService` | Key-value básico (GET/SET/DELETE), Pub/Sub, SCAN, TTL, Increment | `Services/RedisService.cs` |
| `HeartbeatCacheService` | Fonte de verdade para heartbeat (Online/Offline via TTL) | `Infrastructure/Services/HeartbeatCacheService.cs` |
| `HeartbeatExpiryBackgroundService` | Detecta expirações e persiste Offline no DB | `Services/HeartbeatExpiryBackgroundService.cs` |
| `DashboardCacheKeys` | Invalidação de cache de dashboards | `Core/Helpers/DashboardCacheKeys.cs` |
| `OutputCache` (infra) | Output caching com Redis — mas **NENHUM endpoint usa** | `DependencyInjection/OutputCacheServiceCollectionExtensions.cs` |
| `SoftwareInventoryController` | Caching manual de queries de SW | `Controllers/SoftwareInventoryController.cs` |
| `NatsSignalRBridge` | Invalida cache de dashboard via evento NATS | `Services/NatsSignalRBridge.cs` |
| `RealtimeController` | Status checks de Redis/NATS | `Controllers/RealtimeController.cs` |

## 📊 Uso Atual do NATS

| Componente | O que faz |
|---|---|
| `NatsAgentMessaging` | Comunicação bidirecional com agents (command, heartbeat, result, hardware, dashboard events) |
| `NatsSignalRBridge` | Bridge NATS → SignalR para eventos de dashboard em tempo real |
| `NatsAuthController` | Emissão de credenciais NATS para usuários |
| `NatsBackgroundService` | Background service para conexão NATS |
| `NatsAuthCalloutBackgroundService` | Auth callout para NATS |
| `NatsCredentialsService` | Geração de JWT NATS |

## 📊 Uso Atual do SignalR

| Hub | Função |
|---|---|
| `AgentHub` | Conexão WebSocket agents (heartbeat, comandos, eventos) |
| `NotificationHub` | Notificações em tempo real (users, topics) |
| `RemoteDebugHub` | Sessões de debug remoto |

---

## 🔴 Problemas Críticos

### 1. Rate Limiting é in-memory (não escala horizontalmente)

**Arquivo:** `DependencyInjection/RateLimitingServiceCollectionExtensions.cs`
**Problema:** O rate limiter usa `PartitionedRateLimiter.Create` que é **in-memory por instância**. Com 3 instâncias da API, cada uma tem seu próprio contador.
**Impacto:** 20 req/min configurados viram 60 req/min efetivos (20 × 3 instâncias). Sem lockout distribuído contra brute-force.

### 2. SignalR sem Redis Backplane (não escala)

**Arquivo:** `Program.cs` (config do SignalR)
**Problema:** O SignalR não tem Redis backplane configurado. Quando há múltiplas instâncias, um cliente conectado na instância A **não recebe** mensagens enviadas pela instância B.
**Impacto:** Notificações e eventos SignalR são perdidos em deployments multi-instância. Agentes conectados em instâncias diferentes não compartilham estado.

### 3. OutputCache configurado mas NUNCA usado

**Arquivo:** `DependencyInjection/OutputCacheServiceCollectionExtensions.cs`
**Problema:** Infra completa de OutputCache com Redis e políticas nomeadas ("Short", "Medium", "Long"), mas nenhum endpoint usa `[OutputCache(PolicyName = "...")]`.
**Impacto:** Desperdício de infra. Endpoints de leitura pesada (listas de agentes, catálogo de software, configurações) vão ao banco em toda requisição.

### 4. Sem caching em endpoints de leitura de alta frequência

| Endpoint | Frequência estimada | Sem cache |
|---|---|---|
| `GET /api/v1/clients` | Alta (listas UI) | ❌ |
| `GET /api/v1/tickets` | Alta (dashboard, busca) | ❌ |
| `GET /api/v1/agents` | Alta (listas, busca) | ❌ |
| `GET /api/v1/departments` | Média | ❌ |
| `GET /api/v1/configurations/server` | Baixa mas consulta cara | ❌ |
| `GET /api/v1/roles` | Média | ❌ |
| `GET /api/v1/user-groups` | Média | ❌ |

---

## 🟠 Problemas de Otimização

### 5. HeartbeatCacheService faz ServiceLocator (anti-padrão)

**Arquivo:** `Infrastructure/Services/HeartbeatCacheService.cs`
```csharp
private IAgentRepository GetAgentRepo()
{
    var scope = _scopeFactory.CreateScope();
    return scope.ServiceProvider.GetRequiredService<IAgentRepository>();
}
```
**Problema:** Cria um scope novo a cada chamada de heartbeat, sem dispose. Vaza escopos DI.
**Solução:** Injetar `IAgentRepository` diretamente no construtor ou usar `IServiceScopeFactory` com `using`.

### 6. Notificações não usam Redis Pub/Sub para broadcast entre instâncias

**Arquivo:** `Services/NotificationService.cs`
**Problema:** O `PublishAsync` envia via SignalR diretamente. Em multi-instância, notificações só chegam aos clientes conectados na mesma instância.
**Solução:** Publicar notificações via Redis Pub/Sub e ter um listener que repassa para SignalR em cada instância.

### 7. Sem distributed locking para operações críticas

**Problema:** Operações como dedup de monitoring events, criação de tickets automáticos, e sync de catálogo não têm lock distribuído. Com múltiplas instâncias, pode haver race conditions.
**Solução:** Implementar `IDistributedLock` via Redis (`SETNX` com TTL).

### 8. NATS e SignalR sem fallback mútuo

**Arquivos:** `Hubs/AgentHub.cs`, `Infrastructure/Messaging/NatsAgentMessaging.cs`
**Problema:** NATS e SignalR operam como canais independentes para comunicação com agents. Se um falhar, o outro não assume.
**Modelo de fallback proposto:**
```
Agent → [SignalR WebSocket (primário)] → Hub → comandos/heartbeat/results
Agent → [NATS (fallback)]                → NatsMessaging → comandos/heartbeat/results

Servidor envia comando:
  1. Tenta SignalR (Clients.Group("agent-{id}"))
  2. Se falhar/desconectado, tenta NATS (PublishAsync)
  
Agent envia heartbeat:
  1. Via SignalR (AgentHub.Heartbeat)
  2. Se SignalR desconectado, via NATS (já existe)
```
**Solução:** 
- Unificar `SendCommandAsync` com fallback SignalR → NATS
- Unificar heartbeat processing: ambos canais alimentam o mesmo `HeartbeatCacheService`
- Flag no Redis `transport:agent:{id}` indica canal ativo (signalr/nats) para roteamento

---

## 🟡 Oportunidades de Melhoria

### 9. Caching de configuração resolvida

A `ConfigurationResolver` consulta o banco em toda requisição para resolver configurações hierárquicas (Server → Cliente → Site). Cache em Redis com invalidação por evento reduziria drasticamente a carga.

### 10. Agregação de status de agentes em tempo real

O `DashboardController` consulta o banco para contar agentes online/offline. Com Redis `SET` de agentes online, essa query seria O(1).

### 11. Sessões de usuário em cache

`UserSession` é consultado a cada refresh de token. Cache em Redis reduziria latência e carga no banco.

### 12. Catálogo de App Store em cache

O `AppStoreController.SearchCatalog` consulta APIs externas (Winget/Chocolatey) — cache Redis com TTL de 1h reduziria chamadas externas.

---

## 🎯 Plano de Ação

### Fase 1 — Correções críticas de escalabilidade (Prioridade MÁXIMA)

#### 1.1 Adicionar Redis Backplane ao SignalR ✅

Arquivo: `Program.cs` / `DependencyInjection/RedisServiceCollectionExtensions.cs`
```csharp
builder.Services.AddSignalR().AddStackExchangeRedis(redisConnString, options => {
    options.Configuration.ChannelPrefix = "SignalR";
});
```
**Impacto:** Multi-instância SignalR funcionando. Notificações e eventos chegam a todos os clientes.

#### 1.2 NATS ↔ SignalR fallback mútuo para comunicação com agents ✅

Arquivos: `Hubs/AgentHub.cs`, `Infrastructure/Messaging/NatsAgentMessaging.cs`, `Core/Interfaces/IAgentMessaging.cs`

Modelo:
- **Servidor → Agent**: `SendCommandAsync` tenta SignalR primeiro, fallback NATS
- **Agent → Servidor**: ambos canais alimentam mesmo `HeartbeatCacheService`
- **Transporte ativo**: Redis registra `transport:agent:{id}` (signalr|nats) para roteamento inteligente

#### 1.3 Corrigir vazamento de scope no HeartbeatCacheService ✅

Arquivo: `Infrastructure/Services/HeartbeatCacheService.cs`
Substituir `IServiceScopeFactory` sem dispose por injeção correta com `using`.
**Mudança:** Substituir `PartitionedRateLimiter.Create` (in-memory) por implementação Redis-backed.
**Alternativa:** Usar middleware de rate limiting com `IDistributedCache` do ASP.NET Core.
**Impacto:** Rate limiting consistente entre instâncias.

#### 1.3 Corrigir vazamento de scope no HeartbeatCacheService

Arquivo: `Infrastructure/Services/HeartbeatCacheService.cs`
```csharp
// Substituir ServiceLocator por:
private IAgentRepository GetAgentRepo()
{
    var scope = _scopeFactory.CreateScope();
    // ❌ Falta using/dispose no scope
}

// Para:
public class HeartbeatCacheService : IHeartbeatCacheService
{
    private readonly IAgentRepository _agentRepo; // Injetar direto
    ...
}
```
Mas isso quebraria o singleton (IAgentRepository é scoped). Alternativa: usar `IServiceScopeFactory` corretamente com `using`.

### Fase 2 — Ativar caching nos endpoints (Prioridade ALTA)

#### 2.1 Ativar OutputCache nos endpoints de leitura

| Endpoint | Política | TTL |
|---|---|---|
| `GET /api/v1/clients` | Medium | 30s |
| `GET /api/v1/roles` | Long | 5min |
| `GET /api/v1/user-groups` | Long | 5min |
| `GET /api/v1/departments` | Medium | 30s |
| `GET /api/v1/agent-labels/agents/{agentId}` | Short | 10s |
| `GET /api/v1/app-store/catalog` | Long | 5min |
| `GET /api/v1/configurations/server` | Long | 5min |
| `GET /api/v1/auto-ticket-rules` | Medium | 30s |
| `GET /api/v1/configuration-audit` | Short | 10s |

#### 2.2 Invalidação de cache nos endpoints de escrita

Cada POST/PUT/DELETE deve invalidar os caches relacionados:
- `PUT /api/v1/clients/{id}` → Evict: `GET /api/v1/clients*`
- `POST /api/v1/tickets` → Evict: `GET /api/v1/tickets*`, dashboard
- `PUT /api/v1/configurations/server` → Evict: `GET /api/v1/configurations/server*`

Usar `IOutputCacheStore` para evicção seletiva.

### Fase 3 — Otimizações de Redis (Prioridade MÉDIA)

#### 3.1 Distributed Locking

Criar `IDistributedLock`:
```csharp
public interface IDistributedLock
{
    Task<IDisposable?> AcquireAsync(string key, TimeSpan expiry);
}
```
Implementação Redis com `StringSetAsync(key, token, expiry, When.NotExists)`.
Aplicar em: dedup de monitoring events, sync de catálogo, auto-ticket creation.

#### 3.2 Cache de configuração resolvida

Adicionar caching no `ConfigurationResolver`:
```csharp
public async Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId)
{
    var cacheKey = $"config:resolved:site:{siteId:N}";
    var cached = await _redis.GetAsync(cacheKey);
    if (cached is not null) return Deserialize(cached);
    
    var resolved = await ResolveFromDbAsync(siteId);
    await _redis.SetAsync(cacheKey, Serialize(resolved), 300);
    return resolved;
}
```
Invalidar em `ConfigurationsController` POST/PUT/PATCH/DELETE.

#### 3.3 Agregação de status de agentes

Adicionar Redis `SET` para agentes online:
```csharp
// Em HeartbeatCacheService.SetHeartbeatAsync:
await _redis.SetAddAsync("heartbeat:online:agents", agentId.ToString("N"));

// Em HeartbeatExpiryBackgroundService:
await _redis.SetRemoveAsync("heartbeat:online:agents", agentId.ToString("N"));
```
`DashboardController` consulta `SCARD heartbeat:online:agents` ao invés de query no DB.

### Fase 4 — Notificações distribuídas (Prioridade BAIXA)

#### 4.1 Redis Pub/Sub para NotificationService

```csharp
public async Task<AppNotification> PublishAsync(NotificationPublishRequest request)
{
    var notification = await _repository.CreateAsync(request);
    
    // Publica via Redis Pub/Sub para broadcast entre instâncias
    var dto = JsonSerializer.Serialize(notification);
    await _redisService.PublishAsync("notifications:broadcast", dto);
    
    // Listener em cada instância repassa para SignalR local
    return notification;
}
```

### Fase 5 — Melhorias futuras (Prioridade BAIXA)

#### 5.1 Cache de sessão de usuário

TTL de 15min (mesmo que access token). Reduz latência em refresh.

#### 5.2 Catálogo App Store em Redis

TTL de 1h. Cache de `SearchCatalog` e `GetCatalogPackage`.

#### 5.3 Redis Streams para processamento assíncrono

Substituir NATS para eventos internos (ex.: background jobs) com Redis Streams + consumer groups.

---

## 📊 Resumo de Impacto

| Fase | O quê | Impacto | Esforço |
|---|---|---|---|
| F1.1 | Redis Backplane SignalR | 🔴 Escalabilidade crítica | 2 linhas de config |
| F1.2 | Rate Limiting Redis | 🔴 Segurança multi-instância | Médio |
| F1.3 | Corrigir vazamento scope | 🟠 Memory leak | Baixo |
| F2.1 | OutputCache endpoints | 🟠 Performance (10-100x) | 20 annotations |
| F2.2 | Invalidação de cache | 🟠 Consistência | Médio |
| F3.1 | Distributed Locking | 🟡 Consistência multi-instância | Baixo |
| F3.2 | Cache de config | 🟡 Performance | Médio |
| F3.3 | Agregação status agents | 🟡 Performance dashboard | Baixo |
| F4.1 | Notificações distribuídas | 🟡 UX multi-instância | Médio |
| F5.x | Melhorias futuras | 🟢 Otimizações incrementais | Variável |

---

## Progresso

| Fase | Status | Data |
|---|---|---|
| Fase 1 | ✅ Concluída | 30/04/2026 |
| Fase 2 | ✅ Concluída | 30/04/2026 |
| Fase 3 | ✅ Concluída | 30/04/2026 |
| Fase 4 | ✅ Concluída | 30/04/2026 |
| Fase 5 | ✅ Concluída | 30/04/2026 |
