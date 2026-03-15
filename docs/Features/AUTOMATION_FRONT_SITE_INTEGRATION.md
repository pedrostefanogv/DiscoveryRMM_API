# Automation Front Site Integration

## Objetivo

Este documento descreve a API administrativa de automacao consumida pelo front/site.

Escopo deste documento:

- cadastro e consulta de scripts
- cadastro e consulta de tarefas de automacao
- auditoria funcional
- disparos operacionais imediatos em agents especificos
- consulta de historico de execucao
- validacoes aplicadas pelo backend

Este documento nao inclui exemplos de payload. O foco esta em contrato, finalidade e regras de uso.

## Conceitos Basicos

### Script

Script e um artefato reutilizavel.

Serve para:

- armazenar conteudo executavel versionado
- descrever metadados e parametros
- ser referenciado por tarefas do tipo RunScript
- ser consumido diretamente quando necessario

### Tarefa de automacao

Tarefa e a regra operacional que define o que deve acontecer, onde deve acontecer e em quais gatilhos.

Serve para:

- instalar pacote
- atualizar pacote
- executar script previamente cadastrado
- executar comando customizado

### Escopo

O backend trabalha com estes niveis de escopo:

- Global
- Client
- Site
- Agent

Regras:

- ScopeType define o nivel do cadastro
- ScopeId e exigido para Client, Site e Agent
- ScopeId nao deve ser enviado para Global
- a resolucao de aplicabilidade para o agent e feita no backend durante o policy sync

### Tags

As listas IncludeTags e ExcludeTags refinam a aplicabilidade.

Regras:

- IncludeTags: se preenchida, o agent precisa ter pelo menos uma tag correspondente
- ExcludeTags: se qualquer tag casar, a tarefa nao se aplica
- o backend normaliza tags removendo vazios, espacos laterais e duplicidades sem diferenciar maiusculas de minusculas

## Correlation Id

Os controllers de scripts e tarefas aceitam o header X-Correlation-Id.

Comportamento:

- se o front enviar esse header, o valor e propagado para log e auditoria
- se nao enviar, o backend gera um valor automaticamente
- o mesmo valor retorna no header de resposta

Uso recomendado:

- gerar um correlation id por acao administrativa relevante, como criacao, alteracao, exclusao e disparo operacional

## API de Scripts

Base: /api/automation/scripts

### GET /api/automation/scripts

Finalidade:

- listar scripts com filtros administrativos

Query params:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| clientId | guid nullable | nao | filtra scripts vinculados a um client especifico |
| activeOnly | bool | nao | quando true, retorna apenas scripts ativos |
| limit | int | nao | paginacao. O backend normaliza para faixa entre 1 e 200 |
| offset | int | nao | deslocamento paginado. Valores negativos viram 0 |

Resposta:

- AutomationScriptPageDto com Items, Count, Total, Limit e Offset

Campos resumidos por item:

- Id
- ClientId
- Name
- Summary
- ScriptType
- Version
- ExecutionFrequency
- TriggerModes
- IsActive
- LastUpdatedAt
- CreatedAt

### GET /api/automation/scripts/{id}

Finalidade:

- obter detalhes completos de um script

Resposta:

- AutomationScriptDetailDto

Campos adicionais em relacao ao resumo:

- Content
- ContentHashSha256
- ParametersSchemaJson
- MetadataJson
- UpdatedAt

### POST /api/automation/scripts

Finalidade:

- criar um novo script reutilizavel

Corpo:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| ClientId | guid nullable | nao | escopo opcional do script por client |
| Name | string | sim | nome administrativo do script |
| Summary | string | sim | resumo funcional do script |
| ScriptType | enum | sim | tipo de executor esperado |
| Version | string | nao | default 1.0.0 quando vazio |
| ExecutionFrequency | string | nao | default manual quando vazio |
| TriggerModes | lista de string | sim | modos declarativos de disparo do script |
| Content | string | sim | conteudo bruto executavel |
| ParametersSchemaJson | string nullable | nao | definicao textual de parametros |
| MetadataJson | string nullable | nao | metadados adicionais |
| IsActive | bool | nao | default true |

Validacoes aplicadas pelo backend:

