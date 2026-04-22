# Implementacao de Chamados - Quadro de Tarefas

Data de inicio: 2026-04-17
Responsavel: Copilot

## Status geral

- Fase ativa: Fase 3 concluída
- Progresso Fase 0: 9/9 concluídas
- Progresso Fase 1: 4/4 concluídas
- Progresso Fase 2: 3/3 concluídas
- Progresso Fase 3: 4/4 concluídas

## Fase 0 - Corretude (em execucao)

### Concluido

- [x] F0.1 Criar quadro de tarefas e trilha de execucao
- [x] F0.2 Persistir ClosedAt corretamente em transicoes de workflow (admin)
- [x] F0.3 Persistir ClosedAt corretamente em transicoes de workflow (agente)
- [x] F0.4 Persistir WorkflowProfileId efetivo no create de ticket (admin)
- [x] F0.5 Persistir WorkflowProfileId efetivo no create de ticket (agente)
- [x] F0.6 Registrar TicketActivityType.Commented ao comentar ticket (admin)
- [x] F0.7 Registrar TicketActivityType.Commented ao comentar ticket (agente)
- [x] F0.8 Registrar TicketActivityType.Commented ao fechar com comentario (agente)
- [x] F0.9 Validar compilacao/erros nas alteracoes da Fase 0

### Em andamento

- [ ] Nenhuma no momento

### Proximo alvo

- Iniciar Fase 1: operacao de fila, filtros ricos e notificacoes do modulo de tickets.

## Backlog imediato (apos Fase 0)

### Fase 1 - Operacao

- [x] F1.1 Busca/listagem rica de tickets (filtros: clientId, siteId, agentId, departmentId, workflowProfileId, workflowStateId, assignedToUserId, priority, slaBreached, isClosed, text ILike, limit/offset) — `TicketFilterQuery`, `ITicketRepository`, `TicketRepository`, `TicketsController`
- [x] F1.2 Saved views de fila — entidade `TicketSavedView`, `ITicketSavedViewRepository`, `TicketSavedViewRepository`, migração M098, `TicketSavedViewsController` (CRUD + suporte compartilhamento)
- [x] F1.3 Notificações do módulo de tickets — `TicketsController` (criação com assignee, atribuição, mudança de estado, comentário público) + `SlaMonitoringBackgroundService` (sla_warning, sla_breached) via `INotificationService`
- [x] F1.4 Timeline unificada de eventos do ticket — endpoint `GET /api/tickets/{id}/audit/timeline/unified` mescla atividades + comentários em ordem cronológica

### Fase 2 - IA aplicada ao ticket

- [x] F2.1 Triagem automática — `POST /api/tickets/{id}/ai/triage` sugere categoria, prioridade e departamento via LLM (JSON estruturado)
- [x] F2.2 Resumo executivo — `POST /api/tickets/{id}/ai/summarize` gera resumo do problema + ações + situação atual com base em comentários
- [x] F2.3 Sugestão de resposta — `POST /api/tickets/{id}/ai/suggest-reply` gera próxima mensagem profissional com base no histórico

### Fase 3 - SLA maduro, escalação e governança

- [x] F3.1 First response SLA — `WorkflowProfile.FirstResponseSlaHours` + `Ticket.SlaFirstResponseExpiresAt` + `Ticket.FirstRespondedAt` (migração M099); `ISlaService.CalculateFirstResponseExpiryAsync` + `GetFrtStatusAsync`; `TicketSlaController.details` retorna `firstResponseSla`
- [x] F3.2 Pausa de SLA por estado — `WorkflowState.PausesSla` (migração M100); `Ticket.SlaPausedSeconds` + `Ticket.SlaHoldStartedAt`; `SlaService.GetEffectiveSlaExpiry` desconta tempo pausado; `TicketsController` pausa/retoma hold na mudança de estado
- [x] F3.3 Escalação automática — `TicketEscalationRule` entity + migração M101 + `ITicketEscalationRuleRepository` + `EscalationRulesController` (CRUD); `SlaMonitoringBackgroundService` processa regras ativas, bumpa prioridade e notifica
- [x] F3.4 KPIs operacionais — `GET /api/tickets/kpi` (filtros: clientId, departmentId, since) retorna: totalOpen/Closed, slaBreached, slaWarning, onHold, frtAchievementRate, avgResolutionHours, avgAgeOpenHours, byAssignee, byDepartment

## Notas de execucao

- Este documento sera atualizado a cada entrega parcial para refletir concluido, em andamento e proximo passo.