## Plan: Custom Fields multi-escopo para Discovery

Implementar um modulo backend/API de campos personalizados com definicao central no servidor, escopo por entidade (Server, Client, Site, Agent), coleta opcional por agent, e leitura controlada em runtime para automacao. A proposta reutiliza o fluxo ja existente de autenticacao de agent, execucao de tarefas/scripts e permissoes por escopo para minimizar risco e manter consistencia.

**Status atual**
- Fases 1 a 3 implementadas no backend, incluindo validacao tipada, regras de escopo e politica de acesso em runtime.
- Fase 4 implementada: CRUD administrativo de definicoes, endpoints de valores por escopo e schema efetivo em `/api/custom-fields/schema/{scopeType}`.
- Fase 5 implementada no backend: `agent-auth` ja expoe leitura runtime por contexto (`TaskId`/`ScriptId`) e upsert controlado de valores coletados pelo agent.
- Fase 6 esta parcialmente adiantada: o backend ja possui allowlist por tarefa/script via `CustomFieldExecutionAccess`; ainda faltam auditoria dedicada de acesso e fechamento do contrato operacional com automacao conforme necessario.
- Fase 8 avancou com cobertura de servico para schema, runtime access e escrita autorizada/bloqueada por agent.

**Steps**
1. Fase 1 - Modelagem de dominio e contratos
1. Definir modelo de definicao de campo com: nome tecnico, label, descricao, escopo alvo, tipo, validacoes avancadas (regex, min/max, obrigatorio, lista fixa), status ativo e auditoria. *base para todas as fases*
1. Definir modelo de valor de campo por entidade alvo (Server/Client/Site/Agent), incluindo serializacao consistente para tipos heterogeneos e controle de versao de atualizacao. *depends on 1*
1. Definir politica de acesso por campo com dois eixos: leitura em runtime e escrita por agent, com modo de exposicao: Publico, RestritoPorTarefaScript, Desabilitado. *depends on 1*

1. Fase 2 - Persistencia e migracoes
1. Criar migracoes para tabelas de definicoes, valores e vinculos de acesso por tarefa/script (read/write allowlist), com indices por escopo e entidade alvo. *depends on Fase 1*
1. Implementar repositorios e mapeamentos no DbContext para CRUD de definicoes, valores e politicas de acesso, seguindo padrao atual de repositorios EF. *parallel with validacoes de servico da Fase 3, apos migracao pronta*

1. Fase 3 - Regras de negocio e validacao
1. Implementar servico de custom fields com validacao por tipo (int, texto, decimal, bool, date/datetime, dropdown/listbox), validacoes avancadas (regex, min/max, obrigatorio, tamanho), e coercao de payload de entrada. *depends on Fase 1 e 2*
1. Implementar resolucao de acesso para runtime com precedencia: campo ativo + escopo aplicavel + modo exposicao + vinculo opcional com tarefa/script + permissoes de actor (agent ou usuario). *depends on step anterior*
1. Definir regra explicita de escrita por agent: somente campos de escopo Agent com allowAgentWrite habilitado e, quando RestritoPorTarefaScript, apenas dentro de execucao vinculada. *depends on step anterior*

1. Fase 4 - APIs administrativas (web/backoffice)
1. Expor endpoints administrativos para CRUD de definicoes de campos no nivel servidor (fonte unica), incluindo tipos e regras de validacao. *depends on Fase 3*
1. Expor endpoints para leitura/edicao de valores em Client, Site e Agent, com validacao de escopo e permissao de usuario. *depends on step anterior*
1. Expor endpoint para listar schema efetivo por escopo, para o frontend externo montar formularios dinamicos sem hardcode. *parallel with step anterior*

1. Fase 5 - APIs de runtime para agents/scripts
1. Adicionar endpoints em agent-auth para leitura de custom fields disponiveis em runtime (inclui Server/Client/Site/Agent conforme habilitacao), com filtros por contexto de execucao (TaskId/ScriptId). *depends on Fase 3 e 4*
1. Adicionar endpoint em agent-auth para upsert de valores coletados pelo agent, restrito a escopo Agent e allowAgentWrite explicito. *depends on step anterior*
1. Integrar retorno dos custom fields no fluxo de configuracao efetiva do agent quando fizer sentido para cache local controlado. *optional, depends on step anterior*

1. Fase 6 - Integracao com automacao
1. Estender definicao de tarefa/script para permitir vinculo explicito de campos permitidos para leitura e escrita em runtime (allowlist por execucao). *depends on Fase 5*
1. Integrar no disparo/execucao de automacao a resolucao de campos autorizados e registrar trilha de auditoria de acesso. *depends on step anterior*
1. Definir contrato de payload para scripts consumirem valores (ex.: mapa de chaves por nome tecnico) sem expor campos fora da politica. *depends on step anterior*

1. Fase 7 - Observabilidade, seguranca e rollout
1. Registrar auditoria para criacao/edicao de definicoes, alteracoes de valor e operacoes de leitura/escrita em runtime por agent/script. *parallel with testes finais*
1. Aplicar controles anti abuso: limites de tamanho de valor, rate limit de escrita por agent, validacao de owner scope, e rejeicao de campo inativo. *depends on Fase 5*
1. Publicar versao em modo incremental com feature flag para endpoints runtime se necessario. *depends on testes de Fase 8*

1. Fase 8 - Testes e validacao
1. Criar testes unitarios de validacao por tipo e politica de acesso (publico/restrito/desabilitado, allowAgentWrite, allowRuntimeRead). *depends on Fase 3*
1. Criar testes de integracao API para CRUD de definicoes e valores por escopo. *depends on Fase 4*
1. Criar testes de autenticacao de agent e runtime access em agent-auth para leitura/escrita autorizada e bloqueios esperados. *depends on Fase 5*
1. Criar teste E2E de cenario TeamViewer ID: campo Agent + script de coleta + persistencia + consulta na tela (via API de leitura). *depends on Fase 6*

**Decisions**
- Escopo desta entrega: backend/API apenas; frontend esta em repositorio externo.
- Escrita por agent: somente com allowAgentWrite explicito.
- Leitura em runtime: pode incluir todos os escopos (Server, Client, Site, Agent), condicionado a politica do campo.
- Politica de exposicao adotada: Publico, RestritoPorTarefaScript, Desabilitado.
- Validacoes avancadas por tipo entram ja na primeira entrega.
