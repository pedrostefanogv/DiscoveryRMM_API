# Plano de Analise e Evolucao do Sistema de Chamados

Data: 2026-04-17
Status: analise para revisao, sem implementacao

## Objetivo

Consolidar uma avaliacao tecnica e funcional do sistema atual de suporte, tickets e chamados, identificar gargalos reais no backend existente e propor um plano de evolucao por fases antes de qualquer implementacao.

## Resumo executivo

O sistema atual ja passou do nivel de CRUD basico. O backend possui uma base relevante de service desk: tickets, workflow configuravel, departamentos, perfis de workflow, SLA, auditoria, anexos, knowledge base com embeddings, chat IA, vinculo ticket-KB, regras de alerta por estado e integracao de suporte remoto via MeshCentral.

O problema principal nao e ausencia total de funcionalidade. O problema principal e falta de coesao entre blocos que ja existem, mais algumas inconsistencias funcionais no fluxo atual. Hoje o produto parece mais um conjunto de capacidades parcialmente conectadas do que uma experiencia de suporte realmente madura.

Recomendacao central: nao priorizar novas features de IA ou omnichannel como primeira etapa. O maior ROI imediato esta em estabilizar o fluxo de ticket atual, completar a rastreabilidade, enriquecer operacao e so depois expandir IA, automacao e suporte remoto orientado a ticket.

## O que existe hoje no backend

### Dominio de tickets

- Entidade de ticket com vinculo a client, site, agent, department, workflow profile, prioridade, categoria, atribuicao, SLA, rating e soft delete.
- Comentarios de ticket.
- Anexos de ticket via upload presigned em object storage.
- Auditoria de atividades do ticket.
- Regras de alerta automatico por transicao de workflow.

Arquivos base analisados:
- src/Discovery.Core/Entities/Ticket.cs
- src/Discovery.Core/Entities/TicketComment.cs
- src/Discovery.Core/Entities/TicketActivityLog.cs
- src/Discovery.Core/Entities/TicketAlertRule.cs
- src/Discovery.Infrastructure/Repositories/TicketRepository.cs
- src/Discovery.Api/Controllers/TicketsController.cs
- src/Discovery.Api/Controllers/TicketAuditController.cs
- src/Discovery.Api/Controllers/TicketAlertRulesController.cs

### Workflow e organizacao

- Estados e transicoes de workflow por cliente.
- Departamentos globais e por cliente.
- Perfis de workflow com SLA em horas e prioridade padrao.

Arquivos base analisados:
- src/Discovery.Api/Controllers/WorkflowController.cs
- src/Discovery.Api/Controllers/WorkflowProfilesController.cs
- src/Discovery.Api/Controllers/DepartmentsController.cs
- src/Discovery.Core/Entities/WorkflowProfile.cs
- src/Discovery.Core/Entities/Department.cs
- src/Discovery.Migrations/Migrations/M008_CreateWorkflowAndUpdateTickets.cs

### SLA

- Calculo basico de vencimento por soma direta de horas.
- Endpoint de consulta de status e detalhes.
- Background service que monitora tickets abertos com SLA e registra warning/breach.

Arquivos base analisados:
- src/Discovery.Infrastructure/Services/SlaService.cs
- src/Discovery.Api/Controllers/TicketSlaController.cs
- src/Discovery.Api/Services/SlaMonitoringBackgroundService.cs

### Knowledge base e IA

- Knowledge base com artigos, chunks, embeddings e busca semantica.
- Vinculo explicito entre ticket e artigo.
- Sugestao de artigos para ticket.
- Chat IA do agente com contexto do equipamento e RAG da KB.
- Tool call atual da IA exposto apenas para knowledge_search.

