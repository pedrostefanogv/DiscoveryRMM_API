# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Não lançado]

### Added
- 🏠 **Página inicial do agent (`agentHomeTab`)**: configuração herdável (servidor →
  cliente → site) que define qual aba o painel do agent abre ao iniciar (status, loja,
  atualizações, chat IA, suporte, base de conhecimento). Se a aba configurada estiver
  desabilitada para o agent, ele cai para a aba de Status. Editável no console
  (Servidor/Cliente/Site) e no simulador de configuração efetiva; respeita
  `lockedFieldsJson` e expõe a origem no mapa de herança.
- 🗃️ **Migração M205** (`M205_AddAgentHomeTab`): coluna `agent_home_tab` com default
  `status` no servidor e nula em cliente/site (nula/vazia = herda).
- 🔌 **`GET /api/v1/agent-auth/me/configuration`** passa a expor `agentHomeTab` resolvido.
- 🧭 **Contrato publicado na metadata**: `GET /api/v1/configurations/server|clients|sites/.../metadata`
  devolve `agentHomeTabOptions` (lista de ids aceitos) a partir do `AgentHomeTabCatalog`.
  O console consome essa lista, então só existe uma fonte de verdade — sem lista de
  valores duplicada no site.
- ✅ **Testes**: NUnit (catálogo e lista exata de ids, validação, herança e persistência
  client/site), Vitest (metadata → opções do editor, validação/parse) e Go (`agentconfig`:
  flat + hierárquico + normalização).
- 🤖 **Triagem por IA na auto-atribuição de chamados**: nova estratégia `AiTriage` por
  departamento (assignment_strategy = 3). O responsável é escolhido cruzando o conteúdo e
  a dificuldade do chamado com métricas históricas do atendente (carga, tempo de resolução,
  primeira resposta, SLA, reincidência, CSAT), afinidade com chamados semelhantes já
  resolvidos (pg_trgm) e competências cadastradas no perfil do membro.
- 🧩 **Perfil do membro do departamento**: competências (tags + nível 1-5), teto de chamados
  abertos, peso do gestor e opt-out da triagem por IA.
- 📊 **Snapshots de métricas por atendente** (`technician_metrics_snapshots`) com TTL de
  15 minutos e recálculo periódico via Quartz (`TechnicianMetricsRefreshJob`).
- 🗂️ **Decisões auditáveis** (`ticket_assignment_decisions`): candidatos, score, confiança,
  dificuldade, modelo, tokens, aplicação e motivo de não aplicação.
- ⏱️ **Fila assíncrona de triagem** (`ai_assignment_queue`) processada por
  `AiTicketAssignmentJob` (20 s), com retry exponencial (máx. 3), fallback determinístico
  (round-robin ou menos chamados abertos) e rede de segurança para chamados sem responsável.
- 🔌 **Endpoints**: `/departments/{id}/assignment/team-metrics`,
  `/departments/{id}/members/{userId}/profile`, `/departments/{id}/assignment/metrics/refresh`,
  `/tickets/assignment/{ticketId}/decision|preview|apply` e `/tickets/assignment/decisions`.
- 🖥️ **Console**: opção "Triagem por IA" no cadastro do departamento, card de configuração
  (modo, confiança mínima, fallback, pesos, instruções) e card "Métricas da equipe" com
  edição do perfil por atendente; faixa de decisão da IA no detalhe do chamado.
- 🗃️ **Migração M179** (`M179_AddAiTicketAssignment`), aditiva e com defaults: departamentos
  existentes mantêm as estratégias 0/1/2.
- ✅ **Testes**: NUnit (scorer, extração de sinais, métricas por atendente, fluxos de decisão
  da triagem) e Vitest (card de decisão no detalhe do chamado).

