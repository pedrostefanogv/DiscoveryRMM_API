# PLANO: Warmup P2P antes da execução de automação (startup readiness gate)

> **STATUS (2026-09-03): itens 1, 2, 3, 4 (parcial) e 5-B1/B3 IMPLEMENTADOS. Item 6 parcialmente resolvido.**
>
> - **Item 1+2 (agent)**: `ReadyCh()`/`IsReady()`/`markReady()` no Coordinator (`app/p2p/p2p.go`), sinalizado no fim do primeiro lan-probe de startup OU ao confirmar o primeiro peer (`p2p_lan_probe.go`). Router (`automation_p2p.go`) espera com teto de 45s antes de declarar "artifact não encontrado". Fallback winget preservado.
> - **Item 3 (agent)**: `SetStartupReadinessWaiter` no automation Service (`core/automation/service.go`), chamado uma vez antes do primeiro `refreshPolicy` em `Run`. O `App` (`app.go`) injeta waiter que espera `ReadyCh()` ou **teto de 120s** (skip se P2P desabilitado). Triggers immediate/checkin de startup passam a rodar pós-discovery.
> - **Item 4 (servidor)**: `P2pArtifactPresenceDto.ArtifactId` agora aceita string (Guid ou ID sintético do agent, ex.: "winget:7zip7zip"). `P2pService.ResolveArtifactPresenceId` deriva Guid determinístico (UUIDv5/MD5) e grava `IdIsSynthetic=true`. Resolve o HTTP 400 do log. _(fallbackReason na telemetria ainda não implementado)_
> - **Item 5 — seeds resilientes (implementado no agent)**:
>   - **Fonte de download por score**: `PeersWithArtifactScored` (`p2p_status.go`) — Install/Upgrade ordenam peers por score (peer ativo +1.0, conectado via libp2p +0.5, anúncio fresco até +0.5), com desempate determinístico.
>   - **Re-seed automático** (`p2p_reseed.go`): loop de 2min + jitter; artifact que circula na rede mas falta localmente → eleição de fetcher (score CPU/RAM, lease 90s) → download via swarm → republica. **Todos os agents convergem a ter o catálogo = todos viram seed.** Resolve "seed desligou" e "agent novo adota o catálogo".
> - **Item 5-B1 — Fetch proativo via COMANDO do servidor (REVISÃO 3, IMPLEMENTADO)**:
>   - **Sem auto-preload no policy-sync.** O servidor envia um **comando explícito** `p2ppreload` (novo `CommandType.P2pPreload = 17`) — single-agent (`RequestP2pPreloadCommand` + handler CQRS) ou **fan-out site/client/global** (infraestrutura de fan-out já aceita o novo tipo). Payload validado: `{action: preload|cancel, packages: [{packageId, actionType}]}`.
>   - **Coordenação por score entre agentes** (`p2p_preload_command.go` no agent): todos recebem o comando, mas o download é escalonado por capacidade — **stagger = 90s × (1 − score/10)**, score = CPU/RAM livres (mesma ideia da eleição de fetcher). O agent de maior score baixa primeiro e publica; os demais, durante o stagger, **checam o índice P2P a cada 5s** e antecipam o download **da LAN** assim que o artifact aparece. Sem tempestade: 1 download da internet + propagação P2P.
>   - Estado real continua decidindo: pacote instalado/sem update pendente → agent ignora o item (`ShouldPreloadPackage`).
>   - Pendente: endpoint REST no controller para a UI disparar `RequestP2pPreloadCommand` (dispatcher CQRS pronto).
> - **Item 6 — NATS permissions violation**: ACL do `NatsCredentialsService.BuildAgentSubjects` JÁ concede todos os subjects usados pelo agent. A causa real no log é **mismatch de siteId após transferência de site**: o JWT é emitido com o site atual do banco (`01a06390...`), mas o transporte do agent publica/assina com o site antigo (`019dead3-845a...`). Fix pertence ao fluxo de transferência de site (ver `AGENT_TRANSFER_SYNC_FIX_PLAN.md` e o commit `feat(nats): tratar comando nats.reconnect` do agent).
> - Validado: `go build ./app/...`, `go vet ./app/...`, `go test ./app/core/automation/... ./app/p2p/... ./app`, `dotnet build Discovery.Infrastructure` — todos OK.
>
> Pendente: A1 (unificar fórmula seed-plan, bug `total=0→2` no job), A2/A3 (seeds-alvo explícitos, `amISeed`), B4 (fetch proativo baseado em seeds-alvo — hoje é baseado nas tasks do policy-sync, que cobre o caso prático), B5 (warmup informado pelo seed-plan), fallbackReason na telemetria, inventário duplicado no post-bootstrap.

## Problema (evidência nos logs de 2026-09-03)

Na inicialização do agent, a ordem atual dos eventos é:

1. `19:11:39` — **policy sync de automação conclui** e `reconcilePolicy` dispara tasks `TriggerImmediate`/`TriggerOnAgentCheckIn` **imediatamente** (`service.go:313-326`).
2. `19:11:41` — só **agora** o P2P sobe: `libp2p host iniciado`, `descoberta iniciada`.
3. `19:11:43` — o router de pacotes (`automation_p2p.go`) procura o artifact na rede P2P → **"artifact nao encontrado na rede P2P"** → cai no fallback `winget download` (internet).
4. `19:11:56` — **15s depois** o discovery encontra 5 peers, e o peer `019fc513` **já tinha 5 artifacts** (incluindo os mesmos pacotes).

**Custo real medido no log:** downloads de ~2MB (7zip), ~368MB (Foxit) e ~512MB (Chrome) direto da internet, quando havia peers na LAN com os artifacts em cache. Disco chegou a 100% de utilização de leitura/escrita por minutos.

Causa raiz: o router não distingue **"P2P não está pronto (discovery incompleto)"** de **"artifact realmente não existe na rede"**. Ambos caem no mesmo fallback imediato para winget/internet.

Agravantes observados no mesmo log:

- NATS com `permissions violation` em Publish heartbeat e Subscribe command/sync.ping/p2p.discovery → churn de reconexão (nativo → wss) durante o startup.
- Inventário hardware/software enviado 2x em duplicidade no post-bootstrap.

## Proposta — Gate de readiness com grace period

### 1. Sinal de "P2P pronto" no Coordinator (`app/p2p/p2p.go`)

- Adicionar conceito de **warmup concluído**: primeira rodada de lan-probe + primeira rodada de gossip concluídas (ou `N` peers descobertos, ou timeout).
- Expor `Ready() bool` / canal `ReadyCh() <-chan struct{}` no Coordinator.
- Critério sugerido: pronto quando (`peers >= 1` após primeiro probe) OU (`timeout 45s`), o que vier primeiro. Com 0 peers no timeout, o fallback para winget é legítimo.

### 2. Espera com timeout no router de pacotes (`automation_p2p.go`)

No `installViaP2P` (Install/Upgrade), antes de declarar "artifact não encontrado":

```
esperar ReadyCh() com timeout (ex.: 30-60s, configurável)
se estourar o timeout sem readiness → log + prosseguir com fallback winget (comportamento atual)
```

- Só então consultar o catálogo P2P. Assim, "não encontrado" significa realmente "a rede olhou e não tem".
- Mantém o fallback atual como rede de segurança — nunca bloqueia a execução indefinidamente.

### 3. Adiar triggers de startup na automação (`service.go` `reconcilePolicy`)

- Opção A (mais simples): atrasar o **primeiro** `RefreshPolicy`/`reconcilePolicy` até o P2P estar ready (mesmo canal do item 1) com timeout — os triggers `immediate`/`checkin` de startup passam a rodar pós-discovery.
- Opção B (mais granular): manter o sync imediato (estado/UX), mas enfileirar execuções de tasks `TriggerImmediate`/`TriggerOnAgentCheckIn` originadas de `source=startup` num **delay/jitter** (ex.: 30s + random 0-30s) — também reduz thundering herd quando vários agents ligam juntos (mesmo site, mesma task).

### 4. Distinguir erro de "não pronto" no log/telemetria

- Log diferenciado: `[automation][p2p] P2P aguardando discovery (aguardando 30s...)` vs `artifact nao encontrado após discovery completo`.
- Telemetria P2P já existe (`p2p/api.go`); adicionar campo `fallbackReason: {p2p_not_ready, artifact_missing, installer_exec_failed}` — permite medir no servidor (`P2pAgentTelemetry`) quanto bandwidth está sendo desperdiçado por falta de readiness.

### 5. Seeds resilientes — ANÁLISE DETALHADA (revisão 2)

> **Preocupação do usuário:** sites com muitos agentes podem perder todos os seeds (máquina desliga/perde rede); e sites em **deploy progressivo** (agentes chegando aos poucos) começam SEM nenhum seed — cada agent baixa sozinho da internet ou não há replicação.

#### 5.1 Como funciona hoje (investigação completa)

**Server-side — dois produtores de seed-plan, com fórmulas divergentes:**

1. **On-demand** (`P2pService.GetSeedPlanAsync` → `GET me/p2p/seed-plan`):
   - Ativos = `P2pAgentTelemetries` distintos por `SiteId` nos **últimos 10 min** (janela fixa).
   - `CalculateSelectedSeeds(total, 10, 2) = min(max(ceil(total*10%), 2), total)`; `total=0 → 0`.
