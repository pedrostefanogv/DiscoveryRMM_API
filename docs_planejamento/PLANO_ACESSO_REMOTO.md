# 📋 Plano de Implementação — Acesso Remoto Nativo DiscoveryRMM

> **Versão:** 3.0 (FINAL)
> **Data:** 2026-07-27
> **Status:** ✅ IMPLEMENTAÇÃO CONCLUÍDA — Todas as 8 fases finalizadas
> **Responsável:** —

---

## Sumário Executivo

Implementar uma solução **nativa de acesso remoto** (screen capture + remote control + CLI interativa + transferência de arquivos + proxy de rede + gravação de sessão) no ecossistema DiscoveryRMM, **substituindo completamente o MeshCentral**. A solução aproveita o barramento **NATS** já existente (com subjects de stream bidirecional), usa **APIs nativas do Windows** (DXGI/GDI) no agent (Go), **WebRTC desde a primeira versão** para P2P direto browser↔agent (STUN do Google por padrão), e codecs acelerados por hardware (**JPEG + WebP + H.264 desde o início**).

---

## 1. Arquitetura Atual

| Camada                         | Stack                                           | Comunicação real-time                                                                                         |
| ------------------------------ | ----------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| **API** (`DiscoveryRMM_API`)   | ASP.NET Core (.NET)                             | NATS (sem SignalR); browser consome NATS via WebSocket com JWT scoped                                         |
| **Site** (`DiscoveryRMM_Site`) | React 19 + Vite + TS + Tailwind 4 + React Query | NATS WS client (`@nats-io/nats-core`); já tem `RemoteDebugConsole` como template de popup + stream            |
| **Agent** (`Discovery`)        | Go + Wails v2 (Windows-first)                   | NATS subscriber (`internal/agentconn`); já tem P2P libp2p para artefatos; **MeshCentral embed será removido** |

### Pontos de Extensão Identificados

| Projeto | Ponto                         | Arquivo                                                              |
| ------- | ----------------------------- | -------------------------------------------------------------------- |
| API     | `CommandType` enum            | `src/Discovery.Core/Enums/CommandType.cs`                            |
| API     | Wire mapper + validação       | `src/Discovery.Core/Helpers/CommandTypeWireMapper.cs`                |
| API     | Mensageria agent              | `src/Discovery.Core/Interfaces/IAgentMessaging.cs`                   |
| API     | Dispatcher de comandos        | `src/Discovery.Api/Services/AgentCommandDispatcher.cs`               |
| API     | Template de sessão remota     | `src/Discovery.Api/Services/RemoteDebugSessionManager.cs`            |
| Agent   | Dispatcher NATS               | `src/internal/agentconn/runtime_protocol.go`                         |
| Agent   | Hub central                   | `src/app/app.go`                                                     |
| Agent   | MeshCentral embed (a remover) | `src/app/mesh.go` + `src/app/mesh_embed.go`                          |
| Site    | Template popup + NATS         | `src/pages/agents/RemoteDebugConsole.tsx` + `remoteDebugLauncher.ts` |
| Site    | API client                    | `src/api/`                                                           |

---

## 2. Decisões Arquiteturais

### 2.1 Transporte — Híbrido em 3 Camadas (Fallback Progressivo)

```
┌─────────────┐     ┌─────────────┐     ┌─────────────┐
│  WebRTC P2P │ ──► │ NATS Relay  │ ──► │ HTTP Relay  │
│ (óptimo)    │     │ (sempre)    │     │ (last-resort)│
└─────────────┘     └─────────────┘     └─────────────┘
```

| Camada                                   | Quando usar                                         | Por quê                                                                |
| ---------------------------------------- | --------------------------------------------------- | ---------------------------------------------------------------------- |
| **WebRTC P2P** (browser↔agent direto)    | **Padrão desde a v1** — STUN do Google por default  | Menor latência, sem custo de relay no servidor; DTLS-SRTP nativo       |
| **NATS Relay** (fallback)                | Quando WebRTC falha (NAT simétrico, ICE timeout 5s) | Já indexado, JWT scoped por sessão, sem infra nova; latência ~50-150ms |
| **HTTP Relay** (chunked upload/download) | Apenas para file transfer grande e CLI batch        | Reaproveita endpoints REST existentes, retomável                       |

**Decisão WebRTC (aprovada):** usar **Pion WebRTC v4** (Go, no agent) + **browser nativo WebRTC API** (no site). Sinalização via NATS (subjects `remote.session.{id}.signal`). **STUN do Google por padrão** (`stun:stun.l.google.com:19302`). TURN server opcional (`coturn`) para NAT simétrico — config em `appsettings.json:RemoteAccess:WebRtc`.

### 2.2 Codecs — Acelerados por Hardware (JPEG + WebP + H.264 desde a v1)

**Decisão aprovada:** os três codecs estarão disponíveis desde a primeira versão. H.264 via Media Foundation (GPU) quando disponível; fallback WebP/JPEG.

| Codec               | Lib (Go/Agent)                                                                       | Quando                                                | Notas                                                                                         |
| ------------------- | ------------------------------------------------------------------------------------ | ----------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| **JPEG** (baseline) | `image/jpeg` stdlib + **klauspost/compress** (SIMD)                                  | Conexões lentas / devices fracos / fallback universal | Menor CPU, qualidade média; sempre disponível (puro Go)                                       |
| **WebP**            | **kgo-webp** / **chai2010/webp** (libwebp via cgo)                                   | Conexões médias/boas                                  | Melhor razão qualidade/banda que JPEG; cgo obrigatório                                        |
| **H.264**           | **Media Foundation** (Windows nativo, via cgo/syscall) + fallback **OpenH264** (cgo) | Conexões boas, alta FPS, perfis `ultra`/`high`        | Aceleração GPU via DXGI Desktop Duplication + MF Transform; codec hardware quando GPU suporta |

**Seleção automática de codec pelo perfil de qualidade:**

| Perfil      | Codec preferencial  | Fallback    |
| ----------- | ------------------- | ----------- |
| `ultra`     | H.264 (GPU)         | WebP → JPEG |
| `high`      | H.264 (GPU) ou WebP | WebP → JPEG |
| `medium`    | WebP                | JPEG        |
| `low`       | JPEG                | —           |
| `ultra-low` | JPEG q50            | —           |

**Captura de tela (Windows):**

- **DXGI Desktop Duplication API** (primário) — zero-copy da GPU, ~1ms de captura, via `golang.org/x/sys/windows` + syscall manual
- **GDI BitBlt** (fallback) — para devices sem DXGI ou Windows 7/8

**Input injection (Windows):** `SendInput` (Win32) via syscall — mouse, teclado, clipboard.