### Added
- 🧰 **Usuário instalador (com sudo) no instalador do servidor**: quando o instalador roda
  como root, ele cria/reutiliza um usuário comum (padrão `discovery`), define a senha (que é
  também a senha do sudo), libera o checkout temporário e reexecuta a instalação como esse
  usuário — a instalação deixa de ficar presa ao root. O sudo no processo filho usa
  `SUDO_ASKPASS`, então nada trava pedindo senha. No modo `--non-interactive` o fluxo é
  opt-in: sem `DISCOVERY_INSTALL_USER_PASSWORD` o instalador segue como root.
- 📋 **Revisão final antes de executar**: o instalador agora imprime **todos** os campos
  coletados (usuário instalador, branch, repositórios, runtime, modo de acesso, hosts/portal,
  token do Cloudflare capturado, TLS, storage, OpenAPI/Scalar, PostgreSQL, NATS completo,
  sessão remota, build e diretórios) e pede confirmação explícita antes de alterar o servidor.
  Segredos nunca são exibidos — apenas se estão definidos/gerados.

### Changed
- 🔧 **Client do MinIO**: a URL `dl.min.io/client/mc/release/linux-amd64/mc` passou a responder
  **410 Gone**; a ordem foi invertida (`aistor` primeiro, a antiga como fallback) para não deixar
  erro no log a cada instalação.
- 📦 **npm atualizado após instalar o Node**: o pacote do Node 22 traz npm 10.x; o instalador
  agora roda `npm install -g npm@${DISCOVERY_NPM_VERSION:-latest}` depois de validar o Node (default
  `latest`, fixável por `DISCOVERY_NPM_VERSION` no env/config). Falha nesse passo é aviso, não
  aborta a instalação.
- 🗄️ **RustFS passa a ser o padrão do storage**: no passo "Storage de arquivos", apertar Enter
  agora instala o **RustFS local** (antes o padrão era `skip`). Digitar `3` continua adiando a
  configuração. No modo `--non-interactive` o padrão também é `rustfs`; use
  `DISCOVERY_OBJECT_STORAGE_MODE="skip"` para não instalar/alterar nada.
- 🔁 **Entrada inválida não derruba mais o instalador no storage**: valor fora de 1/2/3 (ou
  dos nomes) e bucket/endpoint inválidos agora são **re-perguntados**, em vez de encerrar o
  script com erro de normalização.

### Fixed
- 🐛 **Instalador abortava com a API saudável em máquina lenta**: `wait_for_discovery_api_ready`
  esperava apenas 60×2s = 2 min, mas o primeiro start roda ~230 migrations + seeds antes de abrir
  o `/health`; e, com `Restart=always`, o `systemctl is-failed` nunca dispara (a unit fica em
  `activating (auto-restart)`), então o instalador esperava os 2 min cheios mesmo em crash-loop.
  Agora espera até 6 min e aborta em ~15s ao detectar 3 restarts (`NRestarts`), imprimindo os logs.
- 🐛 **`server_configurations.nats_web_socket_external_url` não existia no banco (drift EF×migrations)**:
  a propriedade é mapeada no EF Core (ServerConfiguration.NatsWebSocketExternalUrl), mas nenhuma
  migration criava a coluna — em banco novo a API subia e falhava em runtime
  (`42703: column s.nats_web_socket_external_url does not exist`, via ServerConfigurationRepository
  → ReportService → ReportGenerationJob), reiniciando em loop e abortando o bootstrap
  administrativo (sem admin criado). Nova migração **M225** adiciona a coluna
  (`text NOT NULL DEFAULT ''`, mesmo padrão da vizinha `nats_server_host_external`). Varredura dos
  21 arquivos de mapeamento do DbContext confirmou ser a **única** coluna faltante.