- Name obrigatorio
- Name com no maximo 200 caracteres
- Summary obrigatorio
- Summary com no maximo 2000 caracteres
- Content obrigatorio
- Content com no maximo 200000 caracteres
- TriggerModes precisa conter ao menos um item

Observacoes:

- TriggerModes e normalizado, com remocao de vazios e duplicados
- o hash SHA-256 do conteudo nao precisa ser enviado pelo front; ele e calculado pelo backend
- criacao gera auditoria e log estruturado

### PUT /api/automation/scripts/{id}

Finalidade:

- atualizar um script existente, inclusive ativacao e desativacao

Corpo:

- mesmo contrato de criacao, acrescido de Reason opcional

Campos relevantes adicionais:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| Reason | string nullable | nao | justificativa administrativa para auditoria |

Validacoes:

- mesmas regras do POST

Comportamento:

- se IsActive mudar, a auditoria registra Activated ou Deactivated
- se IsActive permanecer igual, a auditoria registra Updated

### DELETE /api/automation/scripts/{id}

Finalidade:

- excluir um script do catalogo

Comportamento:

- gera auditoria com snapshot anterior e motivo opcional
- gera log estruturado de exclusao

### GET /api/automation/scripts/{id}/consume

Finalidade:

- obter payload consumivel do script para execucao ou distribuicao

Resposta:

- AutomationScriptConsumeDto

Campos principais:

- ScriptId
- Name
- Version
- ScriptType
- ExecutionFrequency
- TriggerModes
- Summary
- LastUpdatedAt
- Content
- ContentHashSha256
- ParametersSchemaJson
- MetadataJson

Comportamento:

- somente scripts ativos sao retornados
- o acesso gera entrada de auditoria do tipo Consumed
- o acesso gera log operacional

### GET /api/automation/scripts/{id}/audit

Finalidade:

- consultar trilha de auditoria do script

Query params:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| limit | int | nao | quantidade maxima de eventos de auditoria |

Resposta:

- lista de AutomationScriptAuditDto

Campos por evento:

- Id
- ScriptId
- ChangeType
- Reason
- OldValueJson
- NewValueJson
- ChangedBy
- IpAddress
- CorrelationId
- ChangedAt

## API de Tarefas de Automacao

Base: /api/automation/tasks

### GET /api/automation/tasks

Finalidade:

- listar tarefas de automacao por escopo e status

Query params:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| scopeType | enum nullable | nao | filtra por Global, Client, Site ou Agent |
| scopeId | guid nullable | nao | filtra pelo identificador do escopo |
| activeOnly | bool | nao | quando true, retorna apenas tarefas ativas |
| limit | int | nao | paginacao. O backend normaliza para faixa entre 1 e 200 |
| offset | int | nao | deslocamento paginado. Valores negativos viram 0 |

Resposta:

- AutomationTaskPageDto com Items, Count, Total, Limit e Offset

Campos resumidos por item:

- Id
- Name
- Description
- ActionType
- ScopeType
- ScopeId
- IsActive
- RequiresApproval
- LastUpdatedAt

### GET /api/automation/tasks/{id}

Finalidade:

- obter detalhes completos de uma tarefa

Resposta:

- AutomationTaskDetailDto

Campos adicionais em relacao ao resumo:

- InstallationType
- PackageId
- ScriptId
- CommandPayload
- IncludeTags
- ExcludeTags
- TriggerImmediate
- TriggerRecurring
- TriggerOnUserLogin
- TriggerOnAgentCheckIn
- ScheduleCron
- CreatedAt
- UpdatedAt

### POST /api/automation/tasks

Finalidade:

- criar uma nova regra de automacao