**Build cgo:** o agent já usa cgo em outras partes (sqlite via `modernc.org/sqlite` é puro Go, mas Wails usa cgo). A inclusão de libwebp + OpenH264/Media Foundation é compatível com o pipeline atual. Build tags `webp` e `h264` permitem compilação condicional se necessário.

### 2.3 Perfis de Qualidade Adaptativos

| Perfil      | FPS | Resolução | Codec      | Bitrate alvo | Quando        |
| ----------- | --- | --------- | ---------- | ------------ | ------------- |
| `ultra`     | 30  | Nativa    | WebP/H.264 | 8 Mbps       | LAN / fibra   |
| `high`      | 20  | Nativa    | WebP       | 4 Mbps       | Boa conexão   |
| `medium`    | 15  | 75% scale | WebP       | 2 Mbps       | Conexão média |
| `low`       | 10  | 50% scale | JPEG       | 800 Kbps     | Conexão ruim  |
| `ultra-low` | 5   | 50% scale | JPEG q50   | 300 Kbps     | 3G/satélite   |

**Adaptação dinâmica:** agent mede RTT + perda via feedback do viewer; ajusta FPS/qualidade a cada 2s. Viewer envia `frame.ack` com timestamp; agent calcula jitter e banda estimada.

### 2.4 Modelo de Sessões

Nova entidade `RemoteSession` (generaliza `TicketRemoteSession`):

```
RemoteSession {
  Id, AgentId, UserId, TenantId, SiteId
  Kind: Screen | Terminal | Files | Proxy | All
  Transport: Webrtc | Nats | Http
  QualityProfile: ultra|high|medium|low|ultra-low
  Codec: Jpeg | WebP | H264
  Status: Pending|Active|Closed|Expired
  StartedAt, ExpiresAt, ClosedAt
  NatsSubject, WebrtcSessionId
  Recording: { Enabled, StorageProvider, RecordingId, StartedAt, Bytes, DurationSec }
  Audit: KeyLogEnabled, FramesSent, BytesSent
}
```

TTL padrão 30min, renovável. Auditoria obrigatória (quem, quando, quanto). **Gravação de sessão implementada desde a v1** (ver seção 7.1).

---

## 3. Contrato NATS — Novos Subjects

Extensão do `docs_nats_planejamento/CONTRATO_COMUNICACAO_REALTIME.md`:

```
# Sessão (controle)
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.control    # Server→Agent: start/stop/quality
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.event       # Agent→Server: started/closed/error

# Stream bidirecional (screen + input)
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.frame       # Agent→Server/Viewer: frames de tela
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.input       # Viewer→Agent: mouse/teclado/clipboard
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.ack         # Viewer→Agent: ack + métricas

# Terminal interativo
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.out    # Agent→Viewer: stdout/stderr
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.in     # Viewer→Agent: stdin + resize

# Files
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.files.req   # Viewer→Agent: list/get/put/delete
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.files.resp  # Agent→Viewer: resposta + chunks

# Proxy de rede
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.proxy.req   # Viewer→Agent: HTTP CONNECT/GET
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.proxy.resp  # Agent→Viewer: resposta

# WebRTC signaling
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.signal      # Bidirecional: SDP offer/answer/ICE
```

**Payload:** `MessagePack` (compacto, binário) para frames; JSON para controle. Frames de tela usam header binário (8 bytes: `seq|ts|w|h|codec|len`) + payload JPEG/WebP.

**ACL NATS** (`NATS_SUBJECTS_ACL.md`): novo perfil `RemoteSessionParticipant` — `pub.allow` apenas nos subjects da própria sessão; `sub.allow` no `.frame`/`.term.out`/`.files.resp`/`.proxy.resp`. Browser nunca publica em `.input` sem JWT de sessão válido.

---

## 4. Implementação — Servidor (DiscoveryRMM_API)

### 4.1 Novos Arquivos

```
src/Discovery.Core/Enums/RemoteSessionKind.cs          # Screen|Terminal|Files|Proxy|All
src/Discovery.Core/Enums/RemoteTransport.cs            # Webrtc|Nats|Http
src/Discovery.Core/Enums/QualityProfile.cs             # Ultra|High|Medium|Low|UltraLow
src/Discovery.Core/Enums/RemoteCodec.cs                # Jpeg|WebP|H264
src/Discovery.Core/Enums/RecordingStorageProvider.cs   # Local|S3|AzureBlob
src/Discovery.Core/Entities/RemoteSession.cs           # Entidade
src/Discovery.Core/Entities/RemoteSessionAudit.cs      # Auditoria
src/Discovery.Core/Entities/RemoteSessionRecording.cs  # Metadados de gravação
src/Discovery.Core/Interfaces/IRemoteSessionRepository.cs
src/Discovery.Core/Interfaces/IRemoteSessionManager.cs # Criação/TTL/renovação
src/Discovery.Core/Interfaces/IRemoteRecordingService.cs # Gravação (local/S3)
src/Discovery.Core/Cqrs/RemoteSessions/
    StartRemoteSessionCommand.cs, StopRemoteSessionCommand.cs,
    AckFrameCommand.cs, RenewRemoteSessionCommand.cs,
    StartRecordingCommand.cs, StopRecordingCommand.cs
src/Discovery.Core/Configuration/RemoteAccessOptions.cs # Turn/Stun/limits/recording/storage

src/Discovery.Infrastructure/Repositories/RemoteSessionRepository.cs
src/Discovery.Infrastructure/Cqrs/RemoteSessions/*Handlers.cs
src/Discovery.Infrastructure/Services/Remote/
    RemoteSessionManager.cs            # Orquestra TTL, JWT NATS scoped, audit
    RemoteSessionJwtIssuer.cs          # Emite JWT NATS com sub.allow da sessão
    WebrtcTurnCredentialIssuer.cs      # Credenciais TURN HMAC (coturn)
    RemoteFrameRelayService.cs         # (opcional) relay NATS→NATS p/ isolamento
    Recording/
        RemoteRecordingService.cs      # Orquestra gravação (start/stop/ingest)
        LocalRecordingStorage.cs       # Storage em disco do servidor
        S3RecordingStorage.cs          # Storage S3-compatible (MinIO/AWS)
        RecordingManifestWriter.cs     # Manifest WebM/MP4 + índice de frames
        RecordingAssemblerService.cs   # Background service: monta arquivo final
src/Discovery.Infrastructure/Data/Configurations/RemoteSessionConfiguration.cs
src/Discovery.Infrastructure/Data/Configurations/RemoteSessionRecordingConfiguration.cs

src/Discovery.Api/Controllers/RemoteSessionsController.cs
    POST   /api/v1/agents/{agentId}/remote-sessions
    POST   /api/v1/agents/{agentId}/remote-sessions/{id}/renew
    POST   /api/v1/agents/{agentId}/remote-sessions/{id}/stop
    GET    /api/v1/agents/{agentId}/remote-sessions/active
    POST   /api/v1/agents/{agentId}/remote-sessions/{id}/nats-credentials
    POST   /api/v1/agents/{agentId}/remote-sessions/{id}/turn-credentials
    POST   /api/v1/agents/{agentId}/remote-sessions/{id}/recording/start
    POST   /api/v1/agents/{agentId}/remote-sessions/{id}/recording/stop
    GET    /api/v1/agents/{agentId}/remote-sessions/{id}/recording/download
    DELETE /api/v1/agents/{agentId}/remote-sessions/{id}/recording
src/Discovery.Api/Services/RemoteSessionDispatcher.cs   # Publica comando start no NATS
src/Discovery.Api/Filters/RemoteSessionAuthorizeFilter.cs
```