- 🐛 **API em crash-loop por `ALTER COLUMN artifact_id TYPE uuid` sem `USING` (M091)**:
  a migration alterava `p2p_artifact_presence.artifact_id` de `varchar(512)` para `uuid` com
  `AlterColumn(...).AsGuid()`, e o Postgres recusa o cast automático (`42804: column "artifact_id"
  cannot be cast automatically to type uuid`) — a API abortava no startup e o instalador parava em
  `wait_for_discovery_api_ready`. Agora o cast usa `USING artifact_id::uuid` (a tabela é truncada
  antes; dados efêmeros com TTL 2h) e o `Down()` usa `USING artifact_id::text`.
- 🐛 **Bootstrap trocava de branch em falha transitória de clone**: um erro de rede no
  `git clone` da branch pedida fazia o bootstrap cair para outra branch (ex.: `dev` → `release`),
  executando um instalador **diferente** (o do release é o monolítico antigo, que ainda exige a
  senha do usuário instalador e aborta). Agora ele distingue “branch inexistente” (`git ls-remote`)
  de falha de clone, tenta o clone 3× e, se a branch **solicitada** existe mas o clone falha,
  aborta com mensagem clara em vez de trocar de instalador. O fallback (só quando a branch
  realmente não existe) passou a ter default **N**.
- 🐛 **API em crash-loop no primeiro boot: `M130_FixTimestampTimezones` com versão fora do padrão**:
  era a única das 230 migrations com versão numérica curta (`[Migration(130)]`); as demais usam
  `[Migration(2026MMDD_NNN)]`. Como 130 é o menor valor, o FluentMigrator rodava essa migration
  **primeiro** num banco novo, antes de M006 (`CreateTickets`), e o `ALTER TABLE tickets` falhava
  com `42P01 relation "tickets" does not exist` → exceção não tratada → SIGABRT → `Restart=always`
  (o instalador abortava em `wait_for_discovery_api_ready` e o admin nunca era criado). A versão
  passou para `20261215_130` (roda depois de todas) e os `ALTER` agora são idempotentes (só
  convertem coluna existente que ainda esteja `timestamp without time zone`), preservando bancos
  onde a versão 130 já havia sido aplicada.
- 🐛 **Wails instalado em versão diferente da usada pelo agent**: `resolve_wails_version` lia o
  `go.mod` com `[[ -r ]]` direto, mas o clone do agent pertence a `discovery-api` e
  `/opt/discovery-agent-src` é `0750` — o instalador roda como usuário instalador (`discovery`) e
  não conseguia ler o arquivo, caindo no fallback fixo (`v3.0.0-beta.11`) mesmo com o projeto em
  `v3.0.0-beta.28`. A leitura agora usa `sudo` e a busca considera `src/go.mod`, a raiz e
  subdiretórios (até 4 níveis), instalando exatamente a versão declarada.
- 🐛 **Instalação travava em `mc alias set` (Midnight Commander)**: `ensure_mc_cli` aceitava
  qualquer `mc` do PATH, mas em Debian/Ubuntu `mc` também é o **Midnight Commander** (TUI). O
  comando abria a interface e ficava esperando teclado (0% CPU, sem output) — a instalação parava
  logo após o RustFS. O client do MinIO agora é instalado em caminho dedicado
  (`/usr/local/bin/discovery-mc`) e todas as chamadas usam esse caminho.
- 🐛 **`/etc/nats-server.conf` inválido (`Parse error on line 13`)**: o `$(cat <<EOF)` remove o
  newline final de cada bloco e a montagem concatenava `${jetstream_block}${auth_block}`, gerando
  `}authorization {` na mesma linha — o `nats-server` falhava no restart e ficava `failed`. Os
  blocos agora são gravados em linhas separadas.
- 🐛 **Instalador abortava ao escrever o `discovery.env` com `REMOTE_ACCESS_NATS_JWT_SIGNING_KEY:
  unbound variable`**: o heredoc usava a variável sem default e ela nunca é definida (a API lê
  `RemoteAccess__Nats__JwtSigningKey`). Agora a chave é resolvida antes do `tee` — preserva a já
  gravada no env (regerar invalidaria sessões de acesso remoto em andamento) e gera uma nova
  (64 caracteres) apenas quando não existe nenhuma.
