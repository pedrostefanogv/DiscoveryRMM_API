# Automação de Pacotes — Análise e Plano de Correção (2026-09-02)

## Contexto

- Tasks "Instalar Chrome" (01a061d5) e "7-ZIP" (01a06391-dd3c) criadas 20:45, escopo Site_Teste (site 01a06390), `trigger_immediate=true`, `requires_approval=false`, ativas.
- Agent DESKTOP-KRC3N3G (01a06398) iniciou 20:53:46, policy-sync OK (tasks=4 upToDate=true).
- **Nenhuma execução dessas tasks chegou ao servidor** (`automation_execution_reports` só tem 4 registros, nenhum para Chrome/7-ZIP).
- Foxit (check-in) executou, mas exibiu modal "Aprovar/Adiar/Cancelar" e modal de conclusão — apesar de `requires_approval=false`.

## Causas raiz identificadas

### C1 (crítico — notificação pede confirmação mesmo sem aprovação)

`agent: src/app/core/automation/service.go:1049` — `dispatchExecutionNotification` hardcode `Mode: "require_confirmation"` para **toda** ação de pacote (install_start e install_end), ignorando `task.RequiresApproval`.

- Resultado: modal aprovar/adiar/cancelar aparece mesmo com aprovação desativada; timeout 45s registra "deferred" (log 20:57:32) e adia a execução.
- `install_end` também usa require_confirmation → modal de "concluído" que exige interação.

### C2 (crítico — Chrome/7-ZIP nunca executam)

`agent: service.go triggerImmediate` — marcador de dedup `immediate:<fingerprint>:<taskId>` persiste no SQLite para sempre. Tasks criadas/enviadas enquanto o agent estava offline (ou recriadas com mesmo ID) são silenciosamente puladas no primeiro sync após o agent voltar, se o marcador já existir de execução anterior. Evidência: agent subiu 20:53, tasks criadas 20:45, policy-sync 20:53:59 não disparou nada para elas (nenhum log de execução, nenhum report no servidor).

- Agravante: o fingerprint inclui `LastUpdatedAt`, então recriar a task gera novo marcador — mas se o agent já viu a task antes (mesmo ID), o marcador antigo bloqueia.

### C3 (médio — resultado não reportado ao servidor)

- `automation_execution_reports`: 4 registros, todos `status=0` (dispatched), sem ack/result. Execuções locais do agent (Foxit concluída) não atualizam o servidor.
- Agent 1.2.0 (commit 37dc644d) é anterior às correções B1 (CommandId no policy DTO + handlers ack/result reais, M146). Precisa de release nova do agent.

### C4 (médio — PSADT bootstrap falha)

Log 20:54:24: `Install-NuGetClientBinaries ... NonInteractive` — instalação do módulo PSAppDeployToolkit falha porque PowerShellGet 1.0.0.1 pede prompt para instalar o provedor NuGet. Corrigir com `Install-PackageProvider NuGet -Force -Scope AllUsers` (ou `-ForceBootstrap`) antes, ou usar `Register-PackageSource`/download direto do nupkg.

### C5 (baixo — SMART parse)

`[inventory] falha ao parsear saida SMART: cannot unmarshal object into []native.rawDisk` — coletor retorna objeto único onde se espera array. Corrigir parser para aceitar ambos.

## Plano de correção

### Fase 1 — Agent Go (C1, C2, C4, C5)

1. **C1:** em `dispatchExecutionNotification`, usar `Mode: "notify_only"` quando `!task.RequiresApproval` (e `require_confirmation` só quando true). Para `install_end`/`install_failed`/`reboot_required`, usar sempre `notify_only` (resultado não deve pedir confirmação). Atualizar `service_notifications_test.go`.
2. **C2:** no `triggerImmediate`, quando o marcador existe mas é mais antigo que `task.LastUpdatedAt` (novo campo no policy DTO), resetar e disparar. Alternativa mais simples: incluir `LastUpdatedAt` na chave do marcador (`immediate:<fp>:<taskId>:<updatedAt>`) e limpar marcadores órfãos antigos (cleanupOrphanedMarkers já existe).
3. **C4:** no bootstrap PSADT, executar `Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers` antes de `Install-Module`; fallback: baixar nupkg da galeria e extrair manualmente em `C:\Program Files\WindowsPowerShell\Modules`.
4. **C5:** no coletor SMART, aceitar objeto único ou array (`json.RawMessage` → tentar unmarshal em ambos).

### Fase 2 — Servidor (C3, suporte)

5. Publicar nova release do agent (1.3.0) com as correções; sem ela, C1/C2 persistem em produção.
6. Verificar no servidor que `SyncAutomationPolicyCommand` retorna `CommandId` no DTO (B1 já implementado) e que ack/result HTTP persistem (M146 aplicada — confirmado VersionInfo 20260831146).
7. Opcional: endpoint/coluna para expor `requires_approval` efetivo na UI de tarefas já existe ("Aprovação: Não") — nada a fazer no frontend além de, no futuro, mostrar modo de notificação.

### Fase 3 — Validação

8. Build agent: `go build ./...` + `GOOS=windows go build`; testes `go test ./src/app/core/automation/...`.
9. Teste E2E: criar task UpdateOrInstallPackage (7zip) com trigger immediate, sem aprovação → agent deve instalar sem modal e reportar ack/result ao servidor (registro em `automation_execution_reports` com `result_received_at` preenchido).