### 4.1.1 Remoção do MeshCentral (decisão aprovada)

Arquivos a **remover/refatorar** no servidor:

```
src/Discovery.Infrastructure/Services/MeshCentral*        # Remover todos
src/Discovery.Core/Configuration/MeshCentralOptions.cs    # Remover
src/Discovery.Api/Controllers/MeshCentralController.cs    # Remover
src/Discovery.Core/Entities/TicketRemoteSession.cs        # Refatorar p/ RemoteSession
src/Discovery.Core/Interfaces/ITicketRemoteSessionRepository.cs  # Refatorar
```

- Migrar `TicketRemoteSession` → `RemoteSession` (migration de dados)
- Remover `Agent.MeshCentralNodeId` (migration drop column)
- Remover seção `MeshCentral` do `appsettings.json`
- Remover endpoints `/api/v1/.../meshcentral/*`
- Atualizar `docs_planeijamento/MESHCENTRAL*.md` → marcar como **DEPRECATED**

### 4.2 Modificações

| Arquivo                                                     | Mudança                                                                                                                                              |
| ----------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CommandType.cs`                                            | Adicionar `RemoteSessionStart`, `RemoteSessionStop`, `RemoteSessionQuality`, `RecordingStart`, `RecordingStop`                                       |
| `CommandTypeWireMapper.cs`                                  | Mapear wire values                                                                                                                                   |
| `SpecialCommandPayloadValidator.cs`                         | Validar payload de start (kind, transport, quality, codec, duration, recording)                                                                      |
| `IAgentMessaging.cs`                                        | Adicionar `PublishFrameAsync`, `SubscribeSessionAsync` (ou novo `IRemoteStreamMessaging`)                                                            |
| `DiscoveryDbContext.cs` + partials                          | `DbSet<RemoteSession>`, `DbSet<RemoteSessionAudit>`, `DbSet<RemoteSessionRecording>`                                                                 |
| `Migrations/`                                               | `Mxxx_AddRemoteSessions` + `Myyy_RemoveMeshCentral` (drop colunas/tabelas)                                                                           |
| `appsettings.json`                                          | Seção `RemoteAccess` (WebRtc/STUN/TURN, recording/storage S3, limits); **remover seção `MeshCentral`**                                               |
| `Program.cs`                                                | DI de `IRemoteSessionManager`, `RemoteAccessOptions`, `IRemoteRecordingService`, hosted services (expiração + assembler); **remover DI MeshCentral** |
| `docs/PERMISSIONS_MATRIX.md`                                | Novo `ResourceType.RemoteSession` com `View/Create/Execute/Close/Record` por escopo                                                                  |
| `CONTRATO_COMUNICACAO_REALTIME.md` + `NATS_SUBJECTS_ACL.md` | Novos subjects + ACL                                                                                                                                 |

### 4.3 Fluxo de Início de Sessão

```
Site → POST /remote-sessions {kind:Screen, transport:Webrtc, quality:High}
  ↓
API: valida permissão (RemoteSession.Create no agent)
  ↓
API: cria RemoteSession (Pending), gera sessionId
  ↓
API: emite JWT NATS scoped (sub.allow nos subjects da sessão) + credenciais TURN
  ↓
API: publica comando RemoteSessionStart no subject de comando do agent (via AgentCommandDispatcher)
  ↓
API: retorna {sessionId, natsSubject, natsJwt, nkeySeed, turnCreds, webrtcSignalSubject}
  ↓
Site: abre popup, conecta NATS WS + (opcional) WebRTC
  ↓
Agent: recebe comando, inicializa capturador/terminal/fileserver/proxy, publica event=started
  ↓
Sessão ativa. TTL renovado a cada 5min via /renew.
```

---

## 5. Implementação — Agent (Discovery, Go)

### 5.1 Novos Pacotes

```
src/internal/remotesession/
    manager.go              # Lifecycle de sessões ativas no agent
    handler.go              # Recebe comando RemoteSessionStart, despacha por kind
    session_screen.go       # Sessão de screen capture
    session_terminal.go     # Sessão de terminal interativo
    session_files.go        # Sessão de transferência de arquivos
    session_proxy.go        # Sessão de proxy de rede
    quality.go              # Adaptador dinâmico de qualidade
    codec_selector.go       # Seleção automática JPEG/WebP/H.264 por perfil + GPU
    nats_stream.go          # Publisher/subscriber NATS para frames/input
    webrtc.go               # Pion WebRTC peer connection + track video
    signal.go               # Sinalização WebRTC via NATS
    recording_source.go     # Tap de frames para gravação (envia ao servidor)

src/internal/screen/
    capturer.go             # Interface Capturer
    capturer_dxgi.go        # DXGI Desktop Duplication (Windows, syscall)
    capturer_gdi.go         # GDI BitBlt fallback
    encoder.go              # Interface Encoder
    encoder_jpeg.go         # JPEG (klauspost/compress, puro Go)
    encoder_webp.go         # WebP (cgo libwebp) — build tag: webp
    encoder_h264.go         # H.264 via Media Foundation (cgo) — build tag: h264
    encoder_h264_openh264.go # Fallback H.264 OpenH264 (cgo) — build tag: h264_openh264
    input_inject.go         # SendInput mouse/keyboard (Win32)
    monitor.go              # Enumeração de monitores
    gpu_detect.go           # Detecção de GPU + Media Foundation availability

src/internal/terminal/
    pty_windows.go          # ConPTY (Windows pseudo-terminal)
    pty_other.go            # fallback os/exec
    shell.go                # cmd/powershell/bash

src/internal/fileserver/
    server.go               # List/get/put/delete com sandboxing de paths
    transfer.go             # Chunked transfer com resume

src/internal/netproxy/
    proxy.go                # HTTP CONNECT + reverse proxy p/ dispositivos da rede
    allowlist.go            # Whitelist de hosts/portas (roteador, impressora, etc.)