- 🐛 **Instalador abortava em `nsJSON`/dotnet por diretórios fixos em `/tmp`**: o passo do plugin
  NSIS fazia `sudo mkdir -p /tmp/nsJSON-1.1.1.1` (dono root) e em seguida `unzip` como usuário
  instalador — o `unzip` não conseguia escrever (`Permission denied`) e, sob `set -e`, a instalação
  abortava logo após a confirmação. O mesmo padrão existia em `/tmp/dotnet-install.sh` e
  `/tmp/packages-microsoft-prod.deb`. Agora os três usam `mktemp -d` (dono do próprio usuário),
  com extração/limpeza tratadas e sem abortar se o zip falhar.
- 🐛 **Instalador falhava no fim do wizard com `NATS_JS_FANOUT_STREAM_ENABLED=1 exige
  NATS_JS_ENABLED=1`**: quando o host já tinha um `nats-server.conf` sem o bloco `jetstream` (ex.:
  pacote do SO ou sobra de tentativa anterior), o instalador inferia `NATS_JS_ENABLED=0` mas
  mantinha o fan-out no default `1`, abortando depois do último prompt. Agora o fan-out segue o
  `NATS_JS_ENABLED` quando não é definido explicitamente, a inferência de JetStream a partir da
  conf só vale ao adotar uma instalação existente (`discovery.env` presente) e há aviso quando o
  JetStream fica desabilitado. A falha explícita permanece apenas se o operador definir os dois
  valores de forma contraditória.
- 🐛 **Prompt da senha do usuário instalador abortava com Enter em branco**: agora a senha é
  obrigatória de forma explícita, aceita senha gerada (mostrada uma única vez), re-pergunta em
  caso de senha curta/divergente e não encerra mais o instalador. A senha é validada (8–128
  caracteres, sem `:` nem caracteres de controle) e o **nome** do usuário também é re-perguntado
  quando inválido, em vez de abortar. Se `--config` não for legível pelo usuário instalador, o
  instalador segue como root (com aviso) para não perder a configuração.
- 🐛 **Instalador abortava em silêncio logo após a etapa do NATS**: em instalação nova,
  `load_existing_remote_session_defaults` devolvia 1 enquanto
  `/etc/discovery-api/discovery.env` ainda não existia e, sob `set -e`, o chamador
  (`prompt_remote_session_duration`) encerrava o instalador sem nenhuma mensagem — o
  prompt "Subject do auth callout" era o último a aparecer. Agora o loader retorna 0
  explicitamente e o wizard segue para as etapas seguintes.
- 🔧 **Token do Cloudflare Tunnel aceito como comando colado**: o painel da Cloudflare não
  mostra o token puro, e sim um comando (`cloudflared tunnel run --token eyJ...` ou
  `cloudflared service install eyJ...`). O instalador agora extrai o token do texto
  colado, valida o formato, aceita também o token puro e descarta o restante de um paste
  multi-linha para não consumir a resposta do próximo prompt.
- 🔧 **Prompts mais consistentes no instalador**: `OPENAPI_SCALAR_ENABLED` (UI do Scalar
  em produção) passa a ter padrão `n` em vez de exibir `0`, e
  `NATS_AUTH_CALLOUT_ENABLED` passa a aceitar `y/n` (padrão `y`), normalizado para
  `1/0` antes de gerar o env do NATS.
- 🐛 **Override de background processing por cliente era descartado**: o
  `ClientConfigurationRepository.UpdateAsync` copia campo a campo e não copiava
  `BackgroundProcessingSettingsJson`, que é lido pelo `ConfigurationResolver`. O PATCH/PUT
  do cliente perdia a alteração silenciosamente.