## Observação sobre o log analisado

- Execuções Foxit do log (b5b3ca10 aprovada, 154e2cf8 timeout→deferred) são exatamente o sintoma do C1: sem `RequiresApproval`, ainda assim o fluxo passou por confirmação e a segunda execução foi adiada por timeout.

---

## Segunda rodada (2026-09-02, noite) — falhas de instalação 7-ZIP/Chrome

Após o deploy do build 3ec07f61 (C1/C2 OK — `install_end mode=notify_only` confirmado), as instalações ainda falhavam. Novas causas raiz (log `agent(1).log`):

### C6 (crítico — MSI recebe switch /S do catálogo)

`automation_p2p.go runLocalInstallerWithSwitches` aplicava o switch `silent="/S"` do catálogo (feito para o .exe NSIS) ao `msiexec /i <msi>` → `exit status 1` imediato (log 22:11:24).
**Corrigido:** MSI sempre usa `/qn /norestart`; switches do catálogo só entram se forem argumentos válidos do msiexec (opções nativas ou `KEY=VALUE`) — novo helper `isMSIExecArgument`.

### C7 (crítico — fallback winget injeta --custom /S em MSI)

`winget/client.go InstallWithSwitches` adicionava `--custom /S` → winget repassa ao msiexec → `0x8a15004a: Arguments for msiexec are invalid` (log 21:40:52).
**Corrigido:** `--custom` só é adicionado quando o switch não é "exe-only" (`/S`, `/VERYSILENT`, etc.) — novo helper `looksLikeExeOnlySwitch`. MSI packages instalam com `--silent` puro (winget usa o manifesto).

### C8 (alto — msiexec sem admin falha com 1603 e não tenta UAC)

Agent roda `elevated=false`; `msiexec /i` Machine-scope sem admin sai com 1603 (ou pendura até o timeout de 20min — log 21:40:43 "timeout na execução do instalador"). O fallback UAC só disparava em erro 740 de fork/exec, que não ocorre com msiexec.
**Corrigido:** `executeHiddenProcess` agora também tenta `launchInstallerViaUAC` quando msiexec sai com 1603 (novo helper `isMSIElevationExitCode`).

### Chrome (0x8a150011 — externo)

`winget download google.chrome` → "Installer hash does not match" (manifesto do Google desatualizado no winget; reproduzido localmente). O fluxo correto é o fallback winget direto — com C7 corrigido, `winget install --silent` sem `--custom` deve instalar normalmente.

### Validação

- `go build ./...` OK (nativo + GOOS=windows), `go vet` OK, testes `automation`/`winget` OK.
- **Pendente:** novo build/deploy do agent (o build 3ec07f61 em campo não tem C6/C7/C8).

---

## Terceira rodada (2026-09-02) � aproveitar InstallerType do manifesto winget oficial

### An�lise
O pipeline importa os manifestos do `microsoft/winget-pkgs` (`WingetManifestParser`), que cont�m dados valiosos que eram **lidos e descartados**:
- `InstallerType` por arquitetura (ex.: 7-Zip x64 = `wix`, Chrome = `wix`, Foxit = `exe`) � o parser lia o campo YAML mas n�o propagava.
- `installerSha256ByArch` � j� propagado ao metadata, mas n�o ao DTO do agent.
- `installLocation` � propagado ao metadata apenas.

Os bugs C6/C7 da segunda rodada existiam justamente por falta desses dados: o agent "adivinhava" a estrat�gia de instala��o pela extens�o do arquivo e aplicava switches de .exe a instaladores MSI.

### Melhorias implementadas
1. **Servidor** (`WingetManifestParser.cs`): metadata agora inclui `installerTypesByArch` (arch ? InstallerType).
2. **Servidor** (`AppStoreDtos.cs` + `AppStoreService.cs`): `AppCatalogPackageDto` e `EffectiveApprovedAppDto` ganham `InstallerTypesByArch`; `MapUnifiedToDto` extrai do metadata (novo `ParseInstallerTypes`, tolerante a pacotes antigos sem o campo).
3. **Agent** (`appstore/types.go`): `Item.InstallerTypesByArch`.
4. **Agent** (`automation_p2p.go`): novo `catalogInstallerInfo` (switches + InstallerType da arquitetura do host, ordem x64?x86?arm64?arm?neutral) e `runLocalInstallerFull`:
   - `wix`/`msi`/`burn` ? sempre msiexec (mesmo se extens�o .exe) com `/qn /norestart` + propriedades KEY=VALUE;
   - `zip`/`portable` ? erro claro ("use winget install") em vez de tentar executar;
   - `.exe` sem tipo ? cascata heur�stica existente (NSIS/Inno/Burn/InstallShield).

### Compatibilidade
- Pacotes sincronizados antes da mudan�a n�o t�m `installerTypesByArch` no metadata ? DTO retorna dicion�rio vazio ? agent usa a extens�o (comportamento anterior, j� corrigido por C6/C7).
- **Para popular o campo � necess�rio re-sincronizar o cat�logo winget** (AppCatalogSyncService) ap�s o deploy do servidor.

### Valida��o
- Servidor: `dotnet build` OK, 276/276 testes OK.
- Agent: `go build` OK (nativo + GOOS=windows), `go vet` OK, testes `./app/...` OK.
