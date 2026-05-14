# Plano de Implementação — Servidor: Otimização P2P

> Data: 2026-05-13
> Status: Rascunho para revisão
> Escopo: manifesto canônico, telemetria enriquecida, NATS peer discovery otimizado, lock global, URL canônica de artifact
> Relacionado: `C:\Projetos\Discovery\DOCs\PLANO_REDE_PRIVADA_P2P_PSK_SITE.md` (plano do Agent)

---

## Sumário Executivo

O plano do Agent P2P define Fases 1–5 que dependem de novos recursos no servidor.
Este documento detalha a implementação de cada recurso no backend ASP.NET Core,
organizado por ordem de prioridade e dependência.

---

## Índice

1. [Manifesto Canônico](#1-manifesto-canônico-vinculado-ao-winget)
2. [Telemetria Enriquecida](#2-telemetria-enriquecida)
3. [NATS Peer Discovery Otimizado](#3-nats-peer-discovery-otimizado)
4. [Lock Global via Redis](#4-lock-global-via-redis)
5. [URL Canônica do Artifact](#5-url-canônica-do-artifact)

---

## 1. Manifesto Canônico Vinculado ao Winget

### 1.1 Contexto

O plano do Agent (Fase 2) define política híbrida: o agent consulta o servidor
primeiro (`GET /manifest/{artifactId}`) e, se não existir, gera localmente.
Após download validado, publica o manifesto no servidor para otimizar próximos
downloads.

### 1.2 Vínculo do `artifactId`

O `artifactId` do manifesto = `WingetPackage.Id` (Guid). O `WingetPackage` já é
Guid e já é usado como `P2pArtifactPresence.ArtifactId`. Para apps custom futuros,
será `AppPackage.Id`.

```
p2p_artifact_manifest.artifact_id → winget_packages.id
p2p_artifact_manifest.artifact_id → app_packages.id (futuro)
```

### 1.3 Nova Entidade

```csharp
// src/Discovery.Core/Entities/P2pArtifactManifest.cs
public class P2pArtifactManifest
{
    /// <summary>Guid do WingetPackage.Id ou AppPackage.Id (PK)</summary>
    public Guid ArtifactId { get; set; }

    /// <summary>ID do cliente para escopo de consulta</summary>
    public Guid ClientId { get; set; }

    /// <summary>P2PChunkManifest completo serializado como JSON</summary>
    public string ManifestJson { get; set; } = string.Empty;

    /// <summary>SHA-256 final do arquivo</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Tamanho total do arquivo em bytes</summary>
    public long TotalSize { get; set; }

    /// <summary>Tamanho de cada chunk em bytes</summary>
    public int ChunkSize { get; set; }

    /// <summary>Número total de chunks</summary>
    public int TotalChunks { get; set; }

    /// <summary>AgentId que gerou o manifesto</summary>
    public Guid GeneratedBy { get; set; }

    /// <summary>Timestamp de geração no agent</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>Timestamp de upsert no servidor</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

### 1.4 Migration

```sql
CREATE TABLE p2p_artifact_manifest (
    artifact_id      UUID        NOT NULL PRIMARY KEY,
    client_id        UUID        NOT NULL,
    manifest_json    TEXT        NOT NULL,
    sha256           VARCHAR(64) NOT NULL,
    total_size       BIGINT      NOT NULL,
    chunk_size       INT         NOT NULL,
    total_chunks     INT         NOT NULL,
    generated_by     UUID        NOT NULL,
    generated_at     TIMESTAMPTZ NOT NULL,
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_p2p_manifest_client ON p2p_artifact_manifest (client_id);
```

### 1.5 Endpoints no AgentP2pController

#### POST /api/agent-auth/me/p2p/manifest

**Body:**
```json
{
  "artifactId": "550e8400-e29b-41d4-a716-446655440000",
  "artifactName": "MyApp-1.2.3.exe",
  "chunkSizeBytes": 8388608,
  "totalSize": 52428800,
  "sha256": "a1b2c3d4e5f6...",
  "chunks": [
    { "index": 0, "offset": 0, "size": 8388608, "sha256": "..." },
    { "index": 1, "offset": 8388608, "size": 8388608, "sha256": "..." }
  ]
}
```

**Validações:**
1. `artifactId` existe em `WingetPackage` OU `AppPackage`
2. `sha256` do arquivo final bate com reconstituição dos chunks
3. `total_chunks * chunk_size >= total_size` (último chunk pode ser menor)
4. `chunks[i].offset == i * chunkSize` para `i < totalChunks - 1`
5. `chunks[last].offset + chunks[last].size == totalSize`
6. `clientId` do artifact = `clientId` do site do agent
7. Se já existir manifesto, só sobrescreve se `sha256` diferente OU `generatedAt` mais novo

**Response:** `202 Accepted`

#### GET /api/agent-auth/me/p2p/manifest/{artifactId}

**Response `200`:**
```json
{
  "artifactId": "550e8400-e29b-41d4-a716-446655440000",
  "artifactName": "MyApp-1.2.3.exe",
  "manifest": {
    "chunkSize": 8388608,
    "totalSize": 52428800,
    "totalChunks": 7,
    "sha256": "a1b2c3...",
    "chunks": [ ... ]
  },
  "generatedAtUtc": "2026-05-13T12:00:00Z"
}
```

**Response `404`:** Manifesto não existe.

### 1.6 Repository

```csharp
// src/Discovery.Core/Interfaces/IP2pArtifactManifestRepository.cs
public interface IP2pArtifactManifestRepository
{
    Task<P2pArtifactManifest?> GetByArtifactIdAsync(Guid artifactId, CancellationToken ct = default);
    Task UpsertAsync(P2pArtifactManifest manifest, CancellationToken ct = default);
}
```

### 1.7 Tarefas

| # | Tarefa | Arquivos | Esforço |
|---|--------|----------|:-------:|
| 1 | Criar entidade `P2pArtifactManifest` | `Discovery.Core/Entities/` | 10min |
| 2 | Criar migration | `Discovery.Migrations/` | 10min |
| 3 | Criar DTOs `P2pManifestRequest` / `P2pManifestResponse` | `Discovery.Core/DTOs/P2pManifestDtos.cs` | 15min |
| 4 | Criar `IP2pArtifactManifestRepository` e implementação EF | `Infrastructure/Repositories/` | 30min |
| 5 | Adicionar propriedade de navegação no `DiscoveryDbContext` | `Infrastructure/Data/` | 10min |
| 6 | Criar endpoints POST + GET no `AgentP2pController` | `Discovery.Api/Controllers/AgentP2pController.Manifest.cs` | 45min |
| 7 | Validações (chunk consistency, sha256, clientId) | No controller | 30min |
| 8 | Testes unitários | `Discovery.Tests/` | 1h |

**Total estimado:** ~3h30min

---

## 2. Telemetria Enriquecida

### 2.1 Contexto

Hoje `P2pTelemetryRequest` tem `Metrics` (12 contadores) e `CurrentSeedPlan`.
O plano do Agent (Fase 5) adiciona `Artifacts[]`, `HostLoad`, `KnownPeers`,
`ConnectedPeers`.

### 2.2 Novos DTOs

```csharp
// Adicionar em P2pDtos.cs

public class P2pTelemetryRequest
{
    // ── Existentes ──
    public string? AgentId { get; set; }
    public string? SiteId { get; set; }
    public string? CollectedAtUtc { get; set; }
    public P2pMetricsDto? Metrics { get; set; }
    public P2pSeedPlanDto? CurrentSeedPlan { get; set; }

    // ── NOVOS ──
    public List<P2pArtifactPresenceDto>? Artifacts { get; set; }
    public P2pHostLoadDto? HostLoad { get; set; }
    public int KnownPeers { get; set; }
    public int ConnectedPeers { get; set; }
}

public class P2pArtifactPresenceDto
{
    public Guid ArtifactId { get; set; }
    public string ArtifactName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? CachedAtUtc { get; set; }
}

public class P2pHostLoadDto
{
    public int CpuCores { get; set; }
    public double RamGB { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryPercent { get; set; }
    public double DiskBusyPercent { get; set; }
}
```

### 2.3 Atualização da entidade de telemetria

Adicionar colunas em `P2pAgentTelemetry`:

```csharp
// Campos novos em P2pAgentTelemetry
public double? HostCpuPercent { get; set; }
public double? HostMemoryPercent { get; set; }
public double? HostDiskBusyPercent { get; set; }
public int HostCpuCores { get; set; }
public double HostRamGB { get; set; }
public int KnownPeers { get; set; }
public int ConnectedPeers { get; set; }
```

### 2.4 Upsert de `P2pArtifactPresence` a partir do `Artifacts[]`

```csharp
// Em P2pService.IngestTelemetryAsync, após salvar o snapshot:

if (request.Artifacts is { Count: > 0 })
{
    var now = DateTime.UtcNow;
    var presenceItems = request.Artifacts
        .Where(a => a.ArtifactId != Guid.Empty)
        .Select(a => new P2pArtifactPresence
        {
            ArtifactId = a.ArtifactId,
            ArtifactName = a.ArtifactName,
            AgentId = agentId,
            SiteId = agent.SiteId,
            ClientId = clientId,
            LastSeenAt = now
        })
        .ToList();

    // UPSERT via EF Core — tenta update, se 0 rows, insert
    foreach (var item in presenceItems)
    {
        var existing = await _db.P2pArtifactPresences
            .FirstOrDefaultAsync(p => p.ArtifactId == item.ArtifactId && p.AgentId == item.AgentId, ct);
        if (existing is not null)
        {
            existing.LastSeenAt = now;
            existing.ArtifactName = item.ArtifactName;
        }
        else
        {
            _db.P2pArtifactPresences.Add(item);
        }
    }
}
```

### 2.5 Validações

| Campo | Validação |
|-------|-----------|
| `Artifacts[]` | Máx. 500 itens (truncado pelo agent, servidor rejeita se > 500) |
| `HostLoad.CpuPercent` | 0–100 |
| `HostLoad.MemoryPercent` | 0–100 |
| `HostLoad.DiskBusyPercent` | 0–100 |
| `HostLoad.CpuCores` | ≥ 1 |
| `HostLoad.RamGB` | ≥ 0.1 (mínimo realista) |
| `KnownPeers` | ≥ `ConnectedPeers` (warning, não rejeição) |

### 2.6 Tarefas

| # | Tarefa | Esforço |
|---|--------|:-------:|
| 1 | Adicionar `P2pArtifactPresenceDto` e `P2pHostLoadDto` em `P2pDtos.cs` | 15min |
| 2 | Adicionar campos novos em `P2pTelemetryRequest` | 5min |
| 3 | Adicionar campos novos em `P2pAgentTelemetry` (entidade) | 10min |
| 4 | Criar migration para novas colunas em `p2p_agent_telemetry` | 15min |
| 5 | Atualizar `IngestTelemetryAsync` para upsert de `P2pArtifactPresence` | 30min |
| 6 | Atualizar `IngestTelemetryAsync` para persistir `HostLoad` e `KnownPeers` | 15min |
| 7 | Adicionar validações em `ValidateTelemetryRequest` | 20min |
| 8 | Testes unitários | 1h |

**Total estimado:** ~3h

---

## 3. NATS Peer Discovery Otimizado

### 3.1 Decisão Arquitetural

`POST /p2p/bootstrap` foi removido. A descoberta P2P agora é 100% baseada no heartbeat.

**Motivação:**
- O heartbeat já é enviado periodicamente pelo agent via NATS
- O `HeartbeatCacheService` já detecta transição Offline→Online (chave Redis expirou)
- Elimina endpoint HTTP separado, reduz complexidade e latência
- O agent não precisa de HTTP para descoberta — apenas NATS

### 3.2 Fluxo

```
Agent (Go)                          Servidor (C#)                     Agents (Go)
    |                                    |                                 |
    |── tenant.{c}.site.{s}.agent.{a}.heartbeat ──→                       |
    |    { agentId, clientId, siteId,      |                                 |
    |      peerId, addrs, port, ... }     |                                 |
    |                                    |  HeartbeatCacheService           |
    |                                    |  ├─ Detecta Offline→Online       |
    |                                    |  └─ Retorna `wasTransition`=true |
    |                                    |                                 |
    |                                    |── tenant.{clientId}.p2p.events ─→│
    |                                    |    { eventType: "peer.online",   |
    |                                    |      agentId, siteId,            |
    |                                    |      peerId, addrs, port }       |
    |                                    |                                 |
    |                                    |                                 |  [debounce 5s]
    |                                    |                                 ├─ conectar via libp2p
```

### 3.3 Heartbeat enriquecido

O `AgentHeartbeat` ganha 3 campos novos:

```csharp
public record AgentHeartbeat(
    // ── Campos existentes ──
    Guid AgentId,
    Guid? ClientId = null,
    Guid? SiteId = null,
    string? IpAddress = null,
    string? Hostname = null,
    string? AgentVersion = null,
    DateTime? TimestampUtc = null,
    double? CpuPercent = null,
    double? MemoryPercent = null,
    double? MemoryTotalGb = null,
    double? MemoryUsedGb = null,
    double? DiskPercent = null,
    double? DiskTotalGb = null,
    double? DiskUsedGb = null,
    int? P2pPeers = null,
    long? UptimeSeconds = null,
    int? ProcessCount = null,

    // ── NOVOS para descoberta P2P ──
    string? PeerId = null,        // libp2p peer ID (12D3KooW...)
    IReadOnlyList<string>? Addrs = null,  // IPs roteáveis
    int? Port = null              // porta libp2p (41080-41120)
);
```

**Por que não criar um DTO separado?** Porque `PeerId`/`Addrs`/`Port` são dados do agent,
assim como hostname e ipAddress. Unificar evita duplicação e garante consistência:
quem ouve o heartbeat tem todos os dados num payload só.

### 3.4 Nova entidade HeartbeatCacheEntry

A entrada cacheada no Redis também precisa armazenar os campos P2P:

```csharp
public class HeartbeatCacheEntry
{
    // ── Campos existentes ──
    public Guid AgentId { get; init; }
    public AgentStatus Status { get; init; }
    public string? IpAddress { get; init; }
    public string? Hostname { get; init; }
    public string? AgentVersion { get; init; }
    public DateTime LastHeartbeatAt { get; init; }
    public double? CpuPercent { get; init; }
    public double? MemoryPercent { get; init; }
    public double? MemoryTotalGb { get; init; }
    public double? MemoryUsedGb { get; init; }
    public double? DiskPercent { get; init; }
    public double? DiskTotalGb { get; init; }
    public double? DiskUsedGb { get; init; }
    public int? P2pPeers { get; init; }
    public long? UptimeSeconds { get; init; }
    public int? ProcessCount { get; init; }

    // ── NOVOS para descoberta P2P ──
    public string? PeerId { get; init; }
    public IReadOnlyList<string>? Addrs { get; init; }
    public int? Port { get; init; }
}
```

### 3.5 Detecção de transição e publicação de `peer.online`

O `NatsAgentMessaging` — ao processar cada heartbeat — checka a transição:

```csharp
// Em NatsAgentMessaging.SubscribeToAgentMessagesAsync — após cachear heartbeat:

// Novo método: retorna true se o agent acabou de transicionar Offline→Online
var wasTransition = await _heartbeatCache.SetHeartbeatAsync(heartbeat, AgentStatus.Online);

if (wasTransition && !string.IsNullOrWhiteSpace(heartbeat.PeerId))
{
    // Publica evento peer.online no subject do cliente
    var onlineEvent = new P2pPeerOnlineEvent
    {
        EventType = "peer.online",
        ClientId = siteClientId,  // resolvido do subject ou heartbeat
        SiteId = agentSiteId,
        AgentId = agentId.Value,
        PeerId = heartbeat.PeerId!,
        Addrs = heartbeat.Addrs ?? Array.Empty<string>(),
        Port = heartbeat.Port ?? 0,
        GeneratedAtUtc = DateTime.UtcNow
    };

    var eventJson = JsonSerializer.Serialize(onlineEvent, JsonOptions);
    var subject = NatsSubjectBuilder.P2pClientEventsSubject(siteClientId);
    await _connection.PublishAsync(subject, eventJson);
}
```

**`SetHeartbeatAsync` modificado** para retornar `bool` indicando transição:

```csharp
// Em HeartbeatCacheService.SetHeartbeatAsync:
// Retorna true se Offline→Online (chave não existia antes)
public async Task<bool> SetHeartbeatAsync(AgentHeartbeat heartbeat, AgentStatus status, CancellationToken ct = default)
{
    // ... código existente ...

    var existed = await _redis.GetAsync(key);
    await _redis.SetAsync(key, json, (int)DefaultTtl.TotalSeconds);

    if (string.IsNullOrWhiteSpace(existed))
    {
        // Transição Offline → Online
        await UseScopedAsync(repo => repo.UpdateStatusAsync(agentId, AgentStatus.Online, heartbeat.IpAddress));
        return true;  // NOVO
    }

    return false;
}
```

### 3.6 Remoção do `POST /p2p/bootstrap`

| Componente | Ação |
|------------|------|
| `AgentAuthController.P2pKnowledge.cs` | Remover método `P2pBootstrap` e DTOs associados |
| `AgentP2pBootstrap` (entidade) | Remover tabela `agent_p2p_bootstrap` |
| `IP2pBootstrapRepository` | Remover interface e implementação |
| `P2pBootstrapDtos.cs` | Remover `P2pBootstrapRequest`, `P2pBootstrapResponse`, `P2pBootstrapPeerDto` |
| `P2pDiscoveryService.cs` | Remover service (só publicava snapshot por site) — descontinuado |
| `P2pOnlineTracker` | Não implementar — heartbeat cache já detecta transição |
| `P2pOptions.UseSiteSubject` | Remover opção (não há mais snapshot por site) |
| `NatsAgentMessaging.PublishP2pDiscoverySnapshotAsync` | Remover método |

**OBS:** A entidade `AgentP2pBootstrap` tinha as colunas `PeerId`, `AddrsJson`, `Port`.
Esses dados agora são transmitidos via heartbeat. A tabela pode ser dropada na migration.

### 3.6 Subscription no Agent (NATS)

O `NatsCredentialsService` deve adicionar o subject `tenant.{clientId}.p2p.events`
à lista de subscriptions do agent, para que ele receba eventos `peer.online`:

```csharp
// Em NatsCredentialsService — gerar subscriptions
subscribeSubjects.Add(NatsSubjectBuilder.P2pClientEventsSubject(clientId));
```

### 3.7 Debounce no Agent (Go)

O agent deve implementar debounce de 5 segundos ao receber eventos `peer.online`,
para evitar conectar em 50 peers simultaneamente quando um site inteiro sobe:

```go
type peerOnlineDebouncer struct {
    mu      sync.Mutex
    pending map[string]P2PPeerOnlineEvent
    timer   *time.Timer
}

func (d *peerOnlineDebouncer) Enqueue(event P2PPeerOnlineEvent) {
    d.mu.Lock()
    d.pending[event.AgentId] = event
    if d.timer == nil {
        d.timer = time.AfterFunc(5*time.Second, d.flush)
    }
    d.mu.Unlock()
}

func (d *peerOnlineDebouncer) flush() {
    d.mu.Lock()
    events := d.pending
    d.pending = make(map[string]P2PPeerOnlineEvent)
    d.timer = nil
    d.mu.Unlock()
    for _, event := range events {
        coordinator.ConnectToPeer(event.PeerId, event.Addrs, event.Port)
    }
}
```

### 3.8 Nomenclatura do subject p2p.events

```csharp
// Em NatsSubjectBuilder.cs

public static string P2pClientEventsSubject(Guid clientId)
    => $"tenant.{clientId}.p2p.events";
```

### 3.9 Estimativa de Redução de Tráfego NATS

| Cenário | Antes (bootstrap + snapshot) | Depois (heartbeat) |
|---------|:----------------------------:|:------------------:|
| 50 agents, 1 bootstrap/hora cada | 50 snapshots + 50 bootstraps HTTP | 1 evento por agent só na subida (heartbeat já existe) |
| 50 agents sobem juntos | 50 snapshots em cascata + 50 HTTP | 1 evento por agent + debounce = 1 ciclo de conexão |
| Agent online por 24h | 24 bootstraps HTTP + 24 snapshots | 0 eventos adicionais |

**Redução estimada:** ~100% das publicações P2P dedicadas eliminadas (reuso do heartbeat).
Zero tráfego HTTP para descoberta.

### 3.10 Tarefas

| # | Tarefa | Esforço |
|---|--------|:-------:|
| 1 | Adicionar `PeerId`, `Addrs`, `Port` no `AgentHeartbeat` e `HeartbeatCacheEntry` | 15min |
| 2 | Criar `P2pPeerOnlineEvent` DTO + `P2pClientEventsSubject` no `NatsSubjectBuilder` | 15min |
| 3 | Modificar `SetHeartbeatAsync` para retornar `bool` (wasTransition) | 30min |
| 4 | Adicionar lógica no `NatsAgentMessaging` para publicar `peer.online` na transição | 45min |
| 5 | Remover método `P2pBootstrap` do controller + DTOs | 20min |
| 6 | Remover `AgentP2pBootstrap` (entidade, repo, migration de drop) | 20min |
| 7 | Remover `P2pDiscoveryService` e `P2pOptions` associadas | 15min |
| 8 | Adicionar subscription `p2p.events` no `NatsCredentialsService` | 20min |
| 9 | Ajustar ACL AgentIdentity para subscribe em `tenant.{c}.p2p.events` | 15min |
| 10 | Testes unitários (transição, concorrência, heartbeat sem PeerId) | 1h30min |

**Total estimado:** ~4h

---

## 4. Lock Global via Redis

### 4.1 Contexto

O plano do Agent (Fase 2.1) define lock global no servidor para evitar que
múltiplos grupos baixem o mesmo artifact da URL em paralelo.

### 4.2 Implementação com Redis SETNX

```csharp
// src/Discovery.Core/Interfaces/IP2pLockService.cs
public interface IP2pLockService
{
    /// <summary>
    /// Tenta adquirir lock global para download do artifact.
    /// Retorna true se adquirido, false se já existe lock de outro grupo.
    /// TTL padrão: 5 minutos.
    /// </summary>
    Task<bool> TryAcquireAsync(Guid clientId, Guid artifactId, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>
    /// Libera o lock global.
    /// </summary>
    Task ReleaseAsync(Guid clientId, Guid artifactId, CancellationToken ct = default);

    /// <summary>
    /// Verifica se existe lock ativo para o artifact.
    /// </summary>
    Task<bool> ExistsAsync(Guid clientId, Guid artifactId, CancellationToken ct = default);

    /// <summary>
    /// Renova o TTL do lock (chamado pelo fetcher a cada 90s).
    /// Retorna true se o lock ainda pertence a este holder.
    /// </summary>
    Task<bool> RenewAsync(Guid clientId, Guid artifactId, string holderToken, CancellationToken ct = default);
}

// Implementação
public class P2pLockService : IP2pLockService
{
    private readonly IRedisService _redis;

    public async Task<bool> TryAcquireAsync(Guid clientId, Guid artifactId, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var key = $"p2p:lock:{clientId}:{artifactId}";
        var value = Guid.NewGuid().ToString("N"); // holderToken
        var expiry = ttl ?? TimeSpan.FromMinutes(5);
        return await _redis.SetStringIfNotExistsAsync(key, value, expiry);
    }

    public async Task ReleaseAsync(Guid clientId, Guid artifactId, CancellationToken ct = default)
    {
        var key = $"p2p:lock:{clientId}:{artifactId}";
        await _redis.DeleteKeyAsync(key);
    }

    public async Task<bool> ExistsAsync(Guid clientId, Guid artifactId, CancellationToken ct = default)
    {
        var key = $"p2p:lock:{clientId}:{artifactId}";
        return await _redis.KeyExistsAsync(key);
    }

    public async Task<bool> RenewAsync(Guid clientId, Guid artifactId, string holderToken, CancellationToken ct = default)
    {
        var key = $"p2p:lock:{clientId}:{artifactId}";
        var current = await _redis.GetStringAsync(key);
        if (current != holderToken) return false;
        await _redis.SetExpiryAsync(key, (int)TimeSpan.FromMinutes(5).TotalSeconds);
        return true;
    }
}
```

### 4.3 Endpoints

| Método | Rota | Descrição |
|--------|------|-----------|
| `POST` | `/api/agent-auth/me/p2p/lock` | Adquirir lock. Body: `{ artifactId, ttlSeconds? }` |
| `DELETE` | `/api/agent-auth/me/p2p/lock/{artifactId}` | Liberar lock |
| `GET` | `/api/agent-auth/me/p2p/lock/{artifactId}` | Verificar se lock existe |

**POST response `200`:**
```json
{
  "acquired": true,
  "holderToken": "abc123def456...",
  "expiresAtUtc": "2026-05-13T12:05:00Z"
}
```

**POST response `409 Conflict`:**
```json
{
  "acquired": false,
  "message": "Lock already held by another group",
  "retryAfterSeconds": 60
}
```

### 4.4 Tarefas

| # | Tarefa | Esforço |
|---|--------|:-------:|
| 1 | Criar `IP2pLockService` + implementação Redis | 45min |
| 2 | Criar DTOs de request/response | 15min |
| 3 | Criar endpoints no `AgentP2pController.Lock.cs` | 30min |
| 4 | Testes unitários (concorrência de lock) | 45min |

**Total estimado:** ~2h15min

---

## 5. URL Canônica do Artifact

### 5.1 Contexto

O plano do Agent (Fase 2) diz: *"Idealmente a API deve devolver uma URL canônica
e temporária do artifact, junto com metadados de integridade como nome esperado,
tamanho e SHA-256 final."*

### 5.2 Integração com AppPackage e WingetPackage

O servidor já tem `AppPackage.FilePublicUrl`, `FileChecksum`, `FileSizeBytes`.
Para Winget, o `InstallerUrlsJson` contém URLs por arquitetura.

Endpoint proposto:

```
GET /api/agent-auth/me/p2p/artifact-source?artifactId=<guid>[&arch=x64]
```

### 5.3 Resposta

```json
{
  "artifactId": "550e8400-e29b-41d4-a716-446655440000",
  "artifactName": "MyApp-1.2.3.exe",
  "downloadUrl": "https://storage.discovery.io/packages/MyApp-1.2.3.exe?token=eyJ...",
  "sha256": "a1b2c3d4e5f6...",
  "sizeBytes": 52428800,
  "source": "app-package",
  "expiresAtUtc": "2026-05-13T12:30:00Z"
}
```

### 5.4 Lógica de resolução

1. Buscar em `AppPackage` pelo `artifactId` → se encontrado, usa `FilePublicUrl`
2. Buscar em `WingetPackage` pelo `artifactId` → se encontrado, extrai URL da
   arquitetura solicitada (default: `x64`) de `InstallerUrlsJson`
3. A URL é assinada com token temporário (HMAC-SHA256, TTL 30min) se o storage
   exigir autenticação
4. Se não encontrado em nenhuma fonte: `404`

### 5.5 Observação

Este endpoint é menos prioritário que os anteriores porque o Agent pode resolver
a URL por conta própria (já conhece `InstallerUrlsJson` do Winget ou `FilePublicUrl`
do AppPackage). O valor está na assinatura de URL temporária para storage protegido.

### 5.6 Tarefas

| # | Tarefa | Esforço |
|---|--------|:-------:|
| 1 | Criar endpoint `GET /p2p/artifact-source` | 45min |
| 2 | Lógica de resolução WingetPackage + AppPackage | 30min |
| 3 | Geração de token de acesso temporário (se aplicável) | 30min |
| 4 | Testes | 30min |

**Total estimado:** ~2h15min

---

## Plano de Execução (Ordem Recomendada)

| Ordem | Módulo | Esforço | Dependências |
|:-----:|--------|:-------:|--------------|
| 1 | **Telemetria Enriquecida** (servidor) | 3h | Nenhuma |
| 2 | **Manifesto Canônico** (servidor) | 3h30min | Nenhuma |
| 3 | **Lock Global** (servidor) | 2h15min | Redis (já integrado) |
| 4 | **NATS Peer Discovery** (servidor) | 4h | Redis, NATS (já integrados) |
| 5 | **URL Canônica** (servidor) | 2h15min | AppPackage/WingetPackage (já existem) |

**Total:** ~15h de implementação no servidor.

---

## Checklist de Validação Cruzada com Plano do Agent

| # | Cenário | Coberto por |
|---|---------|-------------|
| 1 | `clientId` validado em handshake | Plano Agent Fase 1 |
| 2 | Agent heartbeat inclui `PeerId`, `Addrs`, `Port` | Seção 3.3 |
| 3 | Servidor filtra peers por `clientId` | `QueryDistributionByScope` (existente) |
| 4 | Lock global Redis com TTL | Seção 4 |
| 5 | Manifesto canônico server-first | Seção 1 |
| 6 | Publicação de manifesto pós-download | Seção 1 (POST) |
| 7 | URL canônica com metadados de integridade | Seção 5 |
| 8 | Telemetria inclui artifacts[] + hostLoad | Seção 2 |
| 9 | `p2p_artifact_presence` alimentado por telemetria | Seção 2.4 |
| 10 | Evento `peer.online` disparado na transição Offline→Online do heartbeat | Seção 3.5 |
| 11 | Debounce de 5s no agent para peer.online | Seção 3.7 |
| 12 | `POST /p2p/bootstrap` removido | Seção 3.6 |
| 13 | ACL do AgentIdentity inclui subscribe em `tenant.{c}.p2p.events` | Seção 3.6 |

---

## Histórico de Revisão

| Data | Versão | Autor | Mudanças |
|------|--------|-------|----------|
| 2026-05-13 | 1.0 | — | Criação inicial: manifesto canônico, telemetria enriquecida, NATS discovery otimizado, lock global, URL canônica |
| 2026-05-13 | 1.1 | — | Priorização de `tenant.{c}.p2p.events`, ACL de AgentIdentity ajustada para subscribe no tópico por cliente e descontinuação de `tenant.{c}.site.{s}.p2p.discovery` |
| 2026-05-13 | 2.0 | — | Heartbeat enriquecido com `PeerId`/`Addrs`/`Port`; `POST /p2p/bootstrap` removido; `peer.online` disparado pelo heartbeat na transição Offline→Online; `P2pDiscoveryService` removido |