- 🐛 **Vínculo de detecção do pacote custom não era salvo**: o editor enviava
  `detectionMode`, `inventorySoftwareId`, `detectionName`, `detectionPublisher`,
  `detectionMinVersion`, `detectionCommand` e os `*CommandKind`, mas o
  `UpsertCustomAppPackageCommand` não declarava esses parâmetros — o model binding do
  MVC descartava o payload e o pacote era gravado com `DetectionMode=None` e vínculo
  nulo. Os oito campos agora fazem parte do command e são repassados ao serviço.
- 🐛 **Busca do inventário de software só casava a frase inteira**: a consulta usava um
  único `ILIKE %texto%`, então "Foxit Editor" não encontrava "Foxit PDF Editor". A busca
  agora é quebrada em termos e TODOS precisam casar (AND), permitindo também combinar
  nome, fabricante, versão, hostname, site e cliente. Curingas digitados (`%`, `_`) são
  escapados.
- 🐛 **Software duplicado no inventário**: o fingerprint de `software_catalog` incluía
  `install_id`/`serial`/`source`, voláteis por máquina e por hive de registro — o mesmo
  produto ganhava uma linha de catálogo por instalação (homolog: 1.857 linhas para 802
  nomes; 1.305 softwareIds presentes para 705 nomes). A identidade passou a ser o nome
  normalizado; o upsert consolida as variações, escolhe a **versão mais nova** quando o
  host expõe o mesmo app em hives 32/64 bits e a **migração M224** funde o catálogo
  existente, preservando os metadados de atualização (`available_version`,
  `update_available`, `update_package_id`) e repontando inventário e vínculos de pacotes
  custom.
- ✨ **Migração M224** (`M224_ConsolidateSoftwareCatalogIdentity`).

### Notes
- A triagem exige a integração de IA habilitada (AIIntegration.Enabled + ChatAIEnabled +
  API key). Sem isso, a decisão é registrada como `ai_unavailable` e o fallback é aplicado.
- Configuração de background em `BackgroundJobs:AiTicketAssignment:*` e
  `BackgroundJobs:TechnicianMetrics:*`.

### Added (segunda fase — tokens, custo, aprendizado e alertas)
- 🎚️ **Orçamento de tokens por modelo**: teto de saída agora é `min(pedido, teto do departamento,
  capacidade real do modelo, teto do produto)`. O clamp de configuração subiu de 8.000 para
  32.768 (antes, 16.000 era revertido silenciosamente para o default) e o catálogo passou a
  informar `max_completion_tokens` também no caminho OpenAI nativo. O truncamento do prompt da
  triagem deixou de ser 4.000 caracteres fixos e passou a derivar da janela de contexto.
- 💸 **Cost control nos fluxos de IA de chamado**: `ProcessTicketPromptAsync` e
  `ProcessTicketPromptJsonAsync` respeitam `IAiCostControlService` (rate limit + budget diário por
  cliente/site), com `AiUsageLimitException` e degradação auditável (`ai_budget_exceeded`) na triagem.
- 🧠 **Aprendizado híbrido por departamento** (Off / Sugerir / Automático):
  extração de competências do histórico de chamados resolvidos (`technician_skill_suggestions`) e
  recalibração dos pesos pela taxa de override (`ai_weight_suggestions`), com evidência, limites de
  ajuste por ciclo, faixa min/max e renormalização. Job diário `AiAssignmentLearningJob` e endpoints
  `/departments/{id}/learning/*`. Nova tela de sugestões no console.
- 🎯 **Loop de override**: trocar manualmente o responsável após uma decisão aplicada agora marca
  `overridden_at`/`overridden_by_user_id` (também no endpoint de assign) — insumo da calibração.
  O autor da alteração passou a ser registrado na atividade de atribuição.
- 🚨 **Chamados criados por alerta/evento entram na auto-atribuição** via
  `ITicketAutoAssignmentService` (responsável explícito da regra vence; senão round-robin,
  menos abertos ou fila da triagem por IA).