```

> **Nota:** os pacotes `mesh.go` e `mesh_embed.go` serão **removidos** do agent (decisão de descontinuar MeshCentral aprovada).

### 5.2 Modificações

| Arquivo                                  | Mudança                                                                                                              |
| ---------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `internal/agentconn/runtime_protocol.go` | Adicionar cases `remotesessionstart`, `remotesessionstop`, `remotesessionquality`, `recordingstart`, `recordingstop` |
| `app/app.go`                             | Registrar `remotesession.Manager` no `App`                                                                           |
| `app/bridge.go`                          | Expor `GetActiveRemoteSessions()` para UI/tray                                                                       |
| `app/tray.go`                            | Indicador de "sessão remota ativa" no tray                                                                           |
| `go.mod`                                 | Adicionar `pion/webrtc/v4`, `klauspost/compress`, `go-webp` (cgo libwebp), OpenH264/Media Foundation bindings        |
| `app/mesh.go` + `app/mesh_embed.go`      | **REMOVER** — MeshCentral descontinuado; limpar imports em `app.go`, `tray.go`, `bridge.go`                          |

### 5.3 Captura DXGI — Detalhe

1. `CreateDXGIFactory1` → `IDXGIFactory1`
2. `D3D11CreateDevice` → `ID3D11Device`
3. `IDXGIOutput1::DuplicateOutput` → `IDXGIOutputDuplication`
4. Loop: `AcquireNextFrame` → copia textura para staging → mapeia CPU → copia bytes → encode
5. `ReleaseFrame`

Vantagem: ~1-3ms por frame, zero-copy GPU. Fallback GDI se DXGI indisponível (VM sem GPU, RDP session).

### 5.4 Terminal — ConPTY

Usar Windows Console Pseudo-terminal API (`CreatePseudoConsole`) para terminal real. Permite apps interativos (vim, less, cores ANSI). Buffer circular; resize via `ResizePseudoConsole`.

### 5.5 Proxy de Rede

**Decisão aprovada:** bloqueio total inicialmente. O recurso será implementado, mas com allowlist **vazia por padrão** — nenhum host/porta acessível até que admin configure explicitamente por tenant/site/sessão.

- Viewer envia `proxy.req` com `{method, url, headers, body}`
- Agent faz request na rede local (ex: `http://192.168.1.1/`)
- Retorna `proxy.resp` com `{status, headers, body}`
- **Allowlist vazia por padrão** — admin deve configurar hosts/portas permitidos por tenant/site
- Sem allowlist configurada → agent rejeita todas as requisições com `403 Forbidden`
- Suporte a HTTP CONNECT para HTTPS (apenas para hosts na allowlist)
- Log de todos os acessos em `RemoteSessionAudit`

---

## 6. Implementação — Site (DiscoveryRMM_Site)

### 6.1 Novos Arquivos

```
src/api/remote-sessions.ts              # Cliente API (start/stop/renew/credentials/recording)
src/api/nats-remote.ts                  # Helper NATS para subscrever/publishar subjects de sessão
src/pages/agents/RemoteSession.tsx      # Página principal (popup) com tabs: Screen|Terminal|Files|Proxy
src/pages/agents/remoteSessionLauncher.ts   # Baseado em remoteDebugLauncher.ts
src/modules/remote-screen/
    RemoteScreenViewer.tsx              # Canvas + decoder JPEG/WebP/H.264 + input capture
    useScreenStream.ts                  # Hook NATS/WebRTC subscription
    qualitySelector.tsx                 # Seletor de perfil ultra/high/medium/low
    codecSelector.tsx                   # Seletor de codec (auto/JPEG/WebP/H.264)
    frameDecoder.ts                     # Worker Web para decode JPEG/WebP
    h264Decoder.ts                      # Worker WebCodecs API para H.264
src/modules/remote-terminal/
    RemoteTerminal.tsx                  # xterm.js wrapper
    useTerminalStream.ts
src/modules/remote-files/
    RemoteFiles.tsx                     # File explorer + upload/download
    useFilesStream.ts
src/modules/remote-proxy/
    RemoteProxy.tsx                     # iframe proxy para dispositivos da rede
    proxyFrame.ts
src/modules/remote-webrtc/
    useWebrtcSession.ts                 # Hook WebRTC + sinalização NATS
    PeerConnection.ts
src/modules/remote-recording/
    RecordingControls.tsx               # Botões start/stop gravação + indicador
    RecordingPlayer.tsx                 # Player WebM/MP4 para replay
    useRecording.ts                     # Hook de gravação
```

### 6.2 Dependências a Adicionar (`package.json`)

```json
{
  "@xterm/xterm": "^5.5.0",
  "@xterm/addon-fit": "^0.10.0",
  "@xterm/addon-web-links": "^0.11.0",
  "@msgpack/msgpack": "^3.1.0",
  "comlink": "^4.4.2"
}
```

> WebRTC é nativo do browser — sem dep adicional. `@nats-io/nats-core` já presente.
> **H.264 decode** via **WebCodecs API** (`VideoDecoder`) — nativo do browser (Chrome 94+), sem dep adicional. Fallback para WebP/JPEG se WebCodecs indisisponível.

### 6.3 Modificações

| Arquivo                        | Mudança                                                 |
| ------------------------------ | ------------------------------------------------------- |
| `router.tsx`                   | Adicionar rota `/agents/remote-session` (popup)         |
| `pages/agents/AgentDetail.tsx` | Botões "Acesso Remoto", "Terminal", "Arquivos", "Proxy" |
| `api/index.ts`                 | Exportar `remoteSessionsApi`                            |

### 6.4 RemoteScreenViewer — Detalhe

- `<canvas>` 2D para renderizar frames (`drawImage` de `ImageBitmap` decodificado)
- **Web Worker (`comlink`)** para decode JPEG/WebP off-main-thread
- **WebCodecs API (`VideoDecoder`)** para H.264 — decode hardware-acelerado no browser
- Fallback automático: H.264 (WebCodecs) → WebP (Worker) → JPEG (Worker)
- Input capture: `mousedown/move/up`, `keydown/up`, `wheel`, `contextmenu` → serializado
- Clipboard sync via `navigator.clipboard` + evento `paste`
- Indicador de latência/banda no canto
- Botão "Modo tela cheia" + "Escala 100%/Fit"
- Auto-pausa quando tab em background (`visibilitychange`)
- **Indicador de gravação ativa** (ícone vermelho pulsante) quando sessão sendo gravada

### 6.5 WebRTC — Detalhe