Corpo:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| Name | string | sim | nome administrativo da tarefa |
| Description | string nullable | nao | descricao operacional |
| ActionType | enum | sim | tipo de acao da tarefa |
| InstallationType | enum nullable | condicional | obrigatorio para InstallPackage e UpdatePackage |
| PackageId | string nullable | condicional | obrigatorio para InstallPackage e UpdatePackage |
| ScriptId | guid nullable | condicional | obrigatorio para RunScript |
| CommandPayload | string nullable | condicional | obrigatorio para CustomCommand |
| ScopeType | enum | sim | Global, Client, Site ou Agent |
| ScopeId | guid nullable | condicional | obrigatorio para Client, Site e Agent |
| IncludeTags | lista de string | nao | tags de habilitacao |
| ExcludeTags | lista de string | nao | tags de bloqueio |
| TriggerImmediate | bool | nao | disparo imediato |
| TriggerRecurring | bool | nao | disparo por agenda |
| TriggerOnUserLogin | bool | nao | disparo em login de usuario |
| TriggerOnAgentCheckIn | bool | nao | disparo em check-in do agent |
| ScheduleCron | string nullable | condicional | obrigatorio quando TriggerRecurring for true |
| RequiresApproval | bool | nao | sinaliza necessidade de aprovacao |
| IsActive | bool | nao | default true |

Validacoes gerais aplicadas pelo backend:

- Name obrigatorio
- Name com no maximo 200 caracteres
- pelo menos um trigger deve estar habilitado
- ScheduleCron obrigatorio quando TriggerRecurring for true

Validacoes por ActionType:

- InstallPackage: exige InstallationType e PackageId
- UpdatePackage: exige InstallationType e PackageId
- RemovePackage: exige InstallationType e PackageId
- UpdateOrInstallPackage: exige InstallationType e PackageId
- RunScript: exige ScriptId apontando para script ativo
- CustomCommand: exige CommandPayload

Validacoes por ScopeType:

- Global: nao exige ScopeId
- Client: exige ScopeId e grava o valor como client alvo
- Site: exige ScopeId e grava o valor como site alvo
- Agent: exige ScopeId e grava o valor como agent alvo

Observacoes:

- tags sao normalizadas pelo backend
- o backend registra auditoria e log estruturado na criacao

### PUT /api/automation/tasks/{id}

Finalidade:

- atualizar uma tarefa existente

Corpo:

- mesmo contrato de criacao, acrescido de Reason opcional

Campo adicional:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| Reason | string nullable | nao | justificativa administrativa da alteracao |

Validacoes:

- mesmas regras do POST

Comportamento:

- se IsActive mudar, a auditoria registra Activated ou Deactivated
- se IsActive permanecer igual, a auditoria registra Updated

### DELETE /api/automation/tasks/{id}

Finalidade:

- excluir uma tarefa de automacao

Comportamento:

- gera auditoria com snapshot anterior
- gera log estruturado

### GET /api/automation/tasks/{id}/audit

Finalidade:

- consultar trilha de auditoria da tarefa

Query params:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| limit | int | nao | quantidade maxima de eventos de auditoria |

Resposta:

- lista de AutomationTaskAuditDto

Campos por evento:

- Id
- TaskId
- ChangeType
- Reason
- OldValueJson
- NewValueJson
- ChangedBy
- IpAddress
- CorrelationId
- ChangedAt

### GET /api/automation/tasks/{id}/preview-agents

Finalidade:

- visualizar quais agents seriam alvo da tarefa considerando ScopeType, ScopeId, IncludeTags e ExcludeTags

Query params:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| limit | int | nao | paginacao. O backend normaliza para faixa entre 1 e 200 |
| offset | int | nao | deslocamento paginado. Valores negativos viram 0 |

Resposta:

- AutomationTaskTargetPreviewPageDto

Campos principais da resposta:

- TaskId
- TaskName
- ScopeType
- IncludeTags
- ExcludeTags
- Items
- Count
- Total
- Limit
- Offset

Campos por item de agent:

- AgentId
- SiteId
- Hostname
- DisplayName
- Status
- AgentTags

Comportamento:

- o backend resolve candidatos pelo escopo da tarefa
- em seguida aplica filtros de include e exclude tags
- o retorno ja representa a lista final de agents aplicaveis para a tarefa no estado atual

## Operacao Administrativa em Agent Especifico

Base: /api/agents/{id}

Esses endpoints nao alteram o cadastro estrutural da tarefa ou do script. Eles disparam operacoes para um agent especifico usando a pipeline de AgentCommand existente.

### GET /api/agents/{id}/commands

Finalidade:

- listar comandos enviados ao agent

Query params:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| limit | int | nao | quantidade maxima de comandos retornados |

### POST /api/agents/{id}/commands

Finalidade:

- enviar um comando generico ao agent

