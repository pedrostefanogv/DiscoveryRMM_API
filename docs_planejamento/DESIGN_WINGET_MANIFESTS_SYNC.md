# Design: WingetManifestsSyncService — shallow clone + import local do microsoft/winget-pkgs

**Data:** 2026-09-02
**Ref.:** PLANO_WINGET_P2P_VERSIONADO.md — Fase 4 (itens 10, 10a, 10b, 10c), rev. 3

---

## 1. Visão geral

Novo pipeline no servidor (.NET, Infrastructure) que mantém a tabela `app_packages`
(AppInstallationType.Winget) fresca a partir de um **shallow clone** do branch
`master` do `microsoft/winget-pkgs`, atualizado com `git pull` e importado
localmente a cada X horas (default 1h).

```mermaid
flowchart LR
    GH[(github.com/microsoft/winget-pkgs<br/>branch master)] -->|git clone --depth 1<br/>git pull --depth 1| FS[/var/lib/discovery/winget-pkgs]
    FS -->|"git diff --name-only HEAD@{1} HEAD<br/>(fallback: varredura completa)"| P[WingetManifestParser<br/>YamlDotNet]
    P --> U[AppPackageRepository.BulkUpsertAsync<br/>+ anti-downgrade]
    U --> DB[(app_packages)]
    DB --> H[GetAppStoreEffectiveHandler<br/>/agent-auth/.../appstore]
```

---

## 2. Arquivos novos / alterados

| Arquivo                                      | Projeto                           | Tipo    | Conteúdo                                                                                                |
| -------------------------------------------- | --------------------------------- | ------- | ------------------------------------------------------------------------------------------------------- |
| `WingetManifestsSyncService.cs`              | Discovery.Infrastructure/Services | novo    | Orquestra clone/pull → diff → parse → upsert; expõe `SyncFromManifestsAsync(ct)`                        |
| `WingetManifestParser.cs`                    | Discovery.Infrastructure/Services | novo    | Parse dos YAMLs (`Installer.yaml`, `DefaultLocale.yaml`) → `AppPackage`                                 |
| `WingetVersionComparer.cs`                   | Discovery.Infrastructure/Services | novo    | Comparador tolerante (server-side espelho do `automation_versions.go` do agent)                         |
| `WingetManifestsSyncOptions.cs`              | Discovery.Infrastructure/Services | novo    | Bind de config `AppCatalog:Winget:*`                                                                    |
| `IWingetManifestsSyncService.cs`             | Discovery.Core/Interfaces         | novo    | Interface para o background job                                                                         |
| `AppCatalogSyncService.cs`                   | Infrastructure                    | alterar | Quando `Source = "manifests"`, delega winget para o novo serviço (mantém feed p/ Chocolatey e fallback) |
| `AppPackageRepository.cs`                    | Infrastructure                    | alterar | Guard anti-downgrade no `BulkUpsertAsync` (R1)                                                          |
| `appsettings.json` / `discovery.env.example` | Discovery.Api / scripts           | alterar | Nova seção `AppCatalog:Winget`                                                                          |
| `WingetManifestsSyncTests.cs`                | Discovery.Tests                   | novo    | Testes do parser e do comparador                                                                        |

**Auto-DI:** qualquer classe de Infrastructure que implemente interface única de
`Discovery.Core.Interfaces` é registrada automaticamente como scoped
(`ServiceCollectionExtensions.AddDiscoveryAutoRegisteredServices`) — então basta
criar `IWingetManifestsSyncService` + implementação, sem registro manual.

---

## 3. Configuração (`AppCatalog:Winget`)

```json
"AppCatalog": {
  "Winget": {
    "Source": "manifests",              // "manifests" (default) | "feed" | "both"
    "ManifestsPollIntervalMinutes": 60, // cadência do git pull + import
    "ClonePath": "/var/lib/discovery/winget-pkgs",
    "RepoUrl": "https://github.com/microsoft/winget-pkgs.git",
    "GitTimeoutSeconds": 900,           // clone inicial pode levar minutos
    "Enabled": true
  }
}
```

- `Source = "feed"` mantém o comportamento atual (fallback/rede de segurança).
- `Source = "both"`: pull dos manifests **e** sync do feed (útil durante a
  transição/validação — o anti-downgrade evita que o feed semanal derrube versões).
- **Secret/env override** (`discovery.env`): `AppCatalog__Winget__ClonePath`, etc.

---

## 4. `WingetManifestsSyncService` — fluxo detalhado