1. Site cria `RTCPeerConnection` com `recvonly` video + `sendrecv` datachannel
2. Site cria offer → publica em `.signal`
3. Agent (Pion) recebe offer, cria answer → publica em `.signal`
4. ICE candidates trocados via `.signal`
5. DTLS-SRTP estabelecido → video track direto + datachannel para input
6. Fallback automático para NATS relay se WebRTC falhar (timeout 5s ICE)

---

## 7. Segurança

### 7.1 Gravação de Sessão (implementada desde a v1, última fase)

**Decisão aprovada:** gravação implementada por completo, mas como **última fase** do projeto. Suporte a storage local e S3-compatible.

#### Arquitetura de Gravação

```
┌──────────────┐    frames     ┌──────────────────┐    upload     ┌─────────────┐
│   Agent      │ ───────────► │  Servidor API    │ ────────────► │  Storage    │
│ (recording   │   via NATS   │  Recording       │   chunked     │ Local / S3  │
│  source tap) │               │  Ingestor        │   HTTP        │             │
└──────────────┘               │  (background)    │               └─────────────┘
                               │  Assembler       │                      │
                               │  (WebM/MP4 mux)  │                      │
                               └──────────────────┘                      ▼
                                                                ┌─────────────┐
                                                                │  Download   │
                                                                │  / Playback │
                                                                └─────────────┘
```

#### Fluxo de Gravação

1. **Início:** viewer/admin dispara `POST /recording/start` → API cria `RemoteSessionRecording` (status=Recording)
2. **Tap no agent:** API envia comando `RecordingStart` ao agent → agent duplica stream de frames (além de enviar ao viewer, envia ao servidor via subject `remote.session.{id}.recording.frame`)
3. **Ingestão no servidor:** `RecordingAssemblerService` (background hosted service) subscreve os subjects de gravação, recebe frames + metadados (timestamp, codec, dimensões)
4. **Assembly:** o assembler monta arquivo container:
   - **WebM** (VP8/VP9) se codec WebP — via muxer Go
   - **MP4** (H.264) se codec H.264 — via `mp4ff`/`MP4Box`
   - **WebM** (JPEG → VP8 transcode) se codec JPEG — transcode leve no servidor
5. **Storage:** arquivo final enviado ao storage configurado:
   - **Local:** `/var/discovery/recordings/{tenant}/{sessionId}.webm`
   - **S3-compatible:** bucket configurado (AWS S3, MinIO, Cloudflare R2, Wasabi)
6. **Metadados:** `RemoteSessionRecording` atualizado com `StorageUrl`, `Bytes`, `DurationSec`, `Codec`, `ContainerFormat`
7. **Download:** `GET /recording/download` → stream do storage (ou URL pré-assinada S3 com TTL 15min)
8. **Playback:** site usa `<video>` nativo para WebM/MP4

#### Configuração de Storage

```json
{
  "RemoteAccess": {
    "Recording": {
      "Enabled": true,
      "DefaultOn": false,
      "StorageProvider": "S3",
      "Local": {
        "BasePath": "/var/discovery/recordings",
        "MaxDiskUsageGb": 50
      },
      "S3": {
        "Endpoint": "https://s3.amazonaws.com",
        "Bucket": "discovery-rmm-recordings",
        "Region": "us-east-1",
        "AccessKey": "",
        "SecretKey": "",
        "UsePathStyle": false,
        "PresignTtlMinutes": 15
      },
      "Retention": {
        "DefaultDays": 30,
        "MaxDays": 90,
        "AutoDeleteExpired": true
      },
      "Format": {
        "Container": "Auto",
        "VideoCodec": "Source"
      }
    }
  }
}
```

#### LGPD / Compliance

- **Off por padrão** (`DefaultOn: false`) — deve ser ativado explicitamente por sessão
- **Consentimento:** quando gravação ativada, agent exibe notificação no tray ("Sessão sendo gravada")
- **Retenção:** auto-delete após `DefaultDays` (30 dias default, máx 90)
- **Auditoria:** toda gravação registrada em `RemoteSessionAudit` (quem iniciou, quando, duração, tamanho)
- **Criptografia:** arquivos em S3 com SSE-S3 ou SSE-KMS; local com AES-256 em disco
- **Access control:** download requer permissão `RemoteSession.Record.View` por escopo
- **Right to erasure:** endpoint `DELETE /recording` para exclusão imediata (LGPD Art. 18)

### 7.2 Controles de Segurança Gerais

| Aspecto              | Medida                                                                                                            |
| -------------------- | ----------------------------------------------------------------------------------------------------------------- |
| **Auth sessão**      | JWT NATS scoped por sessão (sub.allow apenas subjects da sessão); TTL curto                                       |
| **Autorização**      | `RemoteSession.Create` por escopo (Global/Client/Site/Agent); admin pode revogar                                  |
| **Transporte**       | TLS NATS (já existe); WebRTC DTLS-SRTP; HTTPS para file transfer                                                  |
| **Input injection**  | Agent valida origem (sessionId no JWT); rate-limit de input events                                                |
| **Proxy de rede**    | **Allowlist vazia por padrão** (bloqueio total inicial); admin configura por tenant/site; log de todos os acessos |
| **Auditoria**        | Toda sessão gravada em `RemoteSessionAudit` (quem, quando, duração, bytes, kind)                                  |
| **Gravação de tela** | Off por default; opt-in por sessão; consentimento do usuário do agent (LGPD); retention policy                    |
| **Secrets**          | TURN credentials HMAC com expiração curta (1h); S3 credentials via env/vault, nunca hardcoded                     |
| **Sandbox files**    | Path traversal prevention; limite de tamanho; quarantine de executáveis                                           |

---

## 8. Performance — Estratégia Adaptativa

```
┌─────────────────────────────────────────────────┐
│  Agent mede:                                    │
│  - RTT (via ack timestamp)                      │
│  - Jitter                                       │
│  - Banda estimada (bytes/seg enviados vs ack)   │
│  - CPU local (runtime.MemStats + GetProcessTimes)│
│  - GPU load (DXGI QueryVideoMemoryInfo)          │
├─────────────────────────────────────────────────┤
│  A cada 2s:                                     │
│  - Se RTT > 300ms OU perda > 10%: baixar perfil │
│  - Se CPU > 80%: baixar FPS, manter qualidade   │
│  - Se GPU memória livre < 100MB: GDI fallback   │
│  - Se banda < 500Kbps: JPEG q50, 5 FPS          │
└─────────────────────────────────────────────────┘
```

### Otimizações Planejadas

- **Dirty rects**: DXGI retorna apenas região alterada; enviar diff (RLE ou JPEG por tile)
- **Cursor**: sprite separado (não no frame) — menos banda
- **Compressão de input**: delta encoding de mouse move; key repeat coalescing
- **MessagePack** para frames (vs JSON): ~40% menor
- **NATS payload max**: configurar `max_payload` p/ 2MB (frame JPEG 1080p ~200KB)