Corpo:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| CommandType | enum | sim | tipo de comando a ser entregue |
| Payload | string | sim | carga do comando |

### POST /api/agents/{id}/automation/tasks/{taskId}/run-now

Finalidade:

- disparar imediatamente uma tarefa de automacao para um agent especifico

Comportamento:

- a tarefa precisa existir e estar ativa
- o backend traduz a tarefa em AgentCommand executavel
- RunScript vira comando do tipo Script com o conteudo do script referenciado
- InstallPackage e UpdatePackage viram comando PowerShell montado para Winget ou Chocolatey
- CustomCommand vira comando PowerShell com o payload cadastrado
- apos o envio, o backend cria AutomationExecutionReport com SourceType RunNow e Status inicial Dispatched

Resposta:

- retorna o command criado e metadados da tarefa disparada

### POST /api/agents/{id}/automation/scripts/{scriptId}/run-now

Finalidade:

- disparar imediatamente um script ativo para um agent especifico, sem depender de uma tarefa cadastrada

Comportamento:

- o script precisa existir e estar ativo
- o backend cria AgentCommand do tipo Script com o conteudo do script
- o backend cria AutomationExecutionReport com SourceType RunNow

Resposta:

- retorna o command criado, o id do script, a versao e o hash do conteudo

### POST /api/agents/{id}/automation/force-sync

Finalidade:

- solicitar ao agent um ciclo tecnico de sincronizacao forcada

Corpo:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| Policies | bool | nao | default true. Solicita resincronizacao de politicas |
| Inventory | bool | nao | default false. Solicita resincronizacao de inventario |
| Software | bool | nao | default false. Solicita resincronizacao de software |
| AppStore | bool | nao | default false. Solicita resincronizacao de app store |

Comportamento:

- o backend envia um AgentCommand tecnico reaproveitando a pipeline existente
- o backend cria AutomationExecutionReport com SourceType ForceSync

### GET /api/agents/{id}/automation/executions

Finalidade:

- consultar historico de execucao de automacao por agent

Query params:

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| limit | int | nao | quantidade maxima de registros retornados |

Resposta:

- lista de AutomationExecutionReportDto

Campos por item:

- Id
- CommandId
- AgentId
- TaskId
- ScriptId
- SourceType
- Status
- CorrelationId
- CreatedAt
- AcknowledgedAt
- ResultReceivedAt
- ExitCode
- ErrorMessage
- RequestMetadataJson
- AckMetadataJson
- ResultMetadataJson

## Erros e Rejeicoes Comuns

Padroes relevantes do backend:

- 400 BadRequest quando uma regra de negocio lanca InvalidOperationException nos controllers de scripts e tarefas
- 404 NotFound quando script, tarefa ou agent nao existem no contexto esperado
- 401 Unauthorized nos endpoints agent-auth quando o agent nao esta autenticado

Erros funcionais mais provaveis no front:

- Name ausente ou acima do limite
- Summary ausente ou acima do limite em scripts
- TriggerModes vazio em scripts
- Content vazio ou acima do limite em scripts
- nenhum trigger habilitado em tarefas
- ScheduleCron ausente com TriggerRecurring ativo
- ScopeId ausente para escopos nao globais
- ScriptId inexistente ou inativo em tarefas RunScript
- InstallationType ou PackageId ausentes em tarefas de pacote
- CommandPayload ausente em CustomCommand

## Observabilidade e Auditoria

Scripts e tarefas geram:

- log estruturado com LogType Automation
- trilha de auditoria persistida
- propagacao de correlation id quando disponivel

Execucoes operacionais geram:

- AutomationExecutionReport com status inicial Dispatched
- atualizacao para Acknowledged no ACK do agent
- atualizacao para Completed ou Failed no RESULT do agent

## Orientacao de Consumo pelo Front

O front deve tratar scripts e tarefas como entidades separadas.

Fluxo recomendado:

1. cadastrar scripts reutilizaveis
2. cadastrar tarefas apontando para script, pacote ou comando customizado
3. consultar auditoria ao exibir historico administrativo
4. usar run-now e force-sync apenas como acao operacional, nao como substituto do cadastro estrutural
5. usar o historico de execucoes para acompanhamento por agent