```
SyncFromManifestsAsync(ct):
 1. GarantirCloneAsync(ct)
    - se ClonePath não existe: git clone --depth 1 --single-branch --branch master <RepoUrl> <ClonePath>.tmp
      → rename atômico para <ClonePath> (clone interrompido não deixa estado inválido)
    - clone em <ClonePath>.tmp órfão (de tentativa anterior) → apagar antes de novo clone
 2. GitPullAsync(ct)
    - git pull --depth 1 --ff-only
    - em falha de ref-history (shallow): git fetch --depth 1 origin master + git reset --hard origin/master
    - capturar saída p/ decidir se houve mudança (Fast-forward / Already up to date)
 3. ColetarManifestsAsync(pullChanged, ct)
    - se pullChanged e reflog tem estado anterior:
        git diff --name-only HEAD@{1} HEAD -- manifests/  → arquivos alterados
        → derivar conjunto de (PackageId, Version) afetados
    - senão (primeira execução, reflog vazio ou reset): varredura completa de manifests/
      (diretórios: manifests/<1ª letra>/<Publisher>/<Package>/ e versões em subpastas)
 4. Para cada (PackageId, Version) do conjunto:
    - ler Installer.yaml + DefaultLocale.yaml (o que existir; version.yaml etc. tolerados)
    - WingetManifestParser.Parse(packageId, version, dir) → AppPackage? (null = malformado, log + pula)
    - pular versão se WingetVersionComparer.Compare(parsedVersion, existingVersion) < 0 (anti-downgrade)
    - acumular em lista; BulkUpsertAsync em chunks de 200 (batch já existente no repo)
 5. Pós-pull: git gc --prune=now (shallow não cresce indefinidamente)
 6. Retornar AppCatalogSyncResultDto (reuso do DTO existente; PagesProcessed = nº de pacotes importados)
```

### Regras operacionais

- **Idempotência:** upsert por `PackageId` — reinício no meio do import apenas
  reimporta; sem checkpoint obrigatório.
- **Concorrência:** execução serializada via `SemaphoreSlim(1,1)` no singleton do
  serviço (o `AppCatalogBackgroundSyncService` já serializa por `InstallationType`,
  mas o novo serviço pode ser chamado por hosted service e endpoint manual).
- **Falha nunca limpa o catálogo:** upsert-only; exceção em 1 pacote não aborta o
  batch; exceção no pull inteiro retorna `Success=false` mantendo o catálogo atual.
- **Fallback automático (10c):** se N pulls consecutivos falharem (default 3),
  log Warning e o sync do feed `packages.json` continua podendo rodar conforme
  `Source` (com `Source="manifests"` puro, o catálogo simplesmente fica parado —
  nunca regredido).

---

## 5. `WingetManifestParser` — mapeamento YAML → AppPackage

### Installer.yaml (campos relevantes)

```yaml
PackageIdentifier: Foxit.FoxitReader      # cross-check com o path (malformado → pula)
PackageVersion: 2026.1.3.36551
Installers:
  - Architecture: x64
    InstallerUrl: https://...exe
    InstallerSha256: <hex>
    InstallerSwitches:
      Silent: /SILENT
      SilentWithProgress: /SILENT
      InstallLocation: <INSTALLDIR>
    InstallerType: exe
  - Architecture: x86
    ...
```

### DefaultLocale.yaml

```yaml
PackageIdentifier: Foxit.FoxitReader
PackageName: Foxit PDF Editor
Publisher: Foxit Software
License: Proprietary
ShortDescription: ...
Tags: [pdf, editor]
PackageUrl: https://www.foxit.com
```

### Mapeamento → `AppPackage`

| AppPackage                                    | Origem                                                                                                                                                                                                                                                                                |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PackageId`                                   | `PackageIdentifier` (normalizado igual ao `NormalizePackageId` do repo)                                                                                                                                                                                                               |
| `Version`                                     | pasta da versão (path), cross-check com `PackageVersion` do YAML                                                                                                                                                                                                                      |
| `Name`, `Publisher`, `Description`, `SiteUrl` | `DefaultLocale.yaml`                                                                                                                                                                                                                                                                  |
| `InstallCommand`                              | vazio (agent monta o comando)                                                                                                                                                                                                                                                         |
| `MetadataJson`                                | `{ license, category: null, tags, installerUrlsByArch: {x64: url,...}, installerSha256ByArch, silent, silentWithProgress, installLocation }` — **mesma forma** que o `AppCatalogSyncService` grava hoje, para o `AppStoreService.MapUnifiedToDto` continuar funcionando sem alteração |
| `SourceGeneratedAt`                           | data do commit do pull (HEAD timestamp)                                                                                                                                                                                                                                               |
| `LastUpdated`                                 | null (não há campo equivalente confiável por pacote no clone)                                                                                                                                                                                                                         |
| `IconUrl`                                     | null (winget-pkgs não inclui ícones no repo)                                                                                                                                                                                                                                          |

**Observações importantes:**

- **Escolha de versão por pacote:** um pacote tem N versões no clone, mas
  `app_packages` guarda **uma linha por PackageId** (o feed só expõe a "latest").
  O parser escolhe, entre as versões presentes do pacote, a **maior** segundo
  `WingetVersionComparer` (empate → maior `PackageVersion` lexicográfico).
- **Silent switches:** preferência de arch x64 → x86 → arm64 → arm → neutral
  (mesma regra do `WingetFeedClient.ParseSilentSwitches`).
- **YamlDotNet** é nova dependência no Infrastructure (leve, sem native deps).

---

## 6. `WingetVersionComparer` — comparador tolerante (server-side)

Espelho do comparador do agent (item 6 do plano) para que server e agent
decidam igual:

```
Compare(a, b): int
  - split em segmentos numéricos/não-numéricos ("2026.1.3" → [2026,1,3])
  - compara numericamente segmento a segmento; ausente = 0
  - segmentos não numéricos: comparar ordinal (estável, só desempate)
  - pré-releases conhecidos: beta/alpha/rc < estável (mais desempate)
  - string vazia/null → sempre "menor" (não bloqueia import)