Arquivos base analisados:
- src/Discovery.Api/Controllers/KnowledgeController.cs
- src/Discovery.Infrastructure/Repositories/KnowledgeArticleRepository.cs
- src/Discovery.Infrastructure/Services/KnowledgeChunkingService.cs
- src/Discovery.Infrastructure/Services/AiChatService.cs
- src/Discovery.Core/Entities/TicketKnowledgeLink.cs
- src/Discovery.Migrations/Migrations/M053_CreateKnowledgeBase.cs
- src/Discovery.Migrations/Migrations/M044_CreateAiChatTables.cs

### Fluxo do agente

- O agente autenticado pode listar seus tickets, abrir ticket, comentar, mudar workflow state e fechar ticket com rating.
- O agente tambem possui endpoints de suporte remoto MeshCentral.

Arquivos base analisados:
- src/Discovery.Api/Controllers/AgentAuthController.cs
- docs/MESHCENTRAL.md

### Notificacoes e realtime

- Existe infraestrutura generica de notificacoes com SignalR e persistencia.
- Tickets publicam eventos de dashboard via messaging.
- Nao foi identificado uso explicito do servico generico de notificacoes pelo modulo de tickets.

Arquivos base analisados:
- src/Discovery.Api/Services/NotificationService.cs
- src/Discovery.Api/Controllers/NotificationsController.cs
- src/Discovery.Api/Hubs/NotificationHub.cs
- src/Discovery.Api/Controllers/RealtimeController.cs

## Achados principais

### 1. O modulo ja e relativamente rico, mas esta subintegrado

Ha base suficiente para posicionar o produto como service desk tecnico integrado ao ecossistema do agente. O que falta nao e modelagem minima; o que falta e conectar melhor tickets, SLA, KB, IA, notificacoes, automacao e suporte remoto em um fluxo unico.

### 2. Existem inconsistencias funcionais importantes no fluxo atual

#### 2.1 Transicao para estado final pode nao persistir fechamento real do ticket

Nos fluxos de mudanca de workflow state do admin e do agente, o controller seta ClosedAt em memoria quando o novo estado e final, mas persiste apenas workflow_state_id via UpdateWorkflowStateAsync. Isso indica risco de ticket em estado final continuar com ClosedAt nulo, afetando consulta de abertos, SLA e metricas.

Arquivos relacionados:
- src/Discovery.Api/Controllers/TicketsController.cs
- src/Discovery.Api/Controllers/AgentAuthController.cs
- src/Discovery.Infrastructure/Repositories/TicketRepository.cs

#### 2.2 WorkflowProfile resolvido por departamento nao e persistido no ticket

Na criacao de ticket, quando o profile padrao e encontrado pelo departamento, o SLA e calculado com base nele, mas o ticket e salvo com WorkflowProfileId vindo apenas do request. Na pratica, o comportamento e aplicado sem o vinculo ficar registrado de forma consistente.

Arquivos relacionados:
- src/Discovery.Api/Controllers/TicketsController.cs
- src/Discovery.Api/Controllers/AgentAuthController.cs

#### 2.3 Auditoria de comentarios esta incompleta

O modulo de auditoria e estatisticas considera TicketActivityType.Commented, mas a inclusao de comentario nao registra essa atividade. Isso reduz rastreabilidade e torna as metricas de timeline incorretas.

Arquivos relacionados:
- src/Discovery.Api/Controllers/TicketAuditController.cs
- src/Discovery.Infrastructure/Services/ActivityLogService.cs
- src/Discovery.Infrastructure/Repositories/TicketRepository.cs

#### 2.4 Semantica de SLA esta inconsistente entre endpoints

Um endpoint usa breach calculado dinamicamente; outro exibe ticket.SlaBreached persistido. Como o flag e atualizado pelo background service em janela periodica, o frontend pode receber respostas divergentes para o mesmo ticket.

Arquivos relacionados:
- src/Discovery.Api/Controllers/TicketSlaController.cs
- src/Discovery.Infrastructure/Services/SlaService.cs
- src/Discovery.Api/Services/SlaMonitoringBackgroundService.cs

### 3. O modelo de ticket ainda e operacionalmente raso

Hoje o sistema resolve abertura, atualizacao, comentarios, anexos e fechamento, mas faltam capacidades que tornam o uso eficiente no dia a dia:

- filtros ricos por assignee, department, profile, prioridade, breach, aging e texto livre;
- filas operacionais e saved views;
- ordenacao e paginacao mais flexiveis;
- validacao de assignee e ownership;
- resposta enriquecida com workflow, department, profile e indicadores prontos para UI;
- timeline unica com comentarios, mudancas, KB, alertas, automacoes e sessoes remotas.

### 4. A IA atual ajuda mais como copiloto tecnico do que como copiloto de chamados

O chat IA do agente tem boa base para contexto tecnico e RAG, mas o tool loop atual esta concentrado em knowledge_search. Nao ha sinais de IA atuando de forma estruturada sobre o ciclo do ticket, por exemplo:

- classificar categoria e prioridade;
- sugerir department/profile;
- gerar titulo e resumo melhores;
- sugerir workflow transition segura;
- resumir historico do ticket;
- propor resposta ao usuario;
- recomendar artigo e ja vincular ao ticket com explicacao;
- sugerir automacao ou sessao remota a partir do contexto.

### 5. SLA e workflow existem, mas ainda nao operam no nivel de service desk maduro

O desenho atual suporta apenas um vencimento simples por horas corridas. Isso e util para primeira versao, mas insuficiente para operacao mais robusta:

- nao ha separacao entre first response SLA e resolution SLA;
- nao ha calendario util, expediente ou feriado;
- nao ha pausa de SLA em estados como Waiting on Client;
- nao ha escalonamento automatico por breach iminente ou definitivo;
- nao ha politicas por impacto x urgencia;
- nao ha ownership de fila ou balanceamento de carga.

### 6. A base de conhecimento ja e forte e pode virar vantagem competitiva

O sistema ja tem componentes de KB, embeddings, busca semantica e vinculo ticket-artigo. Isso abre caminho para uma operacao de suporte muito mais eficiente, mas hoje o ciclo ainda parece parcial:

- a KB pode ser sugerida e vinculada, mas nao ha fluxo claro de "ticket resolvido -> candidato a artigo";
- nao ha ranking de artigos por eficacia em resolucao;
- nao ha feedback sobre sugestao util ou nao util;
- nao ha trilha de aprendizagem da IA sobre resolucoes bem sucedidas.

### 7. Notificacao e suporte remoto estao disponiveis, mas nao amarrados ao ticket

Existe infraestrutura generica de notificacao e tambem suporte remoto via MeshCentral. Isso agrega muito valor se o ticket virar o eixo da operacao, por exemplo:

- criar notificacoes de atribuicao, comentario, SLA warning e escalacao;
- iniciar suporte remoto a partir do ticket;
- gravar URL, horario e operador da sessao remota no historico do ticket;
- relacionar comandos, scripts e alertas ao ticket.

Hoje nao ha evidencia de que esse encadeamento esteja completo.

### 8. Ha sinais de divida tecnica e cobertura insuficiente

- Nao foram encontrados testes de ticket em src/Discovery.Tests.
- Existe enum legado TicketStatus mesmo apos migracao para workflow_state_id.
- Ha tipos de atividade previstos no enum que ainda nao aparecem operacionalizados no fluxo real, como Reopened, Escalated, DescriptionUpdated e CategoryChanged.
- O rollout de autorizacao por escopo no projeto ainda esta em refinamento, o que pode afetar governanca do modulo de tickets.

## O que pode ser melhorado ou otimizado

## Prioridade maxima: corrigir o modulo atual antes de expandir muito

1. Corrigir persistencia de ClosedAt nas transicoes finais.
2. Persistir WorkflowProfileId resolvido automaticamente.
3. Completar auditoria real de comentarios, descricao, categoria, department change, reopen e escalacao.
4. Unificar semantica de SLA para nao depender de leitura divergente entre flag persistido e calculo on-demand.
5. Criar cobertura de testes para workflow, SLA, comentarios, fechamento e rating.

## Melhorias de operacao com alto ROI

