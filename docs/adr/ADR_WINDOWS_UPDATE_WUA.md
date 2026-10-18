# ADR — Camada Windows Update (WUA) no agente Go

**Status:** aceito (implementação da Fase 1/2)
**Data:** 2026-12-20
**Contexto:** gerenciador de patches do Windows (inventário + aprovação + instalação seletiva)

## Contexto

O agente Discovery RMM (Go/Wails, serviço LocalSystem) precisa:

1. listar atualizações pendentes e instaladas do Windows (KB, severidade, classificação, reboot);
2. instalar seletivamente apenas as atualizações aprovadas;
3. ocultar/reexibir KBs e ler/aplicar política de Windows Update (enforcement opt-in);
4. reportar reinício pendente (badge em /agents).

A API roda em Linux: toda interação com WUA fica no agente.

## Decisão

- **Implementação própria sobre `github.com/go-ole/go-ole v1.3.0`** (já era dependência
  direta, usada pelo WMI). Nenhuma dependência Go nova foi adicionada.
- **Pendentes via WUA** (`Microsoft.Update.Session` → `CreateUpdateSearcher` → `Search`).
- **Instaladas via `Win32_QuickFixEngineering`** (WMI/COM), não via
  `IUpdateSearcher.Search("IsInstalled=1")` — o segundo devolve o catálogo inteiro
  e é caro no boot.
- **Instalação**: `Microsoft.Update.UpdateColl` + `CreateUpdateDownloader` +
  `CreateUpdateInstaller`; alvos revalidados por `UpdateID`+revisão (fallback KB).
- **Reboot pendente**: sonda de registro (Windows Update, CBS, PendingFileRenameOperations,
  renomeação de computador, pacotes CBS) — barata e sem PowerShell.
- **Política**: somente o conjunto documentado de valores em
  `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate[\AU]`, com backup/rollback
  e recusa quando há WSUS/GPO.
- **Fallback PowerShell** (se necessário em campo): script próprio com
  `-NoProfile -NonInteractive -EncodedCommand`, JSON, timeout e kill tree; nunca
  `-Command` com KB interpolado.

## Bibliotecas avaliadas

| Lib | Decisão |
| --- | --- |
| go-ole v1.3.0 | Usada (já era dependência) |
| github.com/ceshihao/windowsupdate v0.1.2 (Apache-2.0) | Referência; não adicionada (baixa maturidade) |
| github.com/google/cabbie v1.0.6 (Apache-2.0) | Referência de edge cases; não adicionada (32 imports + servicemgr altera AU) |
| github.com/hnakamur/windowsupdate (2018) | Descartada |
| PSWindowsUpdate (PSGallery) | Proibida (supply chain/versão variável) |
| WUApiLib / Microsoft.UpdateServices.Administration (.NET) | Fora: Windows-only |

## Consequências

**Positivas**
- Sem dependência nova; COM/WUA consistente com o helper WMI já existente.
- Inventário de instaladas rápido (QFE), scan de pendentes focado.
- Instalação seletiva real (só aprovados), com resultado por update.
- Enforcement no SO isolado e opt-in, com backup/rollback.

**Negativas / riscos**
- COM late binding é verboso e não é cancelável: timeouts são aplicados por corrida
  (goroutine órfã) e as operações são serializadas (1 por vez).
- `MsrcSeverity` pode vir nulo via WSUS; a UI degrada e a conformidade usa
  classificação como fallback.
- Ainda é necessário validar em VM Windows real (scan/install/hide/política) —
  o build/teste de CI não exercita WUA.

## Alternativas rejeitadas

- **PowerShell como caminho principal**: subprocesso, latência e risco de
  interpolação; mantido apenas como fallback futuro.
- **Servidor instalar via WUApiLib**: impossível (API em Linux).
- **USOClient.exe**: dispara o fluxo padrão do WU, não é seletivo.
