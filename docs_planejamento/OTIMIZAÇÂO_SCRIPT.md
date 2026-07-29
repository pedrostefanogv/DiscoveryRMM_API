# Plano de Melhoria — Controle de Qualidade do Acesso Remoto (Screen Capture)

> Data: 2026-07-29 | Autor: Análise de código + inspeção da UI
> Status: **FASE 1 (BACKEND) CONCLUÍDA** — Compilação limpa (0 erros, 0 warnings)

## ✅ Implementado (29/07/2026)

| Componente                                                   | Arquivo                                                    | Status |
| ------------------------------------------------------------ | ---------------------------------------------------------- | ------ |
| `ChangeRemoteSessionQualityCommand` (+ flag `Auto`, + `Fps`) | `Core/Cqrs/RemoteSessions/Commands/`                       | ✅     |
| `UpdateQualityAsync` na interface                            | `Core/Interfaces/IRemoteSessionManager.cs`                 | ✅     |
| `UpdateQualityAsync` implementação                           | `Api/Services/RemoteSessionManager.cs`                     | ✅     |
| `ChangeRemoteSessionQualityCommandHandler`                   | `Api/Cqrs/.../RemoteSessionCommandHandlers.cs`             | ✅     |
| `PUT /{agentId}/{sessionId}/quality` endpoint                | `Api/Controllers/RemoteSessionsController.cs`              | ✅     |
| `QualityProfileMapping` (FPS/escala/compressão por perfil)   | `Core/Configuration/QualityProfileMapping.cs`              | ✅     |
| `SessionMetricsStore` (store em memória)                     | `Infrastructure/Services/Remote/SessionMetricsStore.cs`    | ✅     |
| `AdaptiveQualityService` (BackgroundService com histerese)   | `Infrastructure/Services/Remote/AdaptiveQualityService.cs` | ✅     |
| `AckFrameCommandHandler` → alimenta `SessionMetricsStore`    | `Api/Cqrs/.../RemoteSessionCommandHandlers.cs`             | ✅     |
| Registro DI (`Program.cs`)                                   | `Api/Program.cs`                                           | ✅     |
| Config `AdaptiveThresholds`                                  | `Core/Configuration/RemoteAccessOptions.cs`                | ✅     |

## 🔜 Pendente (Frontend)

| Tarefa                                        | Prioridade |
| --------------------------------------------- | ---------- |
| Dropdown de Quality Profile na barra superior | P0         |
| Dropdown de Codec na barra superior           | P0         |
| Toggle "Auto" para qualidade adaptativa       | P0         |
| Chamada `PUT /quality` ao trocar seleção      | P0         |

## 1. Diagnóstico — Situação Atual

### 1.1 O que funciona

| Componente                                                                           | Status |
| ------------------------------------------------------------------------------------ | ------ |
| Stream de tela via NATS (JPEG)                                                       | ✅ OK  |
| Sessão inicia com quality/codec fixos via URL params                                 | ✅ OK  |
| OSD mostra FPS, latência, quality, codec                                             | ✅ OK  |
| `RemoteSessionDispatcher.DispatchQualityChangeAsync()` — método existe               | ✅ OK  |
| `CommandType.RemoteSessionQuality = 14` — wire protocol definido                     | ✅ OK  |
| `CommandTypeWireMapper.ToWireValue(RemoteSessionQuality)` → `"remotesessionquality"` | ✅ OK  |
| Canal NATS `remote.session.{id}.control` suporta `action: "quality"`                 | ✅ OK  |
| `AckFrameCommand` recebe RTT, jitter, bandwidth estimada                             | ✅ OK  |
| `RemoteAccessQualityOptions.AdaptiveEnabled = true` — config existe                  | ✅ OK  |

### 1.2 O que NÃO funciona (gaps)