1. Listagem de tickets com filtros ricos e query unica de fila.
2. Saved views por time, tecnico, cliente e severidade.
3. Enriquecimento do payload de ticket com nomes de estado, department, profile, SLA status e aging.
4. Validacao de assignee e ownership por escopo.
5. Timeline unica do ticket com eventos de negocio e tecnicos.
6. Notificacoes reais por atribuicao, comentario, SLA warning, breach e fechamento.
7. Melhor busca textual em ticket por titulo, descricao, comentario e artigo vinculado.

## Funcoes novas para agregar valor

### Intake e triagem inteligente

- Auto-preenchimento de categoria, prioridade e department via IA.
- Sugestao de workflow profile com score de confianca.
- Geracao de titulo e resumo tecnico melhores a partir da descricao bruta.
- Identificacao de tickets duplicados ou altamente similares.
- Detecao de contexto do agente para preencher automaticamente impacto tecnico.

### Operacao e colaboracao

- Watchers, mentions e seguidores do ticket.
- Comentarios com autor estruturado, tipo de origem e anexos por comentario.
- Macros de resposta e templates de comentario.
- Parent/child tickets, merge e split.
- Campos customizaveis para ticket aproveitando a infraestrutura de custom fields ja existente no projeto.

### SLA e governanca

- First response SLA e resolution SLA separados.
- Calendario util por cliente/site/department.
- Pausa automatica de SLA em estados de espera.
- Escalonamento automatico para supervisor, fila ou outro departamento.
- Matriz impacto x urgencia gerando prioridade automaticamente.

### IA aplicada diretamente ao ciclo do chamado

- Resumo automatico de historico longo.
- Sugestao de proximos passos.
- Draft de resposta para tecnico ou usuario.
- Sugestao de artigo com explicacao do por que foi recomendado.
- Sugestao de fechar, reabrir, escalar ou pedir mais informacoes, sempre com aprovacao humana.
- Tool calls especificos para ticket, por exemplo buscar ticket, criar rascunho, comentar, sugerir fechamento, vincular KB e iniciar checklist.

### KB e resolucao assistida

- Fluxo "resolvido -> gerar rascunho de artigo".
- Ranking de artigos por taxa de sucesso em tickets similares.
- Feedback util/nao util das sugestoes da KB.
- Vinculo automatico entre categoria de ticket e playbooks/artigos recomendados.

### Suporte remoto e automacao orientados a ticket

- Iniciar sessao MeshCentral a partir do ticket.
- Registrar no ticket qual sessao remota foi aberta e por quem.
- Associar automacoes/scripts aprovados ao ticket.
- Criar ticket automaticamente a partir de alerta tecnico relevante.
- Encadear workflow state com PSADT alerts, suporte remoto e automacao.

### Relatorios e gestao

- Dashboards por fila, assignee, cliente e department.
- Aging, backlog, MTTA, MTTR, FRT, breach rate, reopen rate.
- Taxa de resolucao com KB, automacao e suporte remoto.
- CSAT real do usuario, nao apenas rating operacional do agente.

## Roadmap recomendado

### Fase 0 - Estabilizacao e corretude do fluxo atual

Objetivo: tornar o modulo atual confiavel antes de expandir escopo.

Entregas recomendadas:
- corrigir persistencia de ClosedAt em transicao final;
- persistir WorkflowProfileId efetivo;
- completar auditoria de eventos realmente ocorridos;
- alinhar contrato e calculo de SLA;
- remover ou isolar contratos legados sem uso claro, como TicketStatus;
- criar testes de ticket e SLA.

Resultado esperado:
- dados de ticket consistentes;
- metricas confiaveis;
- base segura para evoluir UX e IA.

### Fase 1 - Operacao, fila e experiencia do tecnico

Objetivo: fazer o sistema funcionar bem para quem opera chamados o dia inteiro.

