# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Não lançado]

### Added
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

### Notes
- A triagem exige a integração de IA habilitada (AIIntegration.Enabled + ChatAIEnabled +
  API key). Sem isso, a decisão é registrada como `ai_unavailable` e o fallback é aplicado.
- Configuração de background em `BackgroundJobs:AiTicketAssignment:*` e
  `BackgroundJobs:TechnicianMetrics:*`. Documentação em `docs_planejamento/AI_TICKET_ASSIGNMENT.md`.

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
- 📖 **Documentação**: seções novas em `docs_planejamento/AI_TICKET_ASSIGNMENT.md` e
  `docs_planejamento/DEPARTMENT_INHERITANCE_SEAM.md`.
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