- 🧱 **Seam de herança de departamento**: `IDepartmentTeamResolver` é a fonte única da equipe
  candidata (herança por `InheritFromGlobalId` segue NÃO implementada, apenas preparada).
- 🗃️ **Migração M180** (`M180_AddAiTokenBudgetAndLearning`), aditiva e com defaults.
- 🖥️ **Card dedicado de Processamento em Segundo Plano**: campos tipados no servidor e, no cliente,
  override por campo com indicação "herdado do global"; painel de últimos ciclos por escopo, botões
  "rodar agora" e backfill/cancelar.
- ⚙️ **Tick dinâmico a partir do banco**: `BackgroundProcessingScheduleService` reprograma os triggers do
  Quartz (`TickSeconds` de métricas/triagem) no startup, após salvar a configuração e por job de
  sincronização — sem restart; endpoint `GET /configurations/background-processing/schedule`.
- 🧱 **Backfill de snapshots de métricas**: recálculo forçado da janela configurada em lotes, com pedido
  idempotente, progresso persistido (`processing_scope_state`), cancelamento e limpeza opcional de
  snapshots órfãos (`POST /configurations/background-processing/backfill`).
- ⏱️ **Processamento periódico configurável (métricas + triagem)**: ciclos por escopo de cliente com
  tick fixo (5 min para métricas) e vencimento por intervalo configurado, **global herdado e sobrescrito
  por cliente** (campos ausentes herdam; bloqueável por `LockedFieldsJson`). Estado do último ciclo por
  escopo em `processing_scope_state`.
- 🧮 **Métricas agregadas em SQL** (uma linha por atendente em vez de todos os tickets do lote), com
  índice parcial `ix_tickets_assigned_user_created`, lotes com rotação pelo snapshot mais antigo, teto de
  lotes/tempo por execução e **leitura sem recálculo** (snapshot-only; bootstrap só para quem nunca teve
  snapshot). A triagem registra `metrics_snapshot_age_minutes` na decisão.
- 🎯 **Triagem em lotes por cliente**: cota por cliente (`MaxPerClientPerRun`) para fairness, intervalo
  por escopo, retry/backoff por configuração e `EnqueueOnCreate = false` para o modo somente-lotes.
- 🔌 **Endpoints** `GET /configurations/background-processing/effective` e `/status` (efetivo e último ciclo).
- 🗃️ **Migração M181** (`M181_AddBackgroundProcessingSettings`) e documentação em
  `docs/BACKGROUND_PROCESSING_SETTINGS.md`.
- 💬 **Orçamento model-aware no chat do agente**: `AiChatStreamingOrchestrator` (stream e multi-round,
  com sínteses forçadas) e `ProcessSyncAsync` passaram a limitar o teto do tenant pela capacidade real
  do modelo e pelo teto do produto. O clamp fixo de 8000 no `requestMaxTokens` síncrono foi removido.
- 🔁 **Orçamento de tokens automático em todos os fluxos de IA de chamado**: o model-aware passou a
  ser resolvido dentro do `AiChatService` (triagem textual, resumo, resposta sugerida, artigo de KB e
  a triagem de atribuição). Os handlers de ticket também truncam o prompt pelo orçamento (sem
  estourar a janela de contexto), usam um site do mesmo cliente quando o chamado não tem `SiteId` e
  leem o chamado uma única vez por ação.
- ✅ **Testes**: `AiTokenBudgetTests`, `WeightCalibratorTests`, `SkillExtractorTests`,
  `TicketAutoAssignmentServiceTests`, `TicketAssignmentOverrideTrackerTests`,
  `AiCostControlServiceTests`, `AiAssignmentLearningServiceTests` e `TicketAiBudgetTests`.

### Notes (segunda fase)
- `BackgroundJobs:AiAssignmentLearning:Enabled|HourUtc` controla o ciclo diário.
- Modelos sem metadados no catálogo (`openrouter/auto`, ids inexistentes como
  `deepseek/deepseek-flash-latest`) usam teto conservador de 4.096 tokens e ficam registrados na decisão.