2. **Quartz** (`P2pMaintenanceJob`, 15 min): mesma fórmula, mas **sem guard de total=0** → site inativo fica com `SelectedSeeds=2` (diverge do on-demand que dá 0).
3. **Limitação estrutural**: o plano é um _número_ — não diz **quais** agentes são seeds, não é cumprido por ninguém, e não há verificação de saúde ("será que os 2 seeds selecionados estão vivos e com os artifacts?").

**Agent-side — três mecanismos, todos desconectados entre si:**

| Mecanismo                                                                                         | Onde                                    | Estado                                                                                                                                                                                           |
| ------------------------------------------------------------------------------------------------- | --------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **Seed-plan local** (`BuildSeedPlan(knownPeers+1)`)                                               | `p2p.go` discoveryTick/OnResourceSynced | Só alimenta telemetria/UI; nada o cumpre.                                                                                                                                                        |
| **Eleição de fetcher** (`fetchStates`, `runPendingElections`, lease 90s, heartbeat 15s, graça 2s) | `p2p_fetch*.go`                         | **Órfã**: `fetchStates` só é criado via `handleFetchCandidacy` (candidatura de OUTRO peer). Nenhum código local registra "este artifact é desejado" → a eleição nunca parte de um agent sozinho. |
| **Download+cache da automação** (`downloadAndCacheForP2P`)                                        | `automation_p2p.go`                     | É o único produtor de seeds _de fato_: o 1º agent que instala um pacote publica o artifact no cache P2P (virou seed por efeito colateral).                                                       |

**Consequências práticas (exatamente os cenários do usuário):**

1. **Site em deploy progressivo**: agents vão chegando; o 1º baixa tudo via winget (com o warmup dos itens 1-3, agora espera peers — mas não há peers ainda → 120s de espera inútil no pior caso). Só vira seed _se e quando_ uma task de automação rodar. Ninguém "adota" a lista de artifacts do site.
2. **Site grande**: os artifacts existem só nos agents que passaram pela automação. Se esses desligam → rede fica sem o artifact → novos agents caem no winget direto. **Não há re-eleição proativa de novo seed para repor o perdido.** A eleição existe, mas não há gatilho local.

#### 5.2 Desenho proposto — "seed guarantee" em 3 camadas

**Camada A — Servidor: seed-plan que diz QUEM e é observável**

- **A1. Unificar fórmula** em `Discovery.Core/Helpers/P2pSeedPlanMath.cs`: `Selected = min(max(ceil(total*pct/100), minSeeds), total)`, `total<=1 → 0 seeds`. Usar em `P2pService` e `P2pMaintenanceJob` (corrige a divergência do `total=0→2`).
- **A2. Seeds-alvo determinísticos**: além do número, o servidor **seleciona quais agents** são seeds-alvo — determinístico e estável: ordena agentes ativos do site por `AgentId` e marca os primeiros `Selected` (persistir em nova tabela `p2p_seed_assignments` ou campo JSON no `P2pSeedPlan`). Com rotação: se um seed-alvo está offline > X min na telemetria, promove o próximo da lista (round-robin por ordem de `LastSeenAt`).
- **A3. Contrato para o agent** no `GET me/p2p/seed-plan` (DTO estendido, retrocompatível):

  ```json
  { "plan": {...}, "seedAgentIds": ["...","..."], "amISeed": true,
    "activeWindowMinutes": 10, "artifactPresenceAgents": 4 }
  ```

  - `amISeed` — o agent sabe que tem papel de seed (deve manter cache e responder).
  - `seedAgentIds` — todos sabem quem DEVERIA estar servindo (permite detectar seeds mortos).
  - `artifactPresenceAgents` — contagem de agents com `P2pArtifactPresence` ativo (2h): sinal real de cobertura.

- **A4. Job de manutenção estendido**: no `P2pMaintenanceJob`, além de recalcular, comparar `SelectedSeeds` vs `artifactPresenceAgents` por artifact popular do catálogo e logar/alertar degradação (observabilidade no dashboard Ops P2P: "cobertura de seeds por site").

**Camada B — Agent: registro de artifacts desejados (ativa a eleição órfã)**