| Gap                                                                     | Impacto                                                                |
| ----------------------------------------------------------------------- | ---------------------------------------------------------------------- |
| **Sem endpoint REST para alterar qualidade dinamicamente**              | Usuário não consegue trocar qualidade durante a sessão                 |
| **Sem CQRS Command `ChangeRemoteSessionQualityCommand`**                | Falta o comando no Core para orquestrar a mudança                      |
| **Sem Command Handler para quality change**                             | Ninguém chama `DispatchQualityChangeAsync`                             |
| **Frontend: barra superior mostra Quality/Codec como labels estáticos** | Não há dropdown/seletor para o usuário interagir                       |
| **`RemoteSessionManager` não tem método `UpdateQualityAsync`**          | Qualidade não é atualizada no banco (entidade fica stale)              |
| **Perfis de qualidade sem mapeamento concreto de parâmetros**           | `QualityProfile` (Ultra..UltraLow) não define FPS, escala, compressão  |
| **Sem lógica de adaptação automática implementada**                     | `AdaptiveEnabled=true` não tem código que a execute                    |
| **Sem endpoint de métricas em tempo real exposto**                      | Frontend não tem como exibir latência/bandwidth para o usuário decidir |

---

## 2. Plano de Implementação

### 2.1 Visão Geral

```mermaid
flowchart TD
    A[Usuário na UI] -->|Seleciona novo perfil| B[PUT /remote-sessions/{agentId}/{sessionId}/quality]
    B --> C[ChangeRemoteSessionQualityCommandHandler]
    C --> D[RemoteSessionManager.UpdateQualityAsync]
    D --> E[(UPDATE remote_sessions)]
    C --> F[RemoteSessionDispatcher.DispatchQualityChangeAsync]
    F --> G[NATS: tenant...control {action:quality}]
    G --> H[Agent Go recebe comando]
    H --> I[Agent ajusta FPS/codec/compressão]
    H --> J[Novo stream com qualidade ajustada]

    K[AdaptiveQualityService BG] -.->|Monitora métricas| L[RTT/Jitter/Bandwidth]
    L -.->|Auto-ajuste| M[DispatchQualityChangeAsync]
```

### 2.2 Fase 1 — Backend: Controle Manual de Qualidade (P0)

#### 2.2.1 Novo Command + Handler

**Arquivo:** `src/Discovery.Core/Cqrs/RemoteSessions/Commands/RemoteSessionCommands.cs`

Adicionar:

```csharp
public sealed record ChangeRemoteSessionQualityCommand(
    Guid AgentId,
    Guid SessionId,
    Guid UserId,
    QualityProfile Quality,
    RemoteCodec? Codec = null,
    int? Fps = null
) : ICommand<Result<RemoteSessionResponseDto>>;
```

#### 2.2.2 Handler no `RemoteSessionCommandHandlers.cs`

```csharp
public sealed class ChangeRemoteSessionQualityCommandHandler(
    IRemoteSessionManager sessionManager,
    RemoteSessionDispatcher dispatcher,
    ILogger<ChangeRemoteSessionQualityCommandHandler> logger
) : IRequestHandler<ChangeRemoteSessionQualityCommand, Result<RemoteSessionResponseDto>>
{
    public async Task<Result<RemoteSessionResponseDto>> Handle(
        ChangeRemoteSessionQualityCommand cmd, CancellationToken ct)
    {
        var session = await sessionManager.GetActiveForUserAsync(cmd.SessionId, cmd.UserId, ct);
        if (session is null)
            return Result<RemoteSessionResponseDto>.Failure(Error.NotFound("Session not found or not active."));

        // Atualiza no banco
        session = await sessionManager.UpdateQualityAsync(
            cmd.SessionId, cmd.Quality, cmd.Codec, ct);

        // Dispara comando para o agent
        await dispatcher.DispatchQualityChangeAsync(
            cmd.AgentId, cmd.SessionId, cmd.Quality, cmd.Codec, cmd.Fps, ct);

        logger.LogInformation("Quality changed to {Quality}/{Codec} for session {SessionId}",
            cmd.Quality, cmd.Codec, cmd.SessionId);

        return Result<RemoteSessionResponseDto>.Success(new RemoteSessionResponseDto(
            session.Id, session.NatsSubject ?? "", session.AgentId,
            session.Kind.ToString(), session.Transport.ToString(),
            session.QualityProfile.ToString(), session.Codec.ToString(),
            session.Status, session.ExpiresAt, session.StartedAt));
    }
}
```

