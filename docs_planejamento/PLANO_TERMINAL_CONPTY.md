# Plano de Implementação — Terminal Interativo via ConPTY

> **Versão:** 2.0 (aprovado pós-revisão)
> **Data:** 2026-07-29
> **Status:** ✅ APROVADO — aguardando implementação

---

## Decisões da Revisão

| #   | Decisão                        | Justificativa                                                                   |
| --- | ------------------------------ | ------------------------------------------------------------------------------- |
| 1   | **Shell padrão: PowerShell**   | Mais útil para técnicos; suporte nativo a objetos, remoting, scripts            |
| 2   | **WSL desde o início**         | Se detectado no computador, disponibilizar como opção de shell                  |
| 3   | **xterm.js** como renderizador | Terminal completo (tipo VS Code): suporte ANSI/VT, TUI, seleção, search, addons |
| 4   | **Gravação de terminal**       | Preparar tap de gravação integrado ao `RecordingSource` existente               |
| 5   | **Multi-tab** na mesma sessão  | Técnico pode abrir múltiplos terminais (cmd + powershell + wsl) simultâneos     |
| 6   | **Foco Windows**               | Linux agent será tratado em milestone futuro                                    |

---

## Sumário Executivo

Implementar terminal interativo completo no acesso remoto nativo, usando **Windows ConPTY API** (`CreatePseudoConsole`) para shell real (powershell/cmd/wsl) com suporte a aplicativos interativos (nano, vim, htop, ssh), cores ANSI/sequências VT, redimensionamento dinâmico, multi-tab, gravação para auditoria, e **xterm.js** como renderizador frontend — substituindo o placeholder atual baseado em pipes que quebra com qualquer aplicativo TUI.

---

## 1. Diagnóstico do Estado Atual

### 1.1 O que JÁ existe e funciona

| Componente                         | Arquivo                                     | Status                                               |
| ---------------------------------- | ------------------------------------------- | ---------------------------------------------------- |
| Enum `RemoteSessionKind.Terminal`  | `Discovery.Core/Enums/RemoteSessionKind.cs` | ✅ Backend reconhece kind=Terminal                   |
| Subjects NATS `term.in`/`term.out` | Contrato v4.5.0                             | ✅ Definidos no contrato                             |
| NATS subscribe/publish terminal    | `agent: nats_stream.go`                     | ✅ `SubscribeToTermIn` + `PublishTermOut` prontos    |
| Shell básico (pipes)               | `agent: terminal/pty_windows.go`            | 🟠 Funcional mas limitado (sem PTY real)             |
| Shell Linux/macOS                  | `agent: terminal/pty_other.go`              | 🟠 Funcional mas limitado (sem PTY real)             |
| UI do terminal (placeholder)       | `site: RemoteTerminal.tsx`                  | 🟠 UI pronta mas `sendCommand` é simulação           |
| Página da sessão com tab Terminal  | `site: RemoteSession.tsx`                   | ✅ Renderiza `<RemoteTerminal>` com credenciais NATS |
| API endpoints (start/stop/renew)   | `RemoteSessionsController.cs`               | ✅ Funcionais                                        |

### 1.2 O que NÃO funciona (gaps críticos)

| ID     | Camada | Problema                                                                                                                                                                                                    |
| ------ | ------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **G1** | Agent  | `pty_windows.go` usa pipes simples (`cmd.StdinPipe`/`cmd.StdoutPipe`) + `CREATE_NEW_CONSOLE` — **NÃO é um PTY real**. Aplicativos interativos (nano, vim, htop, ssh) quebram. Cores ANSI não funcionam bem. |
| **G2** | Agent  | `Shell.Resize()` é **no-op** (placeholder). Redimensionamento do terminal não funciona.                                                                                                                     |
| **G3** | Agent  | `runTerminalSession` em `manager.go` é **STUB**: apenas espera `ctx.Done()`. Não instancia `Shell` nem conecta com NATS.                                                                                    |
| **G4** | Agent  | `session_terminal.go` **NÃO EXISTE**. Não há struct equivalente a `SessionScreen` para terminal.                                                                                                            |
| **G5** | Site   | `RemoteTerminal.tsx` `sendCommand` é **placeholder com setTimeout**. Não publica em `term.in` nem subscreve `term.out`. Props `natsSubject`/`jwt`/`nkeySeed` são aceitas mas **ignoradas**.                 |
| **G6** | Site   | `useTerminalStream.ts` **NÃO EXISTE**. Hook de streaming NATS para terminal não implementado.                                                                                                               |

