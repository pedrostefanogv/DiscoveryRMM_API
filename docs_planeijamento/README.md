# Discovery RMM — Documentation

> **Última reorganização:** 2026-04-29  
> **Branch:** dev  
> **Status do build:** ✅ passando (0 erros, 0 warnings)

---

## 📑 Índice rápido

### 📘 Capability docs (o que está implementado)

| # | Documento | Escopo |
|---|----------|-------|
| 1 | [`AUTHENTICATION.md`](AUTHENTICATION.md) | Login, MFA, primeiro acesso, permissões, autorização por escopo |
| 2 | [`MESHCENTRAL.md`](MESHCENTRAL.md) | Embed, identity sync, group policy, node-link, diagnostics |
| 3 | [`FRONTEND_API_CONTRACTS.md`](FRONTEND_API_CONTRACTS.md) | Guia de consumo da API para front/site: endpoints, contratos, validações, auth e realtime |
| 4 | [`MESSAGING_NATS.md`](MESSAGING_NATS.md) | Auth callout, credenciais scoped, subjects canônicos, remote debug dual-channel |
| 5 | [`OBJECT_STORAGE.md`](OBJECT_STORAGE.md) | S3-compatible, ticket attachments, report download presigned |
| 6 | [`REPORTING.md`](REPORTING.md) | Datasets, templates, preview, execuções, multi-source layouts |
| 7 | [`PSADT_ALERTS_PLAN.md`](PSADT_ALERTS_PLAN.md) | Alertas PSADT (Toast/Modal) — implementado ✅ |

### 🗺️ Planos e roadmaps (o que ainda está em andamento)

| # | Documento | Status |
|---|----------|--------|
| 11 | [`AGENT_UPDATE_PLAN.md`](AGENT_UPDATE_PLAN.md) | ⚠️ Fase 1 concluída (releases, build, manifest, download). Fases 2-3 pendentes |
| 12 | [`PLANO_AUTO_TICKET.md`](PLANO_AUTO_TICKET.md) | 📋 Planejado — motor de auto-ticket por eventos de monitoramento |
| 13 | [`KNOWLEDGE_EMBEDDING_ANALYSIS.md`](KNOWLEDGE_EMBEDDING_ANALYSIS.md) | ✅ Pipeline de embeddings unificado (Quartz Job), batching, métricas |
| 14 | [`MESHCENTRAL_ROADMAP.md`](MESHCENTRAL_ROADMAP.md) | 📋 O que falta: conta técnica, ACL por node, login token, node id no Agent |

### 📐 ADRs (Architecture Decision Records)

| # | Documento | Decisão |
|---|----------|---------|
| 17 | [`ADR_API_VERSIONING.md`](ADR_API_VERSIONING.md) | URL path `/api/v1/` com Asp.Versioning.Mvc |
| 18 | [`ADR_BACKGROUND_JOBS.md`](ADR_BACKGROUND_JOBS.md) | Quartz.NET para jobs agendados, IHostedService para loops contínuos |

### 📊 Tracking

| # | Documento | Progresso |
|---|----------|----------|
| 19 | [`REFACTOR_PLAN.md`](REFACTOR_PLAN.md) | 11/12 concluído (resta cobertura de testes) |

### 📖 Referência externa

| # | Documento | Conteúdo |
|---|----------|----------|
| 20 | [`MESHCENTRAL_PLAYBOOK.md`](MESHCENTRAL_PLAYBOOK.md) | Análise da integração TacticalRMM ↔ MeshCentral (6 camadas) |

### 🛠️ Assets

| Diretório | Conteúdo |
|-----------|----------|
| [`Meshcentral/`](Meshcentral/) | `meshctrl.js`, `package.json` — utilitários de manutenção MeshCentral |

---

## 🗑️ Documentos removidos nesta reorganização

| Arquivo removido | Motivo |
|-----------------|--------|
| `MESHCENTRAL_INTEGRATION_PLAN.md` | Substituído pelo `MESHCENTRAL_ROADMAP.md` (V2) |
| `PLANO_ANALISE_CHAMADOS.md` | Análise pré-implementação — tudo já executado em `IMPLEMENTACAO_CHAMADOS_TASKS.md` |
| `IMPLEMENTACAO_CHAMADOS_TASKS.md` | Checklist 100% concluído (Fases 0-3) — evidência no código |
| `RevisaoProjeto.md` | Substituído por `REFACTOR_PLAN.md` + planos específicos |

---

## 🏷️ Convenções

- **Capability docs** descrevem o estado atual implementado (endpoints, fluxos, configuração).
- **Planos** (`*_PLAN.md`, `*_ROADMAP.md`, `*_BACKLOG.md`) descrevem o que ainda falta.
- **ADRs** registram decisões de arquitetura.
- Arquivos com status ✅ no conteúdo = feature completamente implementada.
- Arquivos com status ⚠️ = parcialmente implementado.