#### 2.2.3 Método `UpdateQualityAsync` no `RemoteSessionManager`

```csharp
public async Task<RemoteSession> UpdateQualityAsync(
    Guid sessionId, QualityProfile quality, RemoteCodec? codec = null,
    CancellationToken ct = default)
{
    var session = await _repo.GetByIdAsync(sessionId, ct)
        ?? throw new InvalidOperationException($"Session {sessionId} not found.");

    session.QualityProfile = quality;
    if (codec.HasValue) session.Codec = codec.Value;

    var updated = await _repo.UpdateAsync(session, ct);
    await AuditAsync(sessionId, "quality_changed",
        $"{{\"quality\":\"{quality}\",\"codec\":\"{updated.Codec}\"}}",
        null, null, ct);

    return updated;
}
```

#### 2.2.4 Novo Endpoint REST no `RemoteSessionsController`

```csharp
/// <summary>Altera qualidade/codec/FPS de uma sessão ativa.</summary>
[HttpPut("{agentId:guid}/{sessionId:guid}/quality")]
[RemoteSessionAuthorize(RequiredAction = ActionType.Execute)]
[RequirePermission(ResourceType.Agents, ActionType.Execute)]
public async Task<IActionResult> ChangeQuality(
    Guid agentId, Guid sessionId,
    [FromBody] ChangeRemoteSessionQualityCommand cmd, CancellationToken ct = default)
{
    var userId = GetUserId();
    var result = await _mediator.Send(cmd with
    {
        AgentId = agentId,
        SessionId = sessionId,
        UserId = userId
    }, ct);
    return result.Match<IActionResult>(
        success: Ok,
        failure: errors => errors[0].Code == "NotFound"
            ? NotFound(new { error = errors[0].Message })
            : BadRequest(new { error = errors[0].Message }));
}
```

#### 2.2.5 Mapeamento de Perfis de Qualidade (novo arquivo)

**Arquivo:** `src/Discovery.Core/Configuration/QualityProfileMapping.cs`

```csharp
public static class QualityProfileMapping
{
    public static (int fps, int scalePercent, int jpegQuality, int webpQuality)
        GetParameters(QualityProfile profile) => profile switch
    {
        QualityProfile.Ultra    => (30, 100, 92, 90),
        QualityProfile.High     => (15, 100, 75, 75),
        QualityProfile.Medium   => (10, 75,  60, 65),
        QualityProfile.Low      => (5,  50,  40, 50),
        QualityProfile.UltraLow => (2,  30,  25, 35),
        _ => (15, 100, 75, 75)
    };

    public static string GetLabel(QualityProfile profile) => profile switch
    {
        QualityProfile.Ultra    => "Ultra (30 FPS, original, JPEG 92%)",
        QualityProfile.High     => "Alta (15 FPS, original, JPEG 75%)",
        QualityProfile.Medium   => "Média (10 FPS, 75% escala, JPEG 60%)",
        QualityProfile.Low      => "Baixa (5 FPS, 50% escala, JPEG 40%)",
        QualityProfile.UltraLow => "Mínima (2 FPS, 30% escala, JPEG 25%)",
        _ => "Desconhecido"
    };
}
```

---

### 2.3 Fase 2 — Frontend: Seletor de Qualidade (P0)

#### 2.3.1 Componente de Controle de Qualidade

Adicionar um dropdown na barra superior (`div.flex.items-center.gap-1.px-4.py-1.5.bg-slate-850`):

```
[ Tela ] [ Terminal ] [ Arquivos ] [ Proxy ]    Transport: NATS | [Qualidade: Alta ▼] | [Codec: JPEG ▼] | 9 FPS
```

Onde `[Qualidade: Alta ▼]` e `[Codec: JPEG ▼]` são dropdowns clicáveis que:

1. Exibem o valor atual
2. Ao clicar, mostram as opções disponíveis
3. Ao selecionar, chamam `PUT /api/v1/remote-sessions/{agentId}/{sessionId}/quality`
4. Atualizam o estado local imediatamente (optimistic update)

#### 2.3.2 Hook `useRemoteSessionQuality`

```typescript
interface QualityState {
  profile: 'Ultra' | 'High' | 'Medium' | 'Low' | 'UltraLow';
  codec: 'Jpeg' | 'WebP' | 'H264';
  fps: number;
  isChanging: boolean;
}

function useRemoteSessionQuality(agentId: string, sessionId: string) {
  const [quality, setQuality] = useState<QualityState>(...);

  const changeQuality = async (profile: QualityProfile, codec?: RemoteCodec) => {
    setQuality(prev => ({ ...prev, isChanging: true }));
    await api.put(`/remote-sessions/${agentId}/${sessionId}/quality`, {
      quality: profile,
      codec: codec,
    });
    setQuality(prev => ({ ...prev, profile, codec: codec ?? prev.codec, isChanging: false }));
  };

  return { quality, changeQuality };
}
```

---

### 2.4 Fase 3 — Qualidade Adaptativa Automática (P1)

#### 2.4.1 Serviço `AdaptiveQualityService` (Background Service)

**Arquivo:** `src/Discovery.Infrastructure/Services/Remote/AdaptiveQualityService.cs`

```csharp
public class AdaptiveQualityService : BackgroundService
{
    // A cada 5s, analisa métricas agregadas (RTT, jitter, perda de pacotes, bandwidth)
    // Se RTT > 300ms ou bandwidth < 500kbps → reduz qualidade (High→Medium→Low)
    // Se RTT < 50ms e bandwidth > 5mbps → aumenta qualidade
    // Usa histerese para evitar oscilações (ex: só sobe após 15s estável)
}
```

#### 2.4.2 Coleta de Métricas

O `AckFrameCommandHandler` já recebe os dados. Precisamos persistir métricas agregadas:

- **Opção A:** Armazenar em cache Redis (sliding window 30s) — recomendado
- **Opção B:** Armazenar em memória (`ConcurrentDictionary`) — mais simples

```csharp
// Métricas agregadas por sessão
public record SessionMetrics(
    double AvgRttMs,
    double AvgJitterMs,
    double AvgBandwidthKbps,
    double PacketLossPercent,
    int Fps,
    DateTime LastUpdate
);
```

#### 2.4.3 Configuração Adicional

```csharp
public class RemoteAccessQualityOptions
{
    public string DefaultProfile { get; set; } = "high";
    public bool AdaptiveEnabled { get; set; } = true;
    public int AdaptiveIntervalSeconds { get; set; } = 5;       // NOVO
    public int AdaptiveHysteresisSeconds { get; set; } = 15;    // NOVO
    public int MinFps { get; set; } = 5;
    public int MaxFps { get; set; } = 30;
    public string DefaultCodec { get; set; } = "auto";

    // NOVO: thresholds para adaptação
    public AdaptiveThresholds Thresholds { get; set; } = new();
}

public class AdaptiveThresholds
{
    public double HighLatencyMs { get; set; } = 300;       // Reduz se RTT > isso
    public double LowLatencyMs { get; set; } = 50;         // Aumenta se RTT < isso
    public double LowBandwidthKbps { get; set; } = 500;    // Reduz se bandwidth < isso
    public double HighBandwidthKbps { get; set; } = 5000;  // Aumenta se bandwidth > isso
}
```

---

### 2.5 Fase 4 — Métricas em Tempo Real na UI (P2)

#### 2.5.1 Evento NATS de Métricas

Publicar periodicamente no subject `remote.session.{sessionId}.event`:

```json
{
  "eventType": "metrics",
  "sessionId": "...",
  "avgRttMs": 45.2,
  "avgJitterMs": 3.1,
  "estimatedBandwidthKbps": 3200,
  "fps": 15,
  "quality": "High",
  "codec": "Jpeg",
  "timestampUtc": "2026-07-29T19:00:00Z"
}
```

#### 2.5.2 Gráfico de Qualidade na UI

Adicionar um mini-indicador visual:

- Barra de latência (verde < 50ms, amarelo < 150ms, vermelho > 300ms)
- Indicador de bandwidth
- Histórico de FPS (mini sparkline)

---

## 3. Resumo das Tarefas

| Fase | ID  | Tarefa                                                            | Prioridade | Esforço |
| ---- | --- | ----------------------------------------------------------------- | ---------- | ------- |
| 1    | T1  | Criar `ChangeRemoteSessionQualityCommand` + DTO                   | P0         | 30min   |
| 1    | T2  | Criar `ChangeRemoteSessionQualityCommandHandler`                  | P0         | 45min   |
| 1    | T3  | Adicionar `UpdateQualityAsync` no `RemoteSessionManager`          | P0         | 20min   |
| 1    | T4  | Adicionar `IRemoteSessionManager.UpdateQualityAsync` na interface | P0         | 10min   |
| 1    | T5  | Adicionar endpoint `PUT .../quality` no Controller                | P0         | 15min   |
| 1    | T6  | Criar `QualityProfileMapping` com parâmetros por perfil           | P0         | 30min   |
| 2    | T7  | Frontend: Dropdown de Quality Profile na barra superior           | P0         | 2h      |
| 2    | T8  | Frontend: Dropdown de Codec na barra superior                     | P0         | 1h      |
| 2    | T9  | Frontend: Hook `useRemoteSessionQuality`                          | P0         | 1h      |
| 2    | T10 | Frontend: Chamada PUT ao trocar qualidade                         | P0         | 30min   |
| 3    | T11 | Criar `AdaptiveQualityService` (BackgroundService)                | P1         | 4h      |
| 3    | T12 | Coleta de métricas (Redis sliding window)                         | P1         | 2h      |
| 3    | T13 | Config `AdaptiveThresholds` no `RemoteAccessQualityOptions`       | P1         | 30min   |
| 3    | T14 | Integrar `AdaptiveQualityService` no `Program.cs`                 | P1         | 15min   |
| 4    | T15 | Publicar evento `metrics` no subject `.event`                     | P2         | 1h      |
| 4    | T16 | Frontend: Indicador visual de latência/bandwidth                  | P2         | 2h      |
| 4    | T17 | Testes de integração para o fluxo de quality change               | P1         | 2h      |

---

## 4. Observações Importantes

1. **O Agent Go PRECISA implementar o tratamento do `action: "quality"`** no `remote.session.{sessionId}.control`. Sem isso, o backend envia o comando mas o agent ignora. Verificar repositório `C:\Projetos\Discovery`.

2. **Codec H264** só funciona com WebRTC (requer `RemoteTransport.Webrtc`). Para NATS, usar JPEG ou WebP. O handler deve validar essa restrição.

3. **O campo `FramesSent`/`BytesSent`** no `RemoteSession` já existe mas não é atualizado durante a sessão — o `AckFrameCommandHandler` só loga. Podemos incrementar esses contadores para dashboard futuro.

4. **Segurança:** O endpoint de quality change deve usar `[RemoteSessionAuthorize]` para garantir que só o dono da sessão pode alterar.

5. **Responsividade:** O optimistic update no frontend é essencial — a troca de qualidade deve parecer instantânea, mesmo que o comando NATS demore alguns ms.

---

## 5. Próximos Passos Recomendados

1. **Imediato (hoje):** Fase 1 completa (backend) + Fase 2 (frontend) = ~6h de trabalho
2. **Amanhã:** Fase 3 (qualidade adaptativa) + testes = ~7h
3. **Esta semana:** Fase 4 (métricas visuais) = ~3h
4. **Coordenação:** Verificar com time do Agent Go se o handler de `action: "quality"` já existe ou precisa ser implementado