---

## 2. Arquitetura do Terminal com ConPTY + xterm.js + Multi-Tab

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                           VIEWER (Browser)                                     │
│  ┌──────────────────────────────────────────────────────────────────────┐     │
│  │  RemoteTerminal.tsx (xterm.js)                                        │     │
│  │  ┌──────────┐ ┌──────────┐ ┌──────────┐                              │     │
│  │  │ Tab 1    │ │ Tab 2    │ │ Tab 3    │  ← Multi-tab                  │     │
│  │  │PS (admin)│ │ cmd.exe  │ │ WSL bash │                              │     │
│  │  │ term.1.* │ │ term.2.* │ │ term.3.* │  ← Subjects isolados por tab  │     │
│  │  └──────────┘ └──────────┘ └──────────┘                              │     │
│  │  + useTerminalStream (NATS WS)                                        │     │
│  │  + TerminalTab type ({id, shell, subject, xterm instance})            │     │
│  └──────────────────────────────────────────────────────────────────────┘     │
│                                    │ NATS WebSocket                             │
│                    term.in.{tabId} / term.out.{tabId}                           │
└────────────────────────────────────┼──────────────────────────────────────────┘
                                     │
┌────────────────────────────────────┼──────────────────────────────────────────┐
│                       AGENT (Go - Windows)                                      │
│  ┌─────────────────────────────────┘                                          │
│  │  ┌─────────────────────┐   ┌──────────────────────┐                        │
│  │  │ SessionTerminal     │   │ ConPTY (kernel32.dll)│                        │
│  │  │  - tabManager       │◄─►│ CreatePseudoConsole  │                        │
│  │  │  - tabs map[tabId]  │   │ ResizePseudoConsole  │                        │
│  │  │  - stdin loops      │   │ ClosePseudoConsole   │                        │
│  │  │  - stdout loops     │   └──────────┬───────────┘                        │
│  │  │  - resize handlers  │              │                                    │
│  │  │  - recording tap ───┼──────────────┘                                    │
│  │  └──────┬──────────────┘                                                   │
│  │         │                                                                  │
│  │         │  term.in.{tabId}            stdin pipe                            │
│  │         │  (Viewer→Agent)             stdout pipe                           │
│  │         │                                                                  │
│  │         │  term.out.{tabId}           ▼                                    │
│  │         │  (Agent→Viewer)    ┌────────────────────┐                        │
│  │         └───────────────────►│ powershell.exe     │ ← padrão               │
│  │                              │ cmd.exe            │                        │
│  │                              │ wsl.exe (detectado)│ ← se instalado         │
│  │                              └────────────────────┘                        │
│  │                                                                           │
│  │  ┌─────────────────────┐                                                  │
│  │  │ RecordingTap        │──► remote.session.{id}.recording.term            │
│  │  │ (output multiplex)  │    (frames de terminal para gravação)             │
│  │  └─────────────────────┘                                                  │
│  └──────────────────────────────────────────────────────────────────────────┘
```

### 2.1 Por que ConPTY é essencial

| Problema com pipes atuais                 | Solução com ConPTY                                 |
| ----------------------------------------- | -------------------------------------------------- |
| Aplicativos TUI (nano, vim, htop) quebram | Funcionam perfeitamente (PTY real)                 |
| Cores ANSI/sequências VT inconsistentes   | Tradução nativa de sequências VT                   |
| `Resize()` é no-op                        | `ResizePseudoConsole()` redimensiona em tempo real |
| Sem suporte a WSL interativo              | WSL detecta PTY e funciona 100%                    |
| Ctrl+C, Ctrl+Z não funcionam corretamente | Controle de sinais nativo                          |

---

## 3. Plano de Implementação Detalhado

### Fase 1 — Agent: ConPTY Real (substituir pipes)

#### 3.1.1 Novo arquivo: `internal/terminal/pty_conpty.go` (Windows, build tag windows)

**Responsabilidade:** Implementar `CreatePseudoConsole` + `ClosePseudoConsole` + `ResizePseudoConsole` via syscall na `kernel32.dll`.

**API a implementar:**

```go
//go:build windows

