# Plano: Versionamento de Pacotes Winget no P2P — Eliminação do Loop de Update Defasado

**Data:** 2026-09-02 (rev. 2 — Camada D / Fase 4 revisadas)
**Problema:** Tasks de instalação/update winget usam a versão do catálogo do servidor (`packages.json` do feed, sincronizado a cada N dias — default 5, e **disabled por default**). Se a versão do catálogo estiver defasada em relação ao winget real, o agent entra em loop: tenta atualizar, o winget diz "já atualizado" (versão real ≥ catálogo), a task recurring roda de novo no próximo slot, e o ciclo se repete indefinidamente.

> **Rev. 2 (2026-09-02):** o feed `packages.json` (`winget-package-explo`) é **pré-processado ~1x/semana** (release manual no repo). Sincronizá-lo a cada 6h não resolve: os dados já nascem defasados. O gargalo é a **fonte**, não a cadência de sync. A Fase 4 foi reescrita: a fonte primária passa a ser um **clone raso (shallow) do branch `master` do `microsoft/winget-pkgs`** (apenas os manifests aprovados, sem histórico nem outros branches), atualizado com `git pull` e importado localmente no servidor a cada X horas.

---

## 1. Diagnóstico (evidências no código)

### Fonte da defasagem (servidor)

- `WingetFeedClient.cs:9` — catálogo vem de `https://github.com/pedrostefanogv/winget-package-explo/releases/latest/download/packages.json` (snapshot estático, **não é o winget em tempo real**).
- `WingetCatalogSyncJob.cs` — Quartz job com cron `0 0 0 */N * ?` (N dias, default 5) e **`Enabled = false` por default** (`appsettings`). Ou seja: o catálogo só atualiza se alguém ligar o job manualmente.
- M103 (histórico) substituiu o sync "hub-and-spoke" por "snapshot release do GitHub" — defasagem intrínseca entre releases do feed.

### Comportamento do agent que causa o loop

- `executor.go` — `shouldSkipWingetAction("upgrade")`: consulta `winget upgrade` **real** da máquina. Se o pacote não aparece lá:
  - Instalado + atualizado → skip "ja atualizado" (Success=true) → **correto**.
- `service.go` — tasks `TriggerRecurring` rodam a cada slot do cron com cooldown de apenas 60s; o marcador `recurring:last` só evita reexecução no mesmo slot.
- **O loop:** se a task usa `UpdatePackage` puro e o catálogo do servidor anuncia versão X enquanto a máquina já tem X (ou superior via winget real), tudo bem — o skip "ja atualizado" resolve. O problema real surge quando: (a) o catálogo anuncia uma versão **mais antiga** que a disponível no winget real e a UI/usuário espera "sempre mais recente"; (b) `UpdateOrInstallPackage` com fallback para install: install de pacote já instalado → skip "ja instalado" → benigno; (c) **install falhando repetidamente** (ex.: installer do feed quebrado/defasado que o winget rejeita) → task recurring reexecuta a cada slot para sempre, sem backoff nem desativação.

### O que já existe no P2P (base para a solução)

- `p2p_publish.go` — **`PublishFileWithIDAndVersion(path, artifactID, version)` já existe** e propaga a versão no gossip (`p2p_gossip.go:166`), mas **ninguém passa a versão** hoje (`automation_p2p.go` chama `PublishFileWithID` com version="" e o parser de instalador não extrai versão).
- `P2PArtifactView.Version` — campo já existe no gossip e no índice de peers.
- ArtifactID determinístico: `winget:<normalizedPackageId>` (ex. `winget:foxitfoxitreader`).

---

## 2. Solução proposta — "catálogo P2P versionado + anti-loop"

### Ideia central

A **fonte de verdade de "existe update?" passa a ser a rede P2P + o winget local**, com o catálogo do servidor virando apenas _hint_ de bootstrapping. Cada agente que instala/atualiza um pacote publica no P2P o instalador **com a versão real obtida do winget** (`winget upgrade --id X` retorna "Available" — versão real mais recente que a instalada). Os demais agentes consultam o índice P2P: se o artifact `winget:foxitfoxitreader` tem version >= instalada localmente, executa; senão, pula **registrando a razão** — sem retry cego.

### Camadas da solução