Entregas recomendadas:
- endpoint de busca/listagem rica de tickets;
- saved views e filtros operacionais;
- payload enriquecido do ticket;
- notificacoes reais do modulo de tickets;
- timeline consolidada;
- validacoes de ownership e atribuicao.

Resultado esperado:
- menor friccao operacional;
- menos chamadas extras do frontend;
- melhor tempo de resposta do time.

### Fase 2 - IA aplicada a triagem e resolucao

Objetivo: sair de chat tecnico generico e transformar IA em assistente de service desk.

Entregas recomendadas:
- intake inteligente para novos tickets;
- artigos sugeridos e explicados;
- resumo automatico do ticket;
- draft de resposta;
- tool calls especificos para operacoes seguras de ticket com aprovacao humana.

Resultado esperado:
- triagem mais rapida;
- menor tempo ate resposta util;
- maior aproveitamento da KB.

### Fase 3 - SLA maduro, escalacao e governanca

Objetivo: profissionalizar o service desk para escala maior e contratos mais exigentes.

Entregas recomendadas:
- first response SLA e resolution SLA;
- calendario util e pausa por estado;
- escalacao automatica por regra;
- ownership por fila/time;
- dashboards e KPIs operacionais.

Resultado esperado:
- governanca real de atendimento;
- menos breach evitavel;
- visibilidade de capacidade e gargalo.

### Fase 4 - Orquestracao com suporte remoto, automacao e aprendizagem

Objetivo: transformar o ticket no hub central da operacao tecnica.

Entregas recomendadas:
- vinculo ticket -> sessao remota MeshCentral;
- vinculo ticket -> automacao/script com aprovacao;
- criacao automatica de ticket a partir de eventos relevantes;
- geracao de artigo a partir de resolucao bem sucedida;
- relatorios executivos e tecnicos do ciclo completo.

Resultado esperado:
- ganho forte de produtividade;
- rastreabilidade ponta a ponta;
- melhor aprendizado operacional do sistema.

## Ordem de prioridade sugerida

### Fazer primeiro

1. Corretude do workflow e SLA.
2. Auditoria e testes.
3. Busca/listagem/fila/notificacao.

### Fazer depois

1. IA de triagem e resolucao.
2. SLA avancado.
3. Orquestracao com suporte remoto e automacao.

### Nao priorizar agora

1. Omnichannel completo sem antes estabilizar o nucleo de ticket.
2. IA com autonomia para mudar estado ou fechar ticket sem aprovacao.
3. Portal externo mais sofisticado antes da fila interna estar madura.

## KPIs recomendados para medir evolucao

- MTTA
- MTTR
- FRT (first response time)
- taxa de breach de SLA
- backlog aging por fila
- taxa de reabertura
- taxa de resolucao com artigo sugerido
- taxa de uso efetivo das sugestoes da IA
- CSAT
- tempo medio para triagem

## Dependencias e decisoes de produto que valem alinhar antes de implementar

1. Qual e o modelo oficial de ownership do ticket: usuario, fila, department ou combinado?
2. O agente pode fechar qualquer ticket proprio ou apenas alguns tipos/estados?
3. Ate onde a IA pode agir automaticamente e onde sempre precisa de aprovacao humana?
4. O rating atual do agente representa satisfacao do usuario final ou apenas feedback tecnico da acao?
5. A prioridade sera manual, por matriz impacto x urgencia, por IA ou hibrida?
6. O sistema vai evoluir para portal do usuario final, apenas operacao interna ou ambos?

## Recomendacao final

O sistema ja tem base suficiente para se tornar uma das areas mais fortes do produto, porque combina ticket, contexto do agente, KB vetorial, IA, object storage, automacao e suporte remoto. O maior ganho agora nao esta em adicionar mais blocos isolados. O maior ganho esta em fechar as lacunas de corretude, conectar melhor as capacidades existentes e transformar o ticket em eixo central da operacao tecnica.

Se a revisao deste plano for aprovada, a melhor sequencia pratica e iniciar por Fase 0 e Fase 1 antes de qualquer expansao mais agressiva de IA.