package terminal

import (
    "syscall"
    "unsafe"
)

// Estruturas Win32
type HPCON uintptr  // Handle de pseudo console
type COORD struct { X, Y int16 }

// Constantes
const (
    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016
    EXTENDED_STARTUPINFO_PRESENT        = 0x00080000  // startupInfoEx
    PSEUDOCONSOLE_PIPE_READ            = 0x1
    PSEUDOCONSOLE_PIPE_WRITE           = 0x2
)

// CreatePseudoConsole cria um pseudo console com as dimensões especificadas.
// size: COORD{X: cols, Y: rows}
// inputHandle: handle de leitura do pipe de entrada (stdin do processo)
// outputHandle: handle de escrita do pipe de saída (stdout do processo)
// Retorna HPCON (handle do pseudo console).
func CreatePseudoConsole(size COORD, inputHandle, outputHandle syscall.Handle) (HPCON, error)

// ResizePseudoConsole redimensiona o pseudo console.
func ResizePseudoConsole(hpc HPCON, size COORD) error

// ClosePseudoConsole fecha o pseudo console.
func ClosePseudoConsole(hpc HPCON) error
```

**Nota técnica:** `CreatePseudoConsole` está disponível a partir do Windows 10 1809 (build 17763). O agent deve verificar disponibilidade e fazer fallback para pipes se não disponível (Windows 7/8, Server 2016).

#### 3.1.2 Novo arquivo: `internal/terminal/pty_conpty_shell.go` (Windows)

**Estrutura `ConPTYShell`:**

```go
type ConPTYShell struct {
    hpc        HPCON
    cmd        *exec.Cmd
    process    *os.Process
    stdinPipe  *os.File    // pipe conectado ao ConPTY input
    stdoutPipe *os.File    // pipe conectado ao ConPTY output
    shell      ShellKind   // powershell (default), cmd, wsl
    cols, rows int

    mu         sync.Mutex
    closed     bool
    onOutput   func(string)
}
```

**Shells suportados e comandos de inicialização:**

| ShellKind    | Comando                          | Detecção                                  |
| ------------ | -------------------------------- | ----------------------------------------- |
| `powershell` | `powershell.exe -NoLogo -NoExit` | **Padrão** — sempre disponível no Windows |
| `cmd`        | `cmd.exe`                        | Sempre disponível                         |
| `wsl`        | `wsl.exe`                        | Detectado via `wsl.exe --status`          |
| `wsl-distro` | `wsl.exe -d <distro>`            | Lista distros com `wsl.exe -l -q`         |

**Detecção de WSL:**

```go
func IsWSLAvailable() (bool, []string) {
    // 1. wsl.exe --status → verifica se WSL está instalado
    // 2. wsl.exe -l -q → lista distribuições disponíveis
    // Retorna (true, ["Ubuntu", "Debian"]) se disponível
}
```

**Fluxo de criação `NewConPTYShell(shell, cols, rows, onOutput)`:**

1. Criar pipes anônimos: `stdinRead`/`stdinWrite`, `stdoutRead`/`stdoutWrite`
2. Chamar `CreatePseudoConsole(COORD{X: cols, Y: rows}, stdinRead, stdoutWrite)` → obtém `HPCON`
3. Preparar `STARTUPINFOEX` com `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` apontando para o `HPCON`
4. Resolver caminho do executável conforme `ShellKind`:
   - `powershell` → `os.LookPath("powershell.exe")` + args `-NoLogo`, `-NoExit`
   - `cmd` → `os.LookPath("cmd.exe")`
   - `wsl` → `os.LookPath("wsl.exe")`
5. Chamar `CreateProcess` com `EXTENDED_STARTUPINFO_PRESENT`
6. Iniciar goroutines de leitura em `stdoutRead`
7. Retornar `ConPTYShell` com `stdinWrite` para escrita

> **Nota:** O arquivo `pty_windows.go` atual (baseado em pipes) será mantido como fallback com build tag `!conpty` ou detectado em runtime via `IsConPTYAvailable()`.

#### 3.1.3 Detecção de disponibilidade ConPTY

```go
func IsConPTYAvailable() bool {
    // Tenta carregar CreatePseudoConsole da kernel32.dll
    // Se falhar (Windows < 10 1809), retorna false
    // O Manager usará o fallback Shell (pipes) quando indisponível
}
```

#### 3.1.4 Linux/macOS: `internal/terminal/pty_other.go`

> ⚠️ **ADIADO** — Foco Windows neste milestone. O fallback atual com pipes (`pty_other.go`) permanece funcional para Linux. Migração para `creack/pty` será tratada em milestone futuro de suporte Linux.

---

### Fase 2 — Agent: SessionTerminal com Multi-Tab (wiring NATS)

#### 3.2.1 Novo arquivo: `internal/remotesession/session_terminal.go`

**Responsabilidade:** Ciclo de vida da sessão de terminal com suporte a múltiplas abas simultâneas (powershell + cmd + wsl).

**Estrutura:**

```go
type TerminalTab struct {
    ID        string              // UUID v4 para isolamento de subjects
    shell     *terminal.ConPTYShell
    shellKind terminal.ShellKind
    cols, rows int
    stopCh    chan struct{}
}

type SessionTerminal struct {
    sessionID  string
    natsStream *NatsStreamHandler
    tabs       map[string]*TerminalTab  // tabId → TerminalTab

    recordingTap *RecordingTap  // tap para gravação de output

    stopCh     chan struct{}
    doneCh     chan struct{}
    logger     *slog.Logger
    mu         sync.RWMutex
}
```

**Métodos principais:**

| Método                                  | Descrição                                                                                                                   |
| --------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| `CreateTab(ctx, shellKind, cols, rows)` | Cria nova aba: gera tabId UUIDv4, inicia ConPTYShell, subscreve `term.in.{tabId}`, inicia I/O loops                         |
| `CloseTab(tabId)`                       | Fecha shell de uma aba específica, limpa recursos                                                                           |
| `runInputLoop(ctx, tab)`                | Lê de `SubscribeToTermIn` → decodifica JSON `{data, cols?, rows?}` → `shell.WriteStdin(data)` ou `shell.Resize(cols, rows)` |
| `runOutputHandler(tab, output string)`  | Callback `onOutput` → publica `PublishTermOut(sessionID, tab.ID, data)` + envia para `recordingTap`                         |
| `Stop()`                                | Fecha todos os tabs, unsubscribe, limpa recursos                                                                            |

**Subjects NATS isolados por tab:**

```
# Tab individual (multi-tab)
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.{tabId}.in   # Viewer→Agent
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.{tabId}.out  # Agent→Viewer