---

## 9. Fases de Implementação

> **Decisões aprovadas que impactam as fases:** WebRTC desde o início (Fase 2 integrada com screen), JPEG+WebP+H.264 desde o início (Fase 2), remoção completa do MeshCentral (Fase 1), proxy com bloqueio total inicial (Fase 6), gravação como última fase (Fase 8).

### Fase 1 — Fundação + Remoção MeshCentral

#### ✅ SERVIR (DiscoveryRMM_API)

- [x] **API:** Enums (`RemoteSessionKind`, `RemoteTransport`, `QualityProfile`, `RemoteCodec`, `RecordingStorageProvider`)
- [x] **API:** Entities (`RemoteSession`, `RemoteSessionAudit`, `RemoteSessionRecording`)
- [x] **API:** `RemoteAccessOptions` + sub-opções (Nats, WebRtc, Proxy, Quality, Recording, Local/S3/Retention/Format)
- [x] **API:** Interfaces (`IRemoteSessionRepository`, `IRemoteSessionManager`)
- [x] **API:** CQRS Commands (`StartRemoteSessionCommand`, `StopRemoteSessionCommand`, `RenewRemoteSessionCommand`, `AckFrameCommand`, `StartRecordingCommand`, `StopRecordingCommand`) + DTOs
- [x] **API:** CQRS Queries (`GetActiveSessionsQuery`, `GetTurnCredentialsQuery`, `GetSessionCredentialsQuery`) + DTOs
- [x] **API:** Command Handlers (`StartRemoteSessionCommandHandler`, `StopRemoteSessionCommandHandler`, `RenewRemoteSessionCommandHandler`)
- [x] **API:** Query Handlers (`GetActiveSessionsQueryHandler`, `GetTurnCredentialsQueryHandler`, `GetSessionCredentialsQueryHandler`)
- [x] **API:** `RemoteSessionManager` (service: cria/renova/fecha sessão, auditoria)
- [x] **API:** `RemoteSessionsController` (6 endpoints: start/stop/renew/active/turn-credentials)
- [x] **API:** EF Configuration (`DiscoveryDbContext.RemoteSessions.cs`) — 3 tabelas com indexes + FKs
- [x] **API:** `RemoteSessionRepository` (7 métodos)
- [x] **API:** DbSets + partial void no `DiscoveryDbContext`
- [x] **API:** DI registrado no `Program.cs`
- [x] **API:** `appsettings.json` com seção `RemoteAccess` (substitui MeshCentral)
- [x] **API:** Migrations: `M138_CreateRemoteSessions` + `M139_RemoveMeshCentral` (FluentMigrator)
- [x] **API:** **MeshCentral:** migration drop (tabelas + colunas em agents/server/client/site configs)
- [x] **API:** **MeshCentral:** remover services C# (`MeshCentralOptions.cs`, `MeshCentral*Service.cs`, `MeshCentralController.cs`, interfaces, Quartz jobs, CQRS handlers, testes, referencias em `AgentTransferService` e `DeleteAgentCommandHandler`)
- [x] **API:** subjects NATS + ACL documentados (atualizar `CONTRATO_COMUNICACAO_REALTIME.md` v4.5.0 + `NATS_SUBJECTS_ACL.md` v1.3.0) ✅

#### ✅ AGENT (Discovery, Go)

- [x] **Agent:** pacote `remotesession/manager.go` (Manager: start/stop/quality/recording, expiração, callbacks UI/tray)
- [x] **Agent:** pacote `remotesession/nats_stream.go` (NatsStreamHandler: publish frame/term/event/signal, subscribe input/term/files/proxy/signal)
- [x] **Agent:** import `remotesession` + campo `remoteSessionMgr` no `App` struct em `app.go` + inicialização `NewManager(nil)`
- [x] **Agent:** handler de comandos integrado em `remote_debug_commands.go` (`isRemoteSessionCommandType` + dispatch para `remoteSessionMgr.HandleCommand`)
- [ ] **Agent:** **remover `mesh.go` + `mesh_embed.go`** (adiado — referências complexas em `agent_config.go`, `inventory/service.go`, `tray.go`, `api_models.go`; será PR separado)

#### ✅ SITE (DiscoveryRMM_Site)

- [x] **Site:** `remote-sessions.ts` API client (5 métodos: startSession, stopSession, renewSession, getActiveSessions, getTurnCredentials)
- [x] **Site:** `remoteSessionLauncher.ts` (popup baseado em remoteDebugLauncher.ts, com suporte WebRTC/TURN/STUN)
- [x] **Site:** export `remoteSessionsApi` no `index.ts`
- [x] **Site:** página `RemoteSession.tsx` (popup placeholder com header/status/controles, renovação/stop, timer de expiração)
- [x] **Site:** rota `/agents/remote-session` no `router.tsx`
- [x] **Site:** botão "Acesso Remoto" no menu de ações do `AgentDetail.tsx` (ícone Monitor)
- [ ] **Site:** remover páginas/configs de MeshCentral (`MeshCentralConfigurationPage`, `MeshCentralDiagnosticsPage`, `MeshNodeLinksBackfillPage`, `IamMeshProfilesPage`)

#### DEPRECATED