**Camada A — Versão real no artifact P2P (agent)**

1. Ao baixar/cachear instalador (`downloadAndCacheForP2P`), extrair a **versão real** disponível: parse da coluna "Available" do `winget upgrade --id X` (ou `winget show --id X`), fallback para versão embutida no nome do instalador (ex. `Firefox-132.0.exe`).
2. Publicar com `PublishFileWithIDAndVersion(installerPath, "winget:foxitfoxitreader", realVersion)` em vez de `PublishFileWithID`.
3. Gossip propaga `Version` (já suportado) → todos os peers passam a saber "o pacote X está disponível na versão Y na rede".

**Camada B — Decisão de execução versionada (agent)**

1. Nova função `shouldExecuteWingetAction(ctx, packages, p2p, operation, packageID) (skip bool, reason string)`:
   - Versão instalada (via `winget list`) >= versão no artifact P2P (se existir) **e** >= versão do catálogo do servidor → **skip com reason "atualizado"**.
   - Versão do artifact P2P > instalada → executa via P2P (rápido, LAN).
   - Sem artifact P2P → usa catálogo do servidor como fallback (comportamento atual), **mas com proteção anti-loop (Camada C)**.
2. Parse de versões: comparador SemVer tolerante (lida com `2026.1.3`, `132.0.1`, `1.29.289.0`).

**Camada C — Anti-loop (agent, correção imediata e independente)**

1. **Backoff exponencial por task+pacote quando o resultado é "skip por já atualizado" repetido N vezes consecutivas** — na prática: se as últimas 3 execuções da task terminaram em skip benigno idêntico, aumentar o intervalo efetivo (ex.: pular os próximos 2 slots do cron) e registrar marker `recurring:consecutive-skips:<taskId>`.
2. **Circuit breaker para falha persistente:** se a task falha (não skip) 3x consecutivas com o mesmo erro, suspender execução por 24h e reportar status `Degraded` no policy-sync/resultado (o servidor pode exibir "task em pausa por falhas repetidas").
3. **Dedup de skip por versão:** se o motivo do skip é "ja atualizado" e a versão instalada não mudou desde o último skip, considerar _no-op_ e não contar como execução para o histórico/auditoria (evita poluir "Execuções Recentes" com dezenas de "Concluído — pulando").

**Camada D — Catálogo fresco (servidor) — REVISADA (rev. 2)**

> **Restrição que motivou a revisão:** o `packages.json` do feed (`winget-package-explo`) é um snapshot **pré-processado ~1x por semana** (release manual no repo). Sincronizá-lo a cada 6h não adianta: os dados já nascem defasados. O gargalo não é a cadência de sync do servidor — é a **fonte**. Solução: clonar a fonte primária (`microsoft/winget-pkgs`) em modo **shallow** (só o branch `master`, sem histórico) e **processar localmente no servidor** a cada X horas.

**Alternativas avaliadas:**

| Opção                                                                                       | Veredito                                                                                         |
| ------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| Manter feed `packages.json` com sync mais frequente                                         | ❌ Inútil — o snapshot só muda ~1x/semana                                                        |
| REST source interno do cliente winget                                                       | ❌ Não documentado para terceiros; pode mudar sem aviso                                          |
| Rodar `winget.exe` no servidor para exportar catálogo                                       | ❌ winget é Windows-only; servidor é Linux                                                       |
| GitHub API com polling de commits + download de YAMLs individuais                           | ❌ Descartada — depende de token/rate limit, mais requests e mais complexidade que um clone raso |
| **Shallow clone do branch `master` do `microsoft/winget-pkgs` + `git pull` + import local** | ✅ Fonte primária, sem API/token, sem histórico, dados já aprovados no disco local               |