# Controle de tabs
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.create        # Viewer→Agent: criar tab
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.close          # Viewer→Agent: fechar tab
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.list           # Agent→Viewer: lista tabs ativas
```

#### 3.2.2 Alterar: `internal/remotesession/manager.go` → `runTerminalSession`

Substituir o stub pelo código real com multi-tab:

```go
func (m *Manager) runTerminalSession(ctx context.Context, session *Session) {
    defer safego.Recover()
    defer close(session.doneCh)

    // Shell padrão: powershell
    defaultShell := terminal.ShellPowerShell
    if sk, ok := session.meta["shell"].(string); ok && sk != "" {
        defaultShell = terminal.ShellKind(sk)
    }

    cols, rows := 120, 40
    if c, ok := session.meta["cols"].(int); ok { cols = c }
    if r, ok := session.meta["rows"].(int); ok { rows = r }

    sessTerm, err := NewSessionTerminal(
        session.id, m.natsStream, session.recordingEnabled,
    )
    if err != nil {
        m.publishEvent(session.id, "error", err.Error())
        return
    }

    // Criar tab inicial com o shell padrão
    if _, err := sessTerm.CreateTab(ctx, defaultShell, cols, rows); err != nil {
        m.publishEvent(session.id, "error", err.Error())
        return
    }

    // Subscrever comandos de controle de tabs
    m.natsStream.SubscribeToTermCreate(session.id, func(payload) {
        var req struct { Shell string `json:"shell"`; Cols int `json:"cols"`; Rows int `json:"rows"` }
        json.Unmarshal(payload, &req)
        shellKind := terminal.ShellKind(req.Shell)
        if shellKind == "" { shellKind = terminal.ShellPowerShell }
        sessTerm.CreateTab(ctx, shellKind, req.Cols, req.Rows)
    })

    m.natsStream.SubscribeToTermClose(session.id, func(payload) {
        var req struct { TabID string `json:"tabId"` }
        json.Unmarshal(payload, &req)
        sessTerm.CloseTab(req.TabID)
    })

    // Notificar viewer com lista de shells disponíveis
    availableShells := []string{"powershell", "cmd"}
    if terminal.IsWSLAvailable() {
        if available, distros := terminal.ListWSLDistros(); available {
            for _, d := range distros {
                availableShells = append(availableShells, "wsl:"+d)
            }
        }
    }
    m.publishEvent(session.id, "terminal.ready", map[string]interface{}{
        "shells": availableShells,
        "defaultTab": sessTerm.tabs[0].ID,
    })

    select {
    case <-ctx.Done():
    case <-session.stopCh:
    }

    sessTerm.Stop()
}
```

---

### Fase 3 — Backend: Suporte a shell kind, multi-tab e WSL

#### 3.3.1 Alterar: `RemoteSessionCommands.cs`

Adicionar campos opcionais ao `StartRemoteSessionCommand`:

```csharp
public sealed record StartRemoteSessionCommand(
    // ... campos existentes ...
    string? Shell = "powershell",  // "powershell" (default), "cmd", "wsl"
    int? TermCols = 120,           // colunas iniciais do terminal
    int? TermRows = 40             // linhas iniciais do terminal
) : ICommand<Result<RemoteSessionResponseDto>>;
```

#### 3.3.2 Alterar: `StartRemoteSessionCommandHandler`

Adicionar `shell`, `termCols`, `termRows` ao payload JSON:

```csharp
var payload = JsonSerializer.Serialize(new
{
    action = "start",
    sessionId = session.Id,
    kind = cmd.Kind.ToString().ToLowerInvariant(),
    shell = cmd.Shell ?? "powershell",
    termCols = cmd.TermCols ?? 120,
    termRows = cmd.TermRows ?? 40,
    // ... campos existentes ...
});
```

#### 3.3.3 Novos endpoints no `RemoteSessionsController`

```
# Cria nova aba de terminal na sessão
POST /api/v1/agents/{agentId}/remote-sessions/{sessionId}/terminal/tabs
Body: { "shell": "powershell", "cols": 120, "rows": 40 }
Response: { "tabId": "uuid", "natsSubject": "tenant...term.{tabId}" }

# Fecha uma aba de terminal
DELETE /api/v1/agents/{agentId}/remote-sessions/{sessionId}/terminal/tabs/{tabId}

# Lista WSL disponível no agent (chamado na abertura da sessão)
GET /api/v1/agents/{agentId}/remote-sessions/{sessionId}/terminal/shells
Response: { "shells": ["powershell", "cmd", "wsl:Ubuntu", "wsl:Debian"] }
```

#### 3.3.4 ACL NATS para multi-tab

Atualizar `NatsCredentialsService` e `NATS_SUBJECTS_ACL.md` para incluir:

```
# Terminal multi-tab (pub/sub para viewer)
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.term.>

# Controle de tabs (pub para viewer)
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.term.create
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.term.close
```

---

### Fase 4 — Frontend: xterm.js + Multi-Tab com NATS

#### 4.4.1 Dependências a adicionar (`package.json`)

```json
{
  "@xterm/xterm": "^5.5.0",
  "@xterm/addon-fit": "^0.10.0",
  "@xterm/addon-web-links": "^0.11.0",
  "@xterm/addon-search": "^0.15.0",
  "@xterm/addon-clipboard": "^0.1.0"
}
```

> Nota: `@xterm/xterm` já está listado no plano original de acesso remoto (Fase 3).

#### 4.4.2 Novo arquivo: `src/modules/remote-terminal/useTerminalStream.ts`

**Responsabilidade:** Hook React que gerencia conexão NATS para uma aba de terminal.

```typescript
interface UseTerminalStreamOptions {
  natsSubject: string; // subject base da sessão
  tabId: string; // ID da tab para isolar subjects
  natsUrl: string;
  jwt: string;
  nkeySeed: string;
}

interface UseTerminalStreamReturn {
  isConnected: boolean;
  sendData: (data: string) => void; // envia input para o shell
  sendResize: (cols: number, rows: number) => void;
  onOutput: (callback: (data: string) => void) => void;
  error: string | null;
}
```

**Implementação:**

- Conecta ao NATS via WebSocket (`nats.ws://host:port?access_token=jwt`)
- Subscreve `{natsSubject}.term.{tabId}.out` → onOutput callback com dados decodificados
- Publica input em `{natsSubject}.term.{tabId}.in` como JSON `{data: base64(input)}`
- Publica resize em `{natsSubject}.term.{tabId}.in` como JSON `{cols, rows}`
- Reconexão automática com exponencial backoff

#### 4.4.3 Refatorar: `src/modules/remote-terminal/RemoteTerminal.tsx` → xterm.js

**Novo componente baseado em xterm.js:**

```tsx
interface TerminalTab {
  id: string;
  shell: string; // "powershell", "cmd", "wsl:Ubuntu"
  label: string;
  natsSubject: string;
}

interface RemoteTerminalProps {
  sessionId: string;
  agentId: string;
  natsSubject: string; // subject base da sessão
  natsUrl: string;
  jwt: string;
  nkeySeed: string;
  availableShells: string[]; // recebido do agent no evento terminal.ready
}
```

**Funcionalidades do xterm.js:**

| Funcionalidade      | Implementação                                                                                                                 |
| ------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| **Terminal canvas** | `new Terminal({ cursorBlink: true, fontSize: 14, fontFamily: 'Cascadia Code, monospace', theme: { background: '#0f172a' } })` |
| **Addon Fit**       | `fitAddon.fit()` no mount + `ResizeObserver` → `sendResize(term.cols, term.rows)`                                             |
| **Addon WebLinks**  | Links clicáveis no output (URLs, paths)                                                                                       |
| **Addon Search**    | Ctrl+Shift+F para buscar no buffer                                                                                            |
| **Addon Clipboard** | Seleção de texto + Ctrl+C copia; Ctrl+Shift+V cola                                                                            |
| **Input capture**   | `term.onData(data => sendData(data))` — envia cada tecla em tempo real                                                        |
| **Resize sync**     | `term.onResize(({cols, rows}) => sendResize(cols, rows))` via debounce 200ms                                                  |

**Multi-Tab UI:**

```
┌──────────────────────────────────────────────────────┐
│ [PowerShell] [cmd.exe] [WSL Ubuntu]  [+ Nova Aba]   │ ← Tab bar
├──────────────────────────────────────────────────────┤
│                                                      │
│  PS C:\Users\admin> _                               │ ← xterm.js instance (tab ativa)
│                                                      │
└──────────────────────────────────────────────────────┘
```

- Botão "+ Nova Aba" abre dropdown com shells disponíveis (powershell, cmd, wsl:\*)
- Cada tab tem sua própria instância `Terminal` do xterm.js + `useTerminalStream`
- Tab ativa renderiza o xterm.js; tabs inativas mantêm stream mas não renderizam
- Ctrl+Tab / Ctrl+Shift+Tab para navegar entre tabs
- Botão X para fechar tab → `DELETE /terminal/tabs/{tabId}`

---

### Fase 5 — Gravação de Terminal + Segurança e Auditoria

#### 5.5.1 Gravação de terminal (RecordingTap)

**Arquitetura de gravação de terminal:**

```
┌──────────────────┐     term.out.{tabId}      ┌──────────────────────┐
│  SessionTerminal │──────────────────────────►│  NATS                │
│  recordingTap    │                           │  (viewer stream)     │
│                  │──────────────────────────►│                      │
│                  │  recording.term           │  RecordingAssembler  │
│                  │  (output multiplex)       │  Service (backend)   │
└──────────────────┘                           └──────────────────────┘
```

**Implementação no agent:**

```go
type RecordingTap struct {
    sessionID string
    natsStream *NatsStreamHandler
    enabled    bool
    mu         sync.Mutex
}

func (rt *RecordingTap) WriteTermOutput(tabID string, data string, seq int) {
    if !rt.enabled { return }
    payload := json.Marshal(TermRecordingFrame{
        TabID:     tabID,
        Data:      base64(data),
        Seq:       seq,
        Timestamp: time.Now().UnixMilli(),
    })
    rt.natsStream.PublishRecordingTerm(rt.sessionID, payload)
}
```

**Subject NATS para gravação de terminal:**

```
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.recording.term
```

**Formato do frame de gravação:**

```json
{
  "tabId": "uuid",
  "data": "base64-encoded-output",
  "seq": 1234,
  "timestampMs": 1690123456789
}
```

> **Nota:** A gravação de terminal é multiplexada no mesmo arquivo de gravação da sessão, com um stream separado para terminal. O `RecordingAssemblerService` combina screen + terminal no container final.

#### 5.5.2 Auditoria de comandos

No `RemoteSessionAudit` e no `session_terminal.go`:

| O que auditar       | Como                                                                            |
| ------------------- | ------------------------------------------------------------------------------- |
| Início de tab       | `{action: "terminal.tab.created", tabId, shellKind, sessionId}`                 |
| Fechamento de tab   | `{action: "terminal.tab.closed", tabId, durationSec}`                           |
| Comandos executados | Apenas metadados (commandLength, timestamp) — **NUNCA o comando completo**      |
| Output do terminal  | **NUNCA armazenar em auditoria** (pode conter dados sensíveis)                  |
| Output na gravação  | Armazenado apenas se `RecordingEnabled = true` e consentimento do usuário final |

#### 5.5.3 Rate limiting de input

No agent `session_terminal.go`:

- Máximo de 100 mensagens `term.in` por segundo por tab
- Máximo de 10 resize events por segundo por tab

#### 5.5.4 Sanitização de input

- Payload `term.in` deve ser JSON válido
- `data` deve ser base64 válido
- Tamanho máximo do payload: 64KB
- Caracteres de controle permitidos via ConPTY nativo (não precisamos filtrar — o PTY real trata isso)

---

## 4. Cronograma Estimado

| Fase       | Descrição                                                     | Arquivos                                                                                                                                               | Esforço        |
| ---------- | ------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------- |
| **Fase 1** | ConPTY real no agent (powershell/cmd/wsl)                     | `pty_conpty.go` (novo), `pty_conpty_shell.go` (novo), `shell.go` (atualizar com ShellKind + WSL detect)                                                | 3-4 dias       |
| **Fase 2** | SessionTerminal com multi-tab + NATS                          | `session_terminal.go` (novo), `manager.go` (runTerminalSession real), `nats_stream.go` (term.create/close subjects)                                    | 2-3 dias       |
| **Fase 3** | Backend: shell kind + multi-tab endpoints + ACL               | `RemoteSessionCommands.cs`, `CommandHandlers.cs`, `RemoteSessionsController.cs` (novos endpoints), `NatsCredentialsService.cs`, `NATS_SUBJECTS_ACL.md` | 1-2 dias       |
| **Fase 4** | Frontend: xterm.js + multi-tab + useTerminalStream            | `useTerminalStream.ts` (novo), `RemoteTerminal.tsx` (refatorar p/ xterm.js + tabs), `package.json` (addons)                                            | 2-3 dias       |
| **Fase 5** | Gravação + segurança + auditoria                              | `session_terminal.go` (RecordingTap), `RemoteSessionAudit.cs`, `RecordingAssemblerService.cs` (term stream)                                            | 1-2 dias       |
| **Testes** | Testes end-to-end (powershell, cmd, wsl, multi-tab, gravação) | —                                                                                                                                                      | 1-2 dias       |
| **Total**  |                                                               |                                                                                                                                                        | **10-16 dias** |

---

## 5. Riscos e Mitigações

| Risco                                            | Probabilidade | Impacto | Mitigação                                                                            |
| ------------------------------------------------ | ------------- | ------- | ------------------------------------------------------------------------------------ |
| ConPTY não disponível (Windows < 10 1809)        | Baixa         | Médio   | Fallback automático para pipes com detecção `IsConPTYAvailable()`                    |
| ConPTY + WSL não funcionar corretamente          | Média         | Médio   | Testar especificamente WSL1 e WSL2; fallback para pipes se necessário                |
| Performance com múltiplos tabs (N ConPTY shells) | Média         | Médio   | Limitar a 5 tabs simultâneos por sessão; throttling de output (máx 60 fps por tab)   |
| xterm.js + WebSocket NATS — latência percebida   | Baixa         | Baixo   | Buffer local no xterm.js; flush a cada 16ms (60fps)                                  |
| NATS desconexão durante sessão                   | Média         | Médio   | Reconexão automática no `useTerminalStream`; sessão mantida por 30s antes de expirar |
| Gravação de terminal — volume de dados           | Média         | Baixo   | Compressão de output (apenas diffs entre frames); buffer circular de 5min            |
| Segurança: injeção de comandos                   | Baixa         | Alto    | Sanitização no agent; rate limiting; ConPTY nativo já isola sinais                   |
| WSL não instalado no endpoint                    | Média         | Baixo   | Detecção no agent; botão WSL só aparece se `IsWSLAvailable() == true`                |

---

## 6. Critérios de Aceite

- [ ] Técnico abre sessão com kind=terminal → shell **powershell.exe** inicia automaticamente
- [ ] Técnico digita `Get-Process` → vê tabela formatada com cores no xterm.js
- [ ] Técnico redimensiona a janela do terminal → shell ajusta colunas/linhas em tempo real
- [ ] Técnico clica "+ Nova Aba" → seleciona `cmd.exe` → nova tab abre com cmd funcional
- [ ] Técnico alterna entre tabs com Ctrl+Tab / clique — cada tab é independente
- [ ] Técnico seleciona `WSL: Ubuntu` (se disponível) → terminal Linux funcional dentro do xterm.js
- [ ] Técnico dentro do WSL digita `htop` → interface TUI completa renderiza corretamente
- [ ] Técnico digita `ssh user@host` → conexão SSH interativa funciona
- [ ] Técnico pressiona Ctrl+C → interrompe comando em execução
- [ ] Técnico usa Ctrl+Shift+F → busca no buffer do terminal
- [ ] Log de auditoria registra início/fim de cada tab (sem output/comandos)
- [ ] Gravação de sessão inclui output de terminal multiplexado (quando RecordingEnabled=true)
- [ ] Sessão expira após TTL → todos os tabs são encerrados no agent
- [ ] Fallback para pipes funciona em Windows Server 2016 (sem ConPTY)
- [ ] WSL não disponível → botão WSL não aparece no seletor de shell

---

## 7. Decisões da Revisão (✅ Todas respondidas)

| #   | Pergunta               | Decisão                                                                              |
| --- | ---------------------- | ------------------------------------------------------------------------------------ |
| 1   | Shell padrão?          | **PowerShell** — mais útil para técnicos                                             |
| 2   | WSL desde o início?    | **Sim** — se detectado no computador, aparece como opção                             |
| 3   | xterm.js ou div+input? | **xterm.js** — terminal completo com ANSI/VT, TUI, search, addons                    |
| 4   | Gravação de terminal?  | **Sim** — tap integrado ao `RecordingSource`, stream multiplexado no container final |
| 5   | Multi-tab?             | **Sim** — múltiplos terminais simultâneos (powershell + cmd + wsl) na mesma sessão   |
| 6   | Linux agent?           | **Foco Windows** neste milestone; Linux será tratado depois                          |

---

## 8. Impacto no Contrato NATS

### 8.1 Novos subjects (extensão do contrato v4.5.0 → v4.6.0)

```
# Terminal multi-tab (substitui term.in/term.out simples)
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.term.{tabId}.in    # Viewer→Agent: input + resize
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.term.{tabId}.out   # Agent→Viewer: stdout/stderr

# Controle de tabs
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.term.create         # Viewer→Agent: criar nova tab
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.term.close           # Viewer→Agent: fechar tab
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.term.ready           # Agent→Viewer: shells disponíveis + tab inicial

# Gravação de terminal
tenant.{c}.site.{s}.agent.{a}.remote.session.{id}.recording.term      # Agent→Server: frames de terminal
```

### 8.2 Subjects obsoletos (remover)

```
# Estes subjects simples serão substituídos pelos multi-tab acima:
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.out     # → substituído por term.{tabId}.out
tenant.{c}.site.{s}.agent.{a}.remote.session.{sessionId}.term.in      # → substituído por term.{tabId}.in
```