```

Casos de teste mínimos: `2026.1.3` vs `2026.1.3.36551`; `132.0.1` vs `132.0`;
`1.29.289.0` vs `1.30`; `2026.2-beta` vs `2026.1.9`; versões com sufixo
`b12345`/`r2` (ordem estável, sem crash).

---

## 7. Guard anti-downgrade no `AppPackageRepository.BulkUpsertAsync` (R1)

Hoje o upsert copia `current.Version = incoming.Version` incondicionalmente.
Alteração mínima, **apenas para o caminho Winget via manifests**:

```csharp
// Winget via manifests: não rebaixar versão (commits fora de ordem são normais no git)
if (installationType == AppInstallationType.Winget && allowDowngrade == false)
{
    if (WingetVersionComparer.Compare(incoming.Version, current.Version) < 0)
        continue; // pula este campo Version, mas atualiza metadata (nome/descrição/switches)
}
```

- Assinatura atual do `BulkUpsertAsync` é usada por outros fluxos (feed,
  Chocolatey, custom) → adicionar parâmetro **opcional**
  `bool preventDowngrade = false` (ou overload) para não alterar chamadores existentes.
- Metadata (nome, switches, installers) é sempre atualizada; só a `Version` é protegida.
- `UpsertCustomAsync` não muda.

---

## 8. Agendamento (10b)

Reuso do padrão Quartz já presente no projeto (comentário em
`BackgroundServicesCollectionExtensions`: jobs de purge/retention/etc. migraram
para Quartz):

```csharp
// QuartzServiceCollectionExtensions
var manifestsEnabled = configuration.GetValue<bool?>("AppCatalog:Winget:Enabled") ?? true
    && configuration.GetValue<string?>("AppCatalog:Winget:Source") != "feed";
var manifestsIntervalMin = Math.Max(15, configuration.GetValue<int?>("AppCatalog:Winget:ManifestsPollIntervalMinutes") ?? 60);

if (manifestsEnabled)
    q.ScheduleJob<WingetManifestsSyncJob>(trigger => trigger
        .WithIdentity($"{WingetManifestsSyncJob.Key.Name}-trigger", ...)
        .WithSimpleSchedule(s => s.WithIntervalInMinutes(manifestsIntervalMin).RepeatForever()));
```

- `WingetManifestsSyncJob` (Api/Services/Quartz) segue o modelo do
  `WingetCatalogSyncJob`: scope por execução, `[DisallowConcurrentExecution]`,
  toggle lido em runtime.
- Execução manual: reuso do endpoint existente de sync de catálogo
  (`AppCatalogBackgroundSyncService.TryStartSync`) — quando `Source = "manifests"`,
  `TryStartSync(Winget)` dispara o novo pipeline em background com resultado
  consultável em `GET /sync/status` (sem endpoint novo).

---

## 9. Plano de implementação (ordem)

| Passo | Entrega                                                                                          | Validação                                                               |
| ----- | ------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------- |
| 1     | `WingetVersionComparer` + testes                                                                 | unit tests (casos da seção 6)                                           |
| 2     | `WingetManifestParser` + testes                                                                  | parser contra YAMLs reais baixados do winget-pkgs (amostra ~50 pacotes) |
| 3     | Guard anti-downgrade no `BulkUpsertAsync`                                                        | testes de repositório (downgrade bloqueado, metadata atualizada)        |
| 4     | `WingetManifestsSyncService` (clone/pull/diff/import) + `IWingetManifestsSyncService`            | execução manual local apontando `ClonePath` p/ pasta temporária         |
| 5     | Config + Quartz job + integração com `TryStartSync`/status                                       | `dotnet build` + teste de scheduler                                     |
| 6     | Ajuste no `AppCatalogSyncService` (rota por `Source`) + docs (`appsettings`, env example, PLANO) | dry-run em dev                                                          |

**Fora de escopo desta fase:** endpoint de observações (item 11), sync.ping
(item 12), mudanças no agent.

---

## 10. Critérios de aceite (fase 4-server)

- Com `Source = "manifests"`, commit novo no `master` do winget-pkgs reflete em
  `app_packages` em ≤ 2h.
- Feed semanal desabilitado (`Source = "manifests"`) **não** regredir versões
  já importadas (anti-downgrade).
- Restart da API no meio de um import não corrompe estado; próximo ciclo reimporta.
- Clone falho (rede bloqueada) mantém o catálogo atual intacto e loga erro.
- `AppStoreService` / `GetAppStoreEffectiveHandler` seguem funcionando sem
  alteração (metadata_json no mesmo formato do feed).