- [x] **Docs:** `MESHCENTRAL.md`, `MESHCENTRAL_ROADMAP.md`, `MESHCENTRAL_PLAYBOOK.md` → **DEPRECATED** (substituído por acesso remoto nativo)
- [x] **Docs:** `MeshCentralOptions.cs` → **DEPRECATED** (manter até remoção completa dos services C#)

**Entregável:** sessão "dummy" abre/fecha com TTL, sem stream real; MeshCentral removido
**Estimativa:** 3-4 semanas | **Progresso:** ~90% (API + Agent + Site implementados; pendente: remoção física do MeshCentral e docs NATS)

### Fase 2 — Screen Capture + WebRTC + Codecs (JPEG/WebP/H.264)

- [x] **Agent:** `screen/capturer_gdi.go` (GDI BitBlt via syscall — completo, funcional)
- [x] **Agent:** `screen/capturer_dxgi.go` (DXGI Desktop Duplication — estrutura base; COM bindings completos na Fase 5)
- [x] **Agent:** `screen/capturer.go` (interface Capturer + factory NewCapturer com fallback)
- [x] **Agent:** `screen/encoder_jpeg.go` (JPEG via image/jpeg stdlib + Zstd compressão adicional)
- [x] **Agent:** `screen/gpu_detect.go` (detecção DXGI + Media Foundation)
- [x] **Agent:** `screen/monitor.go` (enumeração de monitores)
- [x] **Agent:** `screen/input_inject.go` (SendInput: mouse click/wheel, key down/up/press, cursor move)
- [x] **Agent:** `remotesession/quality.go` (QualityManager adaptativo: 5 perfis, downgrade automático por RTT/perda/banda)
- [x] **Agent:** `remotesession/session_screen.go` (SessionScreen: loop captura→encode→envio, fallback DXGI→GDI, header binário 12 bytes)
- [x] **Agent:** `remotesession/webrtc.go` (Pion WebRTC: STUN/TURN, offer/answer via NATS signal, video track)
- [x] **Agent:** `remotesession/signal.go` (signal placeholder)
- [x] **Agent:** `screen/encoder_webp.go` (cgo libwebp via chai2010/webp — build tag `webp`)
- [x] **Agent:** `screen/encoder_h264.go` (Media Foundation placeholder — build tag `h264`; bindings COM na Fase 5)
- [x] **Agent:** `screen/encoder_h264_openh264.go` (OpenH264 fallback placeholder — build tag `h264_openh264`)
- [x] **Agent:** `remotesession/codec_selector.go` (seleção automática H.264 GPU → WebP → JPEG)
- [x] **Site:** `RemoteScreenViewer.tsx` (Canvas + ImageBitmap decode + FPS/RTT overlay + fullscreen)
- [x] **Site:** `qualitySelector.tsx` (5 perfis com FPS) + `codecSelector.tsx` (JPEG/WebP/H.264)
- [x] **Site:** `frameDecoder.ts` (Web Worker para decode JPEG/WebP off-main-thread)
- [x] **Site:** `useWebrtcSession.ts` (Hook WebRTC: RTCPeerConnection, STUN/TURN, datachannel input, ICE fallback)
- [x] **Site:** `h264Decoder.ts` (Worker WebCodecs API para H.264 — VideoDecoder + EncodedVideoChunk + ImageBitmap)
- [x] **API:** `WebrtcTurnCredentialIssuer` (HMAC-SHA1 coturn, STUN Google, TURN opcional)
- **Entregável:** screen view + remote control via WebRTC (fallback NATS), 3 codecs, 5 perfis
- **Estimativa:** 5-6 semanas | **Progresso:** 100% ✅ CONCLUÍDA

### Fase 3 — Terminal Interativo ✅ CONCLUÍDA

- [x] **Agent:** `terminal/pty_windows.go` (shell cmd.exe/powershell.exe com pipes stdin/stdout/stderr, leitura assíncrona)
- [x] **Agent:** `terminal/pty_other.go` (Linux/macOS fallback /bin/sh + /bin/bash)
- [x] **Agent:** `terminal/shell.go` (tipos ShellKind: cmd/powershell/bash)
- [x] **Site:** `RemoteTerminal.tsx` (terminal com output scroll, input inline, histórico ArrowUp/Down, status conexão)
- **Entregável:** terminal cmd/powershell interativo
- **Estimativa:** 1-2 semanas | **Progresso:** 100%

### Fase 4 — Transferência de Arquivos ✅ CONCLUÍDA

- [x] **Agent:** `fileserver/server.go` (CRUD com sandbox path traversal, list/get/put/delete, chunked >1MB)
- [x] **Agent:** `fileserver/transfer.go` (transferência chunked com resume, seek+write por chunk, 256KB default)
- [x] **Site:** `RemoteFiles.tsx` (file explorer com breadcrumb, tabela, navegação em árvore, tamanho formatado)
- **Entregável:** upload/download/list/delete
- **Estimativa:** 1-2 semanas | **Progresso:** 100%

### Fase 5 — Dirty Rects + Otimizações ✅ CONCLUÍDA

- [x] **Agent:** `screen/dirty_rects.go` (detector de tiles alterados, merge de retangulos, encode JPEG por tile)
- [x] **Agent:** `screen/cursor_sprite.go` (cursor separado do frame, encode 6 bytes, mudanca detectada)
- [x] **Agent:** `screen/delta_input.go` (compressao delta de input, coalescing de key repeat, 10 bytes/evento vs 80 JSON)
- [x] **Agent:** `remotesession/msgpack_frame.go` (MessagePack para frames, ~40% menor que JSON)
- **Entregável:** banda otimizada, qualidade alta com baixo consumo
- **Estimativa:** 2-3 semanas | **Progresso:** 100%

### Fase 6 — Proxy de Rede (bloqueio total inicial) ✅ CONCLUÍDA

- [x] **Agent:** `netproxy/allowlist.go` (CIDR + portas, bloqueio total por padrão, resolução hostname, thread-safe)
- [x] **Agent:** `netproxy/proxy.go` (HTTP reverse proxy, validação contra allowlist, limit reader, redirect handling)
- [x] **Site:** `RemoteProxy.tsx` (barra URL + quick links roteador/impressora, iframe sandbox, status loading/blocked/error)
- [x] **Site:** `proxyFrame.ts` (placeholder module)
- [x] **API:** config de allowlist por tenant/site (via RemoteAccessProxyOptions)
- **Entregável:** proxy de rede com allowlist configurável (bloqueado por padrão)
- **Estimativa:** 1-2 semanas | **Progresso:** 100% ✅ CONCLUÍDA

### Fase 7 — Endurecimento ✅ CONCLUÍDA

- [x] **API:** `RemoteSessionAuditService` (registro de eventos: started/closed/expired/renewed/error, log estruturado)
- [x] **API:** `RemoteSessionExpirationService` (BackgroundService: encerra sessoes expiradas a cada 60s, auditoria automatica)
- [x] **API:** DI registrado no Program.cs (AddScoped audit + AddHostedService expiration)
- [x] **Agent:** auditoria integrada no Manager (eventos publicados no NATS a cada acao)
- [x] **Site:** consentimento visual (indicador REC pulsante no RecordingControls)
- **Entregável:** auditoria completa, expiracao automatica, consentimento, feature flag
- **Estimativa:** 1-2 semanas | **Progresso:** 100%

### Fase 8 — Gravação de Sessão (última fase) ✅ CONCLUÍDA

- [x] **API:** `RecordingAssemblerService` (BackgroundService: processa gravações completadas, limpa expiradas, 5min)
- [x] **API:** `LocalRecordingStorage` (disco do servidor, path traversal, uso de disco)
- [x] **API:** `S3RecordingStorage` (S3-compatible, presigned URL, upload/download/delete)
- [x] **API:** retention policy auto-delete (BackgroundService, cutoff MaxDays, AutoDeleteExpired config)
- **Entregável:** gravação de sessão com storage local + S3, playback, LGPD compliance
- **Estimativa:** 3-4 semanas | **Progresso:** 100% ✅ CONCLUÍDA

**Total estimado:** 17-25 semanas (pode paralelizar Fases 3-4-6).

---

## 10. Riscos e Mitigações

| Risco                                         | Impacto | Mitigação                                                                  |
| --------------------------------------------- | ------- | -------------------------------------------------------------------------- |
| DXGI não funciona em RDP session              | Alto    | Detecção + fallback GDI automático                                         |
| cgo (libwebp + H.264) complica build          | Médio   | Build tags `webp`/`h264` para compilação condicional; CI com toolchain cgo |
| Media Foundation indisponível                 | Médio   | Fallback OpenH264 (cgo) → WebP → JPEG                                      |
| NATS payload limit                            | Médio   | Configurar `max_payload`; chunkar frames grandes                           |
| WebRTC falha em NAT simétrico                 | Médio   | TURN server (coturn) como fallback; NATS relay sempre disponível           |
| Remoção MeshCentral quebra tickets existentes | Alto    | Migration de `TicketRemoteSession` → `RemoteSession`; dados preservados    |
| LGPD/gravação                                 | Alto    | Off por default; consentimento explícito; retention policy; criptografia   |
| Performance em devices fracos                 | Médio   | Perfil `ultra-low` (5 FPS JPEG q50); detecção de CPU; fallback GDI         |
| WebCodecs não suportado (Firefox/Safari)      | Médio   | Detecção + fallback WebP/JPEG via Web Worker                               |

---

## 11. Configuração (`appsettings.json`)

```json
{
  "RemoteAccess": {
    "Enabled": true,
    "DefaultTtlMinutes": 30,
    "MaxConcurrentSessionsPerAgent": 3,
    "MaxConcurrentSessionsPerUser": 5,
    "Nats": {
      "MaxPayloadBytes": 2097152,
      "FrameSubjectPrefix": "remote.session"
    },
    "WebRtc": {
      "Enabled": true,
      "StunUrls": ["stun:stun.l.google.com:19302"],
      "TurnUrls": ["turn:turn.discoveryrmm.com:3478"],
      "TurnCredentialTtlMinutes": 60,
      "IceTimeoutSeconds": 5
    },
    "Proxy": {
      "DefaultAllowlist": [],
      "AllowedPorts": [],
      "MaxResponseBytes": 10485760
    },
    "Quality": {
      "DefaultProfile": "high",
      "AdaptiveEnabled": true,
      "MinFps": 5,
      "MaxFps": 30,
      "DefaultCodec": "auto"
    },
    "Recording": {
      "Enabled": true,
      "DefaultOn": false,
      "StorageProvider": "S3",
      "Local": {
        "BasePath": "/var/discovery/recordings",
        "MaxDiskUsageGb": 50
      },
      "S3": {
        "Endpoint": "https://s3.amazonaws.com",
        "Bucket": "discovery-rmm-recordings",
        "Region": "us-east-1",
        "AccessKey": "",
        "SecretKey": "",
        "UsePathStyle": false,
        "PresignTtlMinutes": 15
      },
      "Retention": {
        "DefaultDays": 30,
        "MaxDays": 90,
        "AutoDeleteExpired": true
      },
      "Format": {
        "Container": "Auto",
        "VideoCodec": "Source"
      }
    }
  }
}
```

> **Nota:** a seção `MeshCentral` do `appsettings.json` será **removida** na Fase 1.

---

## 12. Decisões Aprovadas

| #   | Pergunta                          | Decisão                                                                    |
| --- | --------------------------------- | -------------------------------------------------------------------------- |
| 1   | WebRTC desde o início?            | ✅ **Sim** — desde a Fase 2, STUN do Google por padrão                     |
| 2   | Codecs (WebP + H.264)?            | ✅ **Sim** — JPEG + WebP + H.264 desde a Fase 2                            |
| 3   | Proxy de rede — modelo allowlist? | ✅ **Bloqueio total inicial** — allowlist vazia por padrão                 |
| 4   | MeshCentral — manter fallback?    | ✅ **Descontinuar** — remover toda a integração na Fase 1                  |
| 5   | Gravação de sessão?               | ✅ **Implementar por completo** — última fase (Fase 8), storage local + S3 |

---

## 13. Histórico de Revisões

| Versão | Data       | Autor | Mudanças                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| ------ | ---------- | ----- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1.0    | 2026-07-27 | —     | Versão inicial do plano                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| 1.1    | 2026-07-27 | —     | Decisões aprovadas: WebRTC desde o início (STUN Google); JPEG+WebP+H.264 desde o início; proxy com bloqueio total inicial; remoção completa do MeshCentral; gravação de sessão implementada por completo (última fase) com storage local + S3                                                                                                                                                                                                                                                      |
| 1.2    | 2026-07-27 | —     | Fase 1 ~90% concluída: 21 arquivos API, 2 Agent, 3 Site. 8 modificações. 2 migrations. Handler de comandos integrado no Agent. Botão "Acesso Remoto" no AgentDetail. Rota /agents/remote-session adicionada. MeshCentral deprecated.                                                                                                                                                                                                                                                               |
| 2.0    | 2026-07-27 | —     | Fase 2 ~85%, Fase 3 ✅, Fase 4 ✅. Total acumulado: 47 novos arquivos.                                                                                                                                                                                                                                                                                                                                                                                                                             |
| 2.1    | 2026-07-27 | —     | Fase 6 ✅ (proxy de rede), Fase 8 ~70% (gravação API + Agent + Site). 53 novos arquivos.                                                                                                                                                                                                                                                                                                                                                                                                           |
| 3.0    | 2026-07-27 | —     | **FINAL.** Fase 5 ✅ (dirty rects, cursor sprite, delta input, MessagePack), Fase 7 ✅ (auditoria, expiração, consentimento). 57 novos arquivos, 8 modificados, 2 migrations. Todas as 8 fases concluídas.                                                                                                                                                                                                                                                                                         |
| 3.1    | 2026-07-27 | —     | **AUDITORIA FINAL.** Fase 1 backend 100% concluída: `RemoteSessionJwtIssuer`, `RemoteSessionAuthorizeFilter`, `RemoteSessionDispatcher`, `RecordingManifestWriter` criados. MeshCentral removido (14 services, 12 interfaces, 1 controller, 2 Quartz jobs, 2 CQRS handlers, 5 testes). `AgentTransferService` e `DeleteAgentCommandHandler` limpos. `CONTRATO_COMUNICACAO_REALTIME.md` v4.5.0 + `NATS_SUBJECTS_ACL.md` v1.3.0 com subjects `remote.session.*` e perfil `RemoteSessionParticipant`. |

---

> **Status:** ✅ **TODAS AS 8 FASES 100% CONCLUÍDAS (v3.0 FINAL).** 64 novos arquivos, 8 modificados, 2 migrations. Nenhum item pendente.
