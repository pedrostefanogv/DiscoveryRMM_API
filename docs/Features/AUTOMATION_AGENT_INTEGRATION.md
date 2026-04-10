# Automation Agent Integration

## Objetivo

Este documento descreve a integracao do agent com a API de automacao do Discovery.

Escopo deste documento:

- autenticacao e contexto esperado do agent
- sincronizacao de politicas de automacao
- devolucao de ACK e resultado de execucao
- significado funcional dos campos retornados pela API
- validacoes e regras operacionais que o agent deve respeitar

Este documento nao cobre CRUD administrativo de scripts e tarefas. Essa parte esta no documento de front/site.

## Contexto Geral

O backend atua como control plane.

Responsabilidades do backend:

- cadastro de scripts reutilizaveis
- cadastro de tarefas de automacao
- resolucao de escopo global, client, site e agent
- filtragem por tags
- auditoria, logging e historico operacional
- disparo manual de execucao imediata

Responsabilidades esperadas do agent:

- sincronizar a politica aplicavel ao proprio contexto
- armazenar fingerprint da politica conhecida
- executar localmente as tarefas recebidas
- aplicar agendamento local quando houver gatilhos recorrentes
- reportar ACK e resultado de cada execucao

## Autenticacao

Todos os endpoints deste documento exigem autenticacao do agent no contexto de api/agent-auth.

Comportamento padrao quando nao autenticado:

- resposta 401 com erro Agent not authenticated.

Comportamento padrao quando o recurso nao pertence ao agent autenticado:

- resposta 404 quando o comando informado nao existir para o agent atual

## Correlation Id

Os endpoints de sincronizacao e callback aceitam o header X-Correlation-Id.

Regras:

- se o header for enviado, o backend reutiliza esse valor
- se nao for enviado no policy sync, o backend gera um novo valor e devolve no header de resposta
- em ACK e RESULT, o valor recebido e armazenado no historico quando presente

Uso recomendado no agent:

- gerar um correlation id por ciclo de sincronizacao
- reutilizar o mesmo correlation id durante os callbacks vinculados ao mesmo fluxo operacional quando fizer sentido

## Endpoint: Sincronizar Politica

Metodo: POST

Rota: /api/agent-auth/me/automation/policy-sync

Finalidade:

- obter a lista de tarefas de automacao aplicaveis ao agent autenticado
- evitar transferencia desnecessaria quando a politica nao mudou
- opcionalmente receber o conteudo completo do script junto com a politica

### Corpo da requisicao

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| KnownPolicyFingerprint | string nullable | nao | fingerprint que o agent ja possui localmente |
| IncludeScriptContent | bool | nao | indica se o backend deve incluir Content do script quando a tarefa for RunScript |

### Regras de comportamento

- o backend resolve o agent autenticado, seu site e seu client
- o backend carrega tarefas ativas dos escopos Global, Client, Site e Agent
- as tarefas sao consolidadas por id e filtradas por tags do agent
- include tags: se existir ao menos uma include tag, o agent precisa casar com pelo menos uma delas
- exclude tags: se qualquer tag excluida casar com o agent, a tarefa nao e entregue
- o fingerprint e calculado a partir do conjunto de tarefas aplicaveis e seus metadados operacionais
- se KnownPolicyFingerprint for igual ao fingerprint calculado, a resposta vem com UpToDate igual a true e sem tarefas

### Resposta

| Campo | Tipo | Finalidade |
| --- | --- | --- |
| UpToDate | bool | informa se a politica local do agent ainda esta valida |
| PolicyFingerprint | string | identificador hash da versao atual da politica |
| GeneratedAt | datetime utc | momento de geracao da resposta |
| TaskCount | int | quantidade de tarefas aplicaveis |
| Tasks | lista | tarefas resolvidas para o agent |

### Estrutura de cada tarefa retornada

| Campo | Tipo | Finalidade |
| --- | --- | --- |
| TaskId | guid | identificador da tarefa |
| Name | string | nome operacional da tarefa |
| Description | string nullable | descricao administrativa |
| ActionType | enum | InstallPackage, UpdatePackage, RunScript ou CustomCommand |
| InstallationType | enum nullable | loja ou origem de pacote quando a acao for de pacote |
| PackageId | string nullable | identificador do pacote quando a acao for de pacote |
| ScriptId | guid nullable | referencia ao script quando a acao for RunScript |
| CommandPayload | string nullable | payload bruto quando a acao for CustomCommand |
| ScopeType | enum | escopo administrativo da tarefa |
| RequiresApproval | bool | sinaliza que a tarefa possui alto impacto e exige fluxo de aprovacao no desenho funcional |
| TriggerImmediate | bool | executavel imediatamente apos sincronizacao, conforme politica local do agent |
| TriggerRecurring | bool | indica que a tarefa participa de agenda recorrente |
| TriggerOnUserLogin | bool | indica disparo no login de usuario |
| TriggerOnAgentCheckIn | bool | indica disparo em check-in do agent |
| ScheduleCron | string nullable | expressao de agenda quando TriggerRecurring estiver ativo |
| IncludeTags | lista de string | tags que habilitam a tarefa |
| ExcludeTags | lista de string | tags que bloqueiam a tarefa |
| LastUpdatedAt | datetime utc | usado para invalidacao e reconciliacao local |
| Script | objeto nullable | dados do script referenciado, quando aplicavel |

### Estrutura do objeto Script

O objeto Script so pode existir quando ActionType for RunScript e ScriptId estiver resolvido para um script ativo.

| Campo | Tipo | Finalidade |
| --- | --- | --- |
| ScriptId | guid | identificador do script |
| Name | string | nome do script |
| Version | string | versao administrativa do script |
| Summary | string | resumo funcional |
| ScriptType | enum | PowerShell, Shell, Python, Batch ou Custom |
| LastUpdatedAt | datetime utc | controle de atualizacao |
| ContentHashSha256 | string | hash SHA-256 do conteudo efetivo |
| Content | string nullable | conteudo bruto do script, apenas quando IncludeScriptContent for true |
| ParametersSchemaJson | string nullable | contrato textual dos parametros esperados pelo script |
| MetadataJson | string nullable | metadados adicionais definidos no cadastro |

### Regras que o agent deve respeitar

- tratar UpToDate igual a true como ausencia de alteracao de politica
- persistir PolicyFingerprint apos sincronizacao bem-sucedida
- nao assumir que o campo Content vira sempre preenchido; ele depende de IncludeScriptContent
- validar localmente a compatibilidade entre ScriptType e o executor disponivel no host
- tratar RequiresApproval como informacao normativa do backend, mesmo que a execucao efetiva ainda dependa de evolucao adicional do fluxo de aprovacao

## Endpoint: Enviar ACK de Execucao

Metodo: POST

Rota: /api/agent-auth/me/automation/executions/{commandId}/ack

Finalidade:

- informar que o agent recebeu o comando e iniciou o processamento da execucao
- registrar associacao entre commandId, taskId, scriptId e sourceType no historico de automacao

### Parametro de rota

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| commandId | guid | sim | identificador do AgentCommand que foi recebido pelo agent |

### Corpo da requisicao

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| TaskId | guid nullable | nao | tarefa associada, quando a execucao vier de uma tarefa |
| ScriptId | guid nullable | nao | script associado, quando existir |
| SourceType | enum | nao | origem da execucao. Default do backend: RunNow |
| MetadataJson | string nullable | nao | metadados de ACK gerados pelo agent |

### Regras de comportamento

- o commandId precisa existir e pertencer ao agent autenticado
- se ainda nao existir historico para o commandId, o backend cria um registro em status Acknowledged
- se ja existir historico, o backend apenas atualiza os campos de ACK
- se o AgentCommand ainda estiver em Pending, o backend o move para Sent

### Resposta

| Campo | Tipo | Finalidade |
| --- | --- | --- |
| acknowledged | bool | confirma registro do ACK |
| commandId | guid | comando referenciado |

## Endpoint: Enviar Resultado de Execucao

Metodo: POST

Rota: /api/agent-auth/me/automation/executions/{commandId}/result

Finalidade:

- informar conclusao da execucao
- registrar status final, metadados, codigo de saida e erro operacional quando houver
- sincronizar o estado final do AgentCommand administrativo

### Parametro de rota

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| commandId | guid | sim | identificador do AgentCommand que foi executado |

### Corpo da requisicao

| Campo | Tipo | Obrigatorio | Finalidade |
| --- | --- | --- | --- |
| TaskId | guid nullable | nao | tarefa associada |
| ScriptId | guid nullable | nao | script associado |
| SourceType | enum | nao | RunNow, Scheduled, ForceSync ou AgentManual |
| Success | bool | sim | indica sucesso ou falha final |
| ExitCode | int nullable | nao | codigo do processo local, quando houver |
| ErrorMessage | string nullable | nao | descricao de falha quando Success for false |
| MetadataJson | string nullable | nao | metadados finais produzidos pelo agent |

### Regras de comportamento

- o commandId precisa existir e pertencer ao agent autenticado
- se nao existir historico previo, o backend cria diretamente um registro final
- se existir historico previo, o backend atualiza o registro ja existente
- Success true gera status Completed
- Success false gera status Failed
- o AgentCommand administrativo tambem e atualizado para Completed ou Failed

### Resposta

| Campo | Tipo | Finalidade |
| --- | --- | --- |
| completed | bool | confirma registro do resultado |
| commandId | guid | comando referenciado |
| success | bool | resultado final aceito pelo backend |

## Enums Relevantes

### AutomationTaskActionType

- InstallPackage
- UpdatePackage
- RunScript
- CustomCommand

### AutomationScriptType

- PowerShell
- Shell
- Python
- Batch
- Custom

### AutomationExecutionSourceType

- RunNow: disparo manual originado do backend administrativo
- Scheduled: execucao recorrente iniciada pelo agendador local do agent
- ForceSync: ciclo tecnico de sincronizacao forcada
- AgentManual: execucao manual iniciada localmente pelo agent

### AutomationExecutionStatus

- Dispatched
- Acknowledged
- Completed
- Failed

## Validacoes e Cuidados de Integracao

O backend nao faz validacao semantica profunda de MetadataJson, ParametersSchemaJson e MetadataJson de script.

Isso significa:

- esses campos devem ser tratados como contratos textuais transportados pela API
- o agent precisa validar localmente qualquer schema, metadata ou formato interno de parametros antes de executar

Cuidados adicionais:

- ScheduleCron so tem valor funcional quando TriggerRecurring estiver ativo
- uma tarefa pode combinar mais de um gatilho ao mesmo tempo
- a ausencia de tarefas na resposta nao deve ser tratada como erro
- tarefas retornadas pelo policy sync ja chegam filtradas por escopo e tags, entao o agent nao precisa repetir essa resolucao, apenas obedecer a politica recebida

## Fluxo Operacional Esperado

1. o agent autentica no contexto agent-auth
2. o agent chama policy-sync enviando o fingerprint conhecido
3. o backend devolve UpToDate true ou a nova lista de tarefas aplicaveis
4. o agent atualiza sua politica local e agenda interna
5. ao iniciar uma execucao vinculada a commandId, o agent envia ACK
6. ao concluir a execucao, o agent envia RESULT