## [1.0.0] - 2026-04-22

### Added
- ✨ **Core API**: Discovery RMM Server - gerenciamento centralizado de agents
- ✨ **NATS Integration**: Message broker com autenticação por token
- ✨ **PostgreSQL Support**: Database com pgvector para embeddings de IA
- ✨ **MeshCentral Integration**: Sincronização de dispositivos e controle remoto
- ✨ **AI Chat Service**: Integração com OpenAI/Ollama para análise de tickets
- ✨ **Embeddings**: Busca semântica com pgvector (1536-dim)
- ✨ **Auto-Ticketing**: Motor automático de geração de chamados
- ✨ **Custom Fields**: Sistema flexível de campos customizáveis
- ✨ **API Tokens**: Autenticação com tokens de longa duração
- ✨ **WebSocket (SignalR)**: Comunicação em tempo real com agents
- ✨ **PSADT Integration**: Suporte para scripts de deployment
- ✨ **Object Storage**: Suporte para storage local, MinIO e S3
- ✨ **Reporting**: Gerador de relatórios com templates personalizáveis
- ✨ **Self-Update**: Script de auto-atualização para agent

### Infrastructure
- 🐳 **Docker Support**: Dockerfile para containerização
- 📦 **Linux Installer**: Script de instalação automatizada (bash)
- 🔒 **Self-Signed Certs**: Certificados para acesso interno
- 🌍 **Cloudflare Tunnel**: Suporte para acesso externo seguro
- 📊 **Monitoring**: Health checks e métricas básicas

### Security
- 🔒 Autenticação com JWT + API Keys
- 🔒 NATS com autenticação callout
- 🔒 Variáveis de ambiente para secrets
- 🔒 CORS configurável
- 🔒 Rate limiting em APIs críticas
- 🔒 Validação de input em todos os endpoints

### Testing
- ✅ Testes unitários completos (Discovery.Tests)
- ✅ Mocks para services críticos
- ✅ Fixture factories para dados de teste

### Documentation
- 📖 Deployment guide (Linux/Windows)
- 📖 Configuration guide
- 📖 API documentation
- 📖 MeshCentral integration guide
- 📖 Contributing guidelines

### Breaking Changes
- Nenhuma (primeira release estável)

---

## Próximas Versões (Planejadas)

### [1.1.0] - Planejado
- [ ] Kubernetes support
- [ ] Multi-tenant isolation
- [ ] Advanced audit logging
- [ ] Database replication

### [2.0.0] - Roadmap
- [ ] GraphQL API
- [ ] Real-time dashboards (WebGL)
- [ ] Distributed agents
- [ ] Plugin system

---

## Guidelines

### Changelog Format

```markdown
### Added
- ✨ Nova funcionalidade

### Changed
- 🔄 Mudança em funcionalidade existente

### Fixed
- 🐛 Correção de bug

### Deprecated
- ⚠️ Feature será removida em versão futura

### Removed
- ❌ Feature removida

### Security
- 🔒 Correção de segurança
```

### Issue Linking

```markdown
Closes #123
Fixes #456
Related to #789
```

### Version Bumping Rules

| Tipo | MAJOR | MINOR | PATCH |
|------|-------|-------|-------|
| Breaking Change | ✅ | | |
| Nova Feature | | ✅ | |
| Bugfix | | | ✅ |
| Hotfix Crítico | | | ✅ |
| Prerelease | | | -alpha/-beta |

---

### Header Format

```markdown
## [X.Y.Z] - YYYY-MM-DD

[Unreleased], [YYYY-MM-DD] (Past)
```

### Keep Last 3 Versions

Versões antigas são arquivadas em `docs/CHANGELOG_ARCHIVE.md`.

---

Última atualização: 2026-04-22
