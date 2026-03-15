# Automation Enums Reference

## Objetivo

Este documento consolida os enums mais relevantes da automacao para consumo do front.

Uso recomendado:

- montar selects e filtros
- exibir labels amigaveis em telas administrativas
- interpretar historico de execucao e auditoria
- evitar divergencia entre UI e comportamento do backend

## AppApprovalScopeType

Usado em tarefas de automacao para definir o nivel do cadastro.

| Valor | Significado para o front | Uso tipico |
| --- | --- | --- |
| Global | regra aplicavel de forma geral, sem ScopeId | politica padrao para todos |
| Client | regra vinculada a um cliente especifico | automacao por cliente |
| Site | regra vinculada a um site especifico | automacao por unidade ou local |
| Agent | regra vinculada a um agent especifico | excecao ou ajuste pontual |

Observacao:

- ScopeId e obrigatorio para Client, Site e Agent
- ScopeId nao e usado para Global

## AppInstallationType

Usado quando a tarefa opera sobre pacote.

| Valor | Significado para o front | Uso tipico |
| --- | --- | --- |
| Winget | pacote gerenciado por Winget | instalar ou atualizar software em Windows |
| Chocolatey | pacote gerenciado por Chocolatey | instalar ou atualizar software em Windows |
| Custom | instalacao customizada | reservado para fluxos futuros ou integracoes proprias |

Observacao:

- para InstallPackage e UpdatePackage, esse campo e obrigatorio

## AutomationTaskActionType

Define o tipo de acao executada pela tarefa.

| Valor | Significado para o front | Uso tipico |
| --- | --- | --- |
| InstallPackage | instala um pacote | formularios com InstallationType e PackageId |
| UpdatePackage | atualiza um pacote | formularios com InstallationType e PackageId |
| RemovePackage | remove um pacote | formularios com InstallationType e PackageId |
| UpdateOrInstallPackage | tenta atualizar e, se necessario, instala | fluxo resiliente para pacote ausente ou nao atualizado |
| RunScript | executa um script cadastrado | formularios com ScriptId |
| CustomCommand | executa um comando customizado | formularios com CommandPayload |

Observacao:

- o front deve alterar os campos obrigatorios conforme o ActionType selecionado

## AutomationScriptType

Define o executor esperado para o script.

| Valor | Significado para o front | Uso tipico |
| --- | --- | --- |
| PowerShell | script PowerShell | automacoes nativas de Windows |
| Shell | script shell generico | ambientes Unix-like |
| Python | script Python | automacao baseada em interpretador Python |
| Batch | script .bat ou .cmd | automacoes legadas em Windows |
| Custom | tipo livre | integracoes futuras ou runtime especifico |

Observacao:

- esse valor ajuda o front a orientar o usuario sobre o ambiente de execucao esperado

## AutomationExecutionSourceType

Usado no historico de execucoes para informar de onde partiu a execucao.

| Valor | Significado para o front | Uso tipico |
| --- | --- | --- |
| RunNow | disparo manual feito pelo backend administrativo | botao de execucao imediata |
| Scheduled | execucao recorrente iniciada pelo agent | historico de rotinas agendadas |
| ForceSync | operacao tecnica de sincronizacao forcada | acao de manutencao ou reconciliacao |
| AgentManual | execucao manual iniciada pelo proprio agent | trilha operacional local |

Observacao:

- esse campo e importante para distinguir acao administrativa de rotina automatica

## AutomationExecutionStatus

Usado no historico de execucoes para informar o estado da operacao.

| Valor | Significado para o front | Uso tipico |
| --- | --- | --- |
| Dispatched | comando criado e despachado pelo backend | execucao ainda sem confirmacao do agent |
| Acknowledged | agent confirmou recebimento e inicio de processamento | execucao em andamento |
| Completed | execucao finalizada com sucesso | status final positivo |
| Failed | execucao finalizada com falha | status final negativo |

Observacao:

- para telas operacionais, Acknowledged pode ser tratado como progresso intermediario

## AutomationScriptChangeType

Usado nos endpoints de auditoria de scripts.

| Valor | Significado para o front | Uso tipico |
| --- | --- | --- |
| Created | script criado | trilha de criacao |
| Updated | script alterado | trilha de manutencao |
| Deleted | script removido | trilha de exclusao |
| Consumed | payload do script foi consumido | trilha de distribuicao ou uso |
| Activated | script foi ativado | controle de disponibilidade |
| Deactivated | script foi desativado | controle de indisponibilidade |

## AutomationTaskChangeType

Usado nos endpoints de auditoria de tarefas.

| Valor | Significado para o front | Uso tipico |
| --- | --- | --- |
| Created | tarefa criada | trilha de criacao |
| Updated | tarefa alterada | trilha de manutencao |
| Deleted | tarefa removida | trilha de exclusao |
| Activated | tarefa ativada | reabilitacao operacional |
| Deactivated | tarefa desativada | suspensao operacional |
| Synced | tarefa participou de sincronizacao | evento tecnico de politica |

## Orientacao de UX

Para evitar inconsistencias, o front deve:

1. mapear cada enum para label amigavel, mas preservar o valor original retornado pela API
2. trocar dinamicamente os campos obrigatorios do formulario conforme ActionType e ScopeType
3. usar SourceType e Status para compor timeline e badges de execucao
4. usar ChangeType para exibir auditoria com linguagem funcional, sem perder o valor tecnico original