- **B1. Desired-artifact registry**: no sync (`appstore`/`automationpolicy`/`configuration`), o agent extrai os `packageId` relevantes ao seu escopo e registra `desiredArtifacts` local (SQLite). **Isto alimenta `fetchStates`** — resolve a eleição órfã: `runPendingElections` passa de fato a trabalhar.
- **B2. Papel de seed vindo do servidor (A3)**: se `amISeed=true`, o agent mantém os desired artifacts em cache e aceita replicar; se não é seed, só baixa sob demanda.
- **B3. Re-seed por perda de cobertura** (o cenário "seed desligou"): quando o gossip/índice mostra que um artifact _desejado_ deixou de ser anunciado por qualquer peer (número de anunciantes < 1), o agent participa de eleição de fetcher (código já existe: broadcast candidatura → `electBestFetcher` → `executeFetch` via swarm). Com o registry da B1, `runPendingElections` detecta e re-povoa a rede automaticamente — **recuperação sem intervenção do servidor**.
- **B4. Bootstrap de site em deploy** (o cenário "agentes chegando aos poucos"): primeiro/primeiros agentes do site (`seedAgentIds` da A3 ou heuristicamente: `knownPeers==0` por > T): ao sincronizar appstore/policy, disparar fetch proativo dos artifacts desejados **via winget download + publish no cache** (mesma rotina `downloadAndCacheForP2P` da automação, mas sem instalar). Assim, quando o 2º, 3º... agent chegar, o warmup dos itens 1-3 encontra os artifacts na LAN. Custo controlado: apenas para `amISeed` ou para os K primeiros agents do site.
- **B5. Warmup informado (ligação com itens 1-3)**: no startup, o agent consulta o seed-plan (cache 5 min já existente em `p2p_api.go`) e decide:
  - `amISeed` ou `seedAgentIds` não-vazio → espera `ReadyCh()` (teto 120s, atual);
  - `totalAgents<=1` / sem seeds e sem artifacts → pular espera (winget direto, sem os 120s);
  - erro na API → espera conservadora (nunca pior que hoje).
- **B6. Anti-thundering no bootstrap**: fetch proativo (B4) só no seed; demais agents esperam o artifact aparecer no gossip (timeout curto) — evita N agents baixando o mesmo instalador da internet simultaneamente.

**Camada C — Fechamento do ciclo de vida do seed**

- Lease/heartbeat de fetcher já existem (90s/15s) — manter.
- **C1. Sucessão**: quando o seed-alvo sai da telemetria > 10 min, `P2pMaintenanceJob` promove o próximo (A2); agents detectam via seed-plan no próximo refresh (5 min) e o novo seed ativa B4.
- **C2. Telemetria**: reportar no payload de telemetria o papel atual (`amISeed`), `desiredArtifacts` atendidos vs faltantes — o dashboard Ops P2P passa a mostrar cobertura real.

#### 5.3 Ordem de implementação sugerida (valor × risco)

1. **A1** (unificar fórmula) — trivial, corrige bug real.
2. **B1 + B3** (registry desejados + re-seed por perda) — ativa a eleição órfã; recuperação automática de seed perdido; sem mudança de contrato.
3. **B4** (bootstrap de deploy) — resolve o cenário principal do usuário; depende de B1.
4. **A3 + B2 + B5** (papel de seed explícito) — refinamento; requer contrato novo.
5. **A2/A4/C1/C2** (rotação, observabilidade, sucessão) — polimento.

**Riscos/mitigações:**

- B4 baixa artifacts que ninguém instala ainda → limitar a artifacts de policies/appstore _ativas_ do site, com cap de tamanho e TTL de limpeza já existente (`CleanupExpiredP2PTempArtifacts`).
- Bootstrap com agent sozinho numa rede sem nenhum par: B4 só roda quando `amISeed` (servidor diz) — se o site tem 1 agent, ele já é o seed natural e o custo é o mesmo de quando instalar.
- Eleição em enxame no reboot de rede: `electionGracePeriod` (2s) + lease (90s) + score de carga já mitigam; adicionar jitter aleatório antes da candidatura (0-2s).

### 6. Correções correlatas (fora do escopo imediato, mas detectadas)

- **NATS ACL**: `permissions violation` em Publish heartbeat e Subscribe `command`/`sync.ping`/`p2p.discovery` — o JWT emitido não cobre os subjects que o agent usa. Corrigir a ACL do perfil `AgentIdentity` no servidor (ver `docs_planejamento/NATS_SUBJECTS_ACL.md`). Isso elimina o churn de reconexão no startup.
- **Telemetria 400**: `$.artifacts[0].artifactId` não converte para `Guid` — o agent envia `artifactId` string (`winget:7zip7zip`); validar contrato no endpoint de telemetria do servidor.
- **Inventário duplicado** no post-bootstrap (hardware/software enviados 2x) — revisar o gatilho de sync no `agent-sync`.

## Ordem sugerida de implementação

1. Item 2 (espera com timeout no router) — menor risco, maior ganho imediato.
2. Item 1 (sinal de readiness no Coordinator) — pré-requisito limpo do item 2.
3. Item 4 (logs/telemetria) — observabilidade para validar o ganho.
4. Item 3 (adiar triggers) — depois, com cuidado para não quebrar expectativa de "task immediate roda logo".
5. Itens 5/6 conforme prioridade.

## Riscos

- Timeout mal calibrado atrasa tasks em redes sem P2P → mitigado com timeout curto e fallback preservado.
- `TriggerImmediate` semanticamente deveria ser imediato; o adiamento (item 3) deve ser opt-in por configuração.