1. **Shallow clone (fonte primária):** no primeiro boot, o servidor executa `git clone --depth 1 --single-branch --branch master https://github.com/microsoft/winget-pkgs.git` para um diretório de trabalho (ex. `/var/lib/discovery/winget-pkgs`). Isso traz **apenas o estado atual do branch `master`** — os manifests já aprovados pela comunidade — sem histórico de commits, sem outros branches, sem tags. Tamanho típico do working tree: ~1–2 GB em disco (aceitável para um servidor; configurável via `AppCatalog:Winget:ClonePath`).
2. **Atualização periódica:** a cada X horas (default 1h), `git pull --depth 1 --ff-only` (ou `git fetch --depth 1` + `git reset --hard origin/master`) — baixa só o delta do branch, alguns MB por pull. Sem token: o protocolo git do GitHub não tem o rate limit de 60/h da API REST.
3. **Import local → `AppPackage`:** varredura do diretório `manifests/` (ou apenas dos diretórios modificados desde o último import, via `git diff --name-only HEAD@{1} HEAD` quando disponível) parseando `Installer.yaml` (URLs por arquitetura, `InstallerSha256`, `InstallerSwitches.Silent/SilentWithProgress/InstallLocation`), `DefaultLocale.yaml` (`PackageName`, `Publisher`, `License`, `ShortDescription`, `Tags`, `PackageUrl`) e a versão da pasta. Upsert em `app_packages` **com proteção anti-downgrade** (só sobrescreve se a versão nova comparada for >= à existente — mesmo comparador tolerante do agent, item 6) e **upsert-only** (falha nunca limpa o catálogo).
4. **Idempotência e retomada:** o import é idempotente (upsert por `PackageId`+`Version`); se o servidor reiniciar no meio de um import, o próximo ciclo simplesmente reimporta o estado atual do clone — sem checkpoint obrigatório. Para reduzir trabalho, o import pode ser incremental usando o diff do git entre pulls (com fallback para varredura completa se o reflog não tiver o estado anterior).
5. **Config:** `AppCatalog:Winget:Source = "manifests" (default) | "feed" | "both"`; `PollInterval = 1h`; `ClonePath`; `GitTimeout`. O feed `packages.json` permanece disponível como opção de fallback/rede de segurança (se o clone falhar repetidamente, ex. bloqueio de rede, o feed semanal ainda alimenta o catálogo).
6. **Enriquecimento via agents (crowdsourcing) — mantido como complemento:** endpoint `POST /agent-auth/me/winget-observations` com `{packageId, latestAvailableVersion}` coletado do `winget upgrade` durante os skips. Com a fonte primária fresca, o crowdsourcing deixa de ser crítico para "frescor" e passa a valer como **telemetria de frota** e detector de divergência (winget real anuncia versão que o catálogo ainda não tem).
7. **sync.ping de catálogo:** quando o import ou crowdsourcing detecta versão mais nova, o servidor empurra `appstore` **só para os agents que têm o pacote instalado** (cruzamento com o inventário de software), com throttle por `(packageId, versão)`. O agent já reage a `appstore` no coordinator — zero mudança de contrato.

---

## 3. Plano de execução

### Fase 1 — Anti-loop (agent, baixo risco, imediato) ✅ prioridade

| #   | Item                                                                           | Arquivo                                    | Esforço |
| --- | ------------------------------------------------------------------------------ | ------------------------------------------ | ------- |
| 1   | Backoff por skips benignos consecutivos (pular 2 slots após 3 skips idênticos) | `service.go` (cron callback + markers)     | Médio   |
| 2   | Circuit breaker: 3 falhas consecutivas → pausa 24h + status Degraded           | `service.go` + `types.go`                  | Médio   |
| 3   | No-op de skip não conta como execução no histórico                             | `service.go` (executeTaskAsync/onComplete) | Pequeno |

### Fase 2 — Versão real no artifact P2P (agent)

| #   | Item                                                                                                | Arquivo                                         | Esforço |
| --- | --------------------------------------------------------------------------------------------------- | ----------------------------------------------- | ------- |
| 4   | Extrair versão real disponível (parse `winget upgrade` col. Available; fallback do nome do arquivo) | `winget/client.go` ou novo `winget/versions.go` | Médio   |
| 5   | Publicar com `PublishFileWithIDAndVersion`                                                          | `automation_p2p.go` (`downloadAndCacheForP2P`)  | Pequeno |
| 6   | Comparador de versões tolerante (semver-ish)                                                        | novo `automation_versions.go`                   | Pequeno |

### Fase 3 — Decisão versionada (agent)

| #   | Item                                                                                           | Arquivo                                       | Esforço |
| --- | ---------------------------------------------------------------------------------------------- | --------------------------------------------- | ------- |
| 7   | `shouldExecuteWingetAction` combinando: versão instalada vs artifact P2P vs catálogo servidor  | `executor.go`                                 | Médio   |
| 8   | Substituir `shouldSkipWingetAction` nos caminhos install/upgrade preservando mensagens de skip | `executor.go`                                 | Pequeno |
| 9   | Telemetria: motivo do skip vai no result/metadata (visível no servidor)                        | `service_helpers.go` (buildExecutionMetadata) | Pequeno |

### Fase 4 — Catálogo fresco (servidor) — revisada (rev. 2)

| #   | Item                                                                                                            | Arquivo / componente                                      | Esforço |
| --- | --------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------- | ------- |
| 10  | Shallow clone do branch `master` do `microsoft/winget-pkgs` + `git pull` periódico (sem histórico/branches)     | novo `WingetManifestsSyncService` (Infrastructure) + DI   | Médio   |
| 10a | Parser de manifests YAML → `AppPackage` (installers por arch, silent switches, metadata) + anti-downgrade       | novo `WingetManifestParser` (YamlDotNet)                  | Médio   |
| 10b | Background de polling periódico (default 1h) + config `AppCatalog:Winget:Source` / `ClonePath` / `PollInterval` | hosted service (padrão `AppCatalogBackgroundSyncService`) | Médio   |
| 10c | Fallback: feed `packages.json` atual mantido como rede de segurança se o clone falhar                           | `AppCatalogSyncService` + `appsettings.json`              | Pequeno |
| 11  | (Opcional) Endpoint de observações de versão dos agents + agregação (telemetria de frota)                       | `AgentAuthController` + service + repo                    | Grande  |
| 12  | (Opcional) sync.ping de catálogo ao detectar versão mais nova (com filtro por inventário)                       | `AgentConfigurationHandlers`/publisher                    | Médio   |

### Ordem recomendada

1. **Fase 1 (1–3):** elimina o sintoma (loop) já — independente do resto.
2. **Fase 2 (4–6) + Fase 3 (7–9):** solução de fundo — rede P2P carrega a versão real.
3. **Fase 4 (10–10c):** ataca a causa raiz da defasagem no servidor — clone raso do branch `master` com deltas de ~30 min, atualizado e importado localmente a cada hora. Itens 11–12 são complemento (telemetria + aceleração) e ficam para depois.

### Riscos e observações

- **Comparação de versões** é o ponto mais frágil (winget não garante semver). Usar comparador tolerante + tie-breaker por ModifiedAtUTC do artifact.
- **Primeiro agent da rede:** sem peer com artifact, o download+cache do fluxo atual já faz o "seeding" — Fase 2 apenas enriquece com a versão real.
- **Compatibilidade:** agents antigos continuam funcionando (server-side não muda contrato); tudo é evolução no agent.
- **Skip benigno virando no-op** reduz visibilidade — manter registro leve (metadata com reason) para auditoria.
- **Pipeline de manifests (Fase 4):** shallow clone (`--depth 1 --single-branch`) consome ~1–2 GB em disco e alguns MB por `git pull` — sem rate limit de API nem token. Falha nunca limpa o catálogo (upsert-only); feed antigo segue como fallback. `git` precisa estar disponível no servidor (já é pré-requisito do deploy Linux).
- **Manifests malformados** existem no winget-pkgs — parser tolerante por pacote (log de erro, pula o pacote, nunca aborta o batch); nova dependência de YAML no servidor (ex. YamlDotNet).

---

## 4. Critérios de aceite

- Task `UpdatePackage` do FoxitReader em máquina já atualizada: 1 execução com skip "atualizado", depois backoff — **sem reexecução a cada slot**.
- Task `UpdateOrInstallPackage` de pacote ausente: instala via P2P quando um peer tem o artifact; senão baixa, publica com **versão real** e instala.
- Artifact `winget:foxitfoxitreader` no índice P2P exibe `Version` preenchida (ex. `2026.1.3.36551`).
- Task que falha 3x seguidas com o mesmo erro: pausa 24h e aparece como Degraded no servidor.
- Commit novo no `microsoft/winget-pkgs` (branch `master`) com versão nova de um pacote reflete no `app_packages` em ≤ 2h (pull 1h + import), e os agents enxergam a versão nova no catálogo efetivo no próximo fetch.
