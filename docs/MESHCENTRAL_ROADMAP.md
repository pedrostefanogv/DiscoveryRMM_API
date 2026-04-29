# MeshCentral — Roadmap (consolidado)

> **Data:** 2026-04-16 (revisão) / consolidado em 2026-04-29  
> **Substitui:** `MESHCENTRAL_INTEGRATION_PLAN.md` (V1, removido)  
> **Status atual:** `MESHCENTRAL.md`

## Objetivo

Registrar o plano de evolução da integração MeshCentral para o projeto Discovery, alinhado ao playbook de referência e ao estado real do código. Este documento cobre o que **falta fazer**. O que **já está implementado** está documentado em `MESHCENTRAL.md`.

## Base da Revisão

Este plano foi elaborado a partir de três fontes:

1. o playbook detalhado em `MESHCENTRAL_PLAYBOOK.md`
2. o estado atual implementado em `MESHCENTRAL.md`
3. a revisao do codigo atual da integracao MeshCentral no backend Discovery

## Resumo Executivo

O projeto ja possui uma base funcional de integracao com MeshCentral, incluindo:

- geracao de URLs de embed
- sincronizacao de usuarios Discovery para contas MeshCentral
- reconciliacao de grupos por site
- provisioning de instalacao via meshagents
- perfis de direitos e reconciliacao por escopo

Mas a implementacao atual ainda diverge do modelo do playbook em pontos estruturais:

1. o embed web ainda usa auth na URL, quando o modelo alvo para browser e login token por usuario
2. a autenticacao administrativa e a autenticacao de sessao web ainda nao estao suficientemente separadas
3. a autorizacao atual esta centrada em mesh por site, nao em ACL por dispositivo
4. o dominio ainda nao persiste mesh node id no Agent, entao falta a chave tecnica de ligacao entre inventario local e MeshCentral
5. o username remoto atual nao e suficientemente estavel para longo prazo

Conclusao:

O ajuste necessario nao e apenas trocar a URL de embed. O projeto precisa evoluir de um modelo baseado em grupo por site como autorizacao final para um modelo hibrido:

- grupo por site como ancora de provisioning e enrollment
- conta tecnica separada para control.ashx e operacoes administrativas
- login token por usuario para sessao web embutida
- ACL por node para autorizacao efetiva
- mesh node id persistido no inventario local

## Estado Atual no Projeto

### Ja existe

- configuracao central em src/Discovery.Core/Configuration/MeshCentralOptions.cs
- geracao de embed em src/Discovery.Infrastructure/Services/MeshCentralEmbeddingService.cs
- cliente administrativo via WebSocket em src/Discovery.Infrastructure/Services/MeshCentralApiService.cs
- sync de identidade em src/Discovery.Infrastructure/Services/MeshCentralIdentitySyncService.cs
- resolucao de direitos em src/Discovery.Infrastructure/Services/MeshCentralPolicyResolver.cs
- reconcile de grupos por site em src/Discovery.Infrastructure/Services/MeshCentralGroupPolicySyncService.cs
- provisioning/install flows em src/Discovery.Infrastructure/Services/MeshCentralProvisioningService.cs e src/Discovery.Infrastructure/Services/MeshCentralInstallUrlBuilder.cs
- endpoints de usuario e admin em src/Discovery.Api/Controllers/MeshCentralController.cs
- endpoints agent-scoped em src/Discovery.Api/Controllers/AgentAuthController.cs
- fluxo deploy-token em src/Discovery.Api/Controllers/DeployTokensController.cs
- persistencia de MeshCentralUserId e MeshCentralUsername no usuario
- persistencia de MeshCentralGroupName e MeshCentralMeshId no site

### Ainda falta

- separacao formal entre token administrativo e token de browser
- conta tecnica explicita na configuracao
- mesh node id persistido no Agent
- autorizacao por node com adddeviceuser
- cleanup remoto de device no lifecycle de uninstall/desvinculacao
- testes automatizados dedicados para MeshCentral

## Diagnostico Tecnico

### 1. Fluxo web e fluxo administrativo ainda estao misturados

Hoje o projeto usa a mesma base criptografica para gerar auth token tanto no embed quanto no acesso administrativo. Isso conflita com o modelo recomendado pelo playbook, onde:

1. control.ashx usa auth token server-to-server
2. navegador usa login token por usuario

### 2. O backend ainda concentra responsabilidades demais

MeshCentralApiService hoje acumula:

- socket administrativo
- autenticacao administrativa
- provisioning de install
- binding de grupos
- user upsert
- membership sync

Isso dificulta manutencao, observabilidade e migracao segura.

### 3. O modelo atual de autorizacao nao chega ao nivel de node

O projeto sincroniza memberships em meshes de site. Isso cobre um caso inicial de organizacao, mas nao entrega o padrao forte do playbook, que usa nodeid como unidade final de autorizacao e sessao remota.

### 4. O Agent ainda nao possui a chave tecnica da integracao

O entity Agent ainda nao armazena mesh node id. Sem isso o sistema nao consegue fechar o ciclo completo de:

- abrir sessoes remotas com vinculo tecnico consistente
- conceder ou revogar ACL por dispositivo
- remover o device no MeshCentral quando o inventario local for removido

### 5. O username remoto precisa ser estabilizado

Hoje o username e derivado com base em cliente e login local. Isso pode ser suficiente no curto prazo, mas nao e o desenho mais robusto para durabilidade operacional. O modelo recomendado e um identificador tecnico deterministico ligado ao id interno do usuario.

## Modelo Alvo

### 1. Grupo por site continua existindo

O grupo por site deve continuar como ancora de:

- enrollment do MeshAgent
- distribuicao de instalador
- descoberta do mesh id
- reconciliacao de drift de configuracao

Mas ele deixa de ser o modelo final de autorizacao.

### 2. Conta tecnica separada

Uma conta tecnica dedicada deve ser usada apenas para:

- abrir control.ashx
- listar meshes e usuarios
- criar e atualizar usuarios remotos
- aplicar ACLs e memberships
- executar health-check e reconciliacao

Essa conta nao deve ser a identidade de sessao remota do usuario final.

### 3. Sessao web por login token

Toda URL de embed entregue ao browser deve usar:

- login token gerado no backend
- usuario MeshCentral correspondente ao usuario autenticado
- gotonode sempre que o node estiver conhecido
- viewmode e hide controlados apenas pelo backend

### 4. Inventario local com mesh node id

O Agent passa a armazenar o mesh node id como foreign key tecnica da integracao.

### 5. ACL por node como autorizacao efetiva

O backend continua sendo a fonte de verdade da autorizacao. O MeshCentral passa a refletir isso no nivel de dispositivo usando adddeviceuser e remocoes equivalentes.

## Arquitetura Recomendada

O desenho interno recomendado fica dividido em seis blocos:

1. MeshCentralConfigService
   - resolve URL publica, URL interna e topologia
   - concentra regras de dominio, TLS e fallback

2. MeshCentralTokenService
   - gera auth token para control.ashx
   - gera login token para browser

3. MeshCentralClient
   - encapsula o WebSocket administrativo
   - expoe comandos tipados e respostas normalizadas

4. MeshIdentityMapper
   - traduz usuario local em username tecnico, user id remoto, display name e metadados auxiliares

5. MeshAclSyncService
   - calcula diff entre estado desejado e estado atual
   - aplica ACL por node

6. MeshAgentEnrollmentService
   - resolve group id
   - gera install URL
   - registra ou reconcilia mesh node id

## Plano de Implementacao

### Fase 0 - Baseline operacional do MeshCentral

Objetivo:
Validar que o ambiente MeshCentral suporta o desenho alvo antes de alterar o codigo.

Validacoes obrigatorias:

- allowLoginToken habilitado
- allowFraming habilitado
- URL publica correta
- URL interna ou derivada correta para uso do backend
- WSS/HTTPS coerentes com a topologia
- proxy reverso compativel com iframe e WebSocket
- conta tecnica funcional em lowercase
- login token key gerada e protegida

Resultado esperado:

- confirmar que o problema nao e apenas operacional
- evitar implementar um fluxo que o servidor atual nao suporta

### Fase 1 - Refatoracao do contrato de configuracao

Objetivo:
Separar explicitamente configuracao de navegador, configuracao administrativa e configuracao de provisioning.

Mudancas propostas:

- evoluir MeshCentralOptions para representar:
  - PublicBaseUrl
  - InternalBaseUrl ou WsBaseUrl derivavel
  - DomainId
  - TechnicalUsername
  - TokenKeyHex
  - flags de TLS e timeouts
  - defaults de install e reconcile
- manter compatibilidade temporaria com BaseUrl/PublicBaseUrl/LoginKeyHex quando necessario
- atualizar Program.cs e docs de configuracao

Resultado esperado:

- menos ambiguidade
- eliminacao de dependencia implicita de admin
- melhor troubleshooting

### Fase 2 - Separacao de responsabilidades internas

Objetivo:
Reduzir acoplamento e tornar o backend MeshCentral auditavel e evolutivo.

Mudancas propostas:

- extrair MeshCentralTokenService de EmbeddingService e ApiService
- extrair MeshCentralClient do atual MeshCentralApiService
- introduzir MeshCentralConfigService para resolver URLs e esquema
- introduzir MeshIdentityMapper

Resultado esperado:

- menos duplicacao de logica criptografica
- melhor cobertura de testes
- menor risco na migracao para login token por usuario

### Fase 3 - Correcao da identidade remota

Objetivo:
Estabilizar a identidade do usuario no MeshCentral.

Mudancas propostas:

- parar de derivar username apenas de client prefix + login local
- adotar identificador tecnico estavel baseado no id interno do usuario
- preservar MeshCentralUserId como ancora para usuarios ja sincronizados
- evitar recriacao em massa sem estrategia formal de migracao

Resultado esperado:

- menos drift operacional
- identidade remota duravel
- menor risco de colisao futura

### Fase 4 - Introducao do mesh node id no dominio

Objetivo:
Persistir a chave tecnica entre inventario local e MeshCentral.

Mudancas propostas:

- adicionar campo de mesh node id em Agent
- mapear o campo no DbContext
- criar migration especifica
- ajustar AgentAuthController e fluxos de enrollment para reportar o node id
- definir estrategia de backfill e reconcile para agentes ja existentes

Resultado esperado:

- suporte real a gotonode
- base para ACL por dispositivo
- cleanup remoto viavel

### Fase 5 - Migracao da autorizacao para ACL por node

Objetivo:
Fazer o backend refletir autorizacao final por dispositivo, alinhado ao playbook.

Mudancas propostas:

- criar MeshAclSyncService dedicado
- usar MeshCentralPolicyResolver como fonte de direitos efetivos
- aplicar adddeviceuser e remocoes equivalentes por node
- preservar MeshCentralGroupPolicySyncService apenas para binding de grupo/site
- revisar os rights profiles atuais antes de reutilizar os masks no modelo por node

Resultado esperado:

- autorizacao aderente ao playbook
- menor excesso de permissao
- melhor coerencia entre inventario, usuario e acesso remoto

### Fase 6 - Correcao dos endpoints de UX e lifecycle

Objetivo:
Alinhar os fluxos HTTP com o novo contrato.

Mudancas propostas:

- atualizar MeshCentralController para sempre gerar embed via login token
- preferir MeshCentralNodeId persistido em vez de aceitar node id arbitrario do caller
- revisar o fluxo agent-scoped em AgentAuthController
- manter provisioning via deploy-token e agent-auth compativel com o novo enrollment
- adicionar cleanup remoto de device em desvinculacao ou uninstall
- adicionar health-check operacional protegido

Resultado esperado:

- sessao remota correta por usuario
- menos superficie de abuso
- operacao mais previsivel

### Fase 7 - Testes, rollout e documentacao

Objetivo:
Reduzir risco antes de habilitar applyChanges em ambiente real.

Cobertura recomendada:

- geracao de login token
- URLs de embed por usuario
- uso da conta tecnica no control.ashx
- identity mapping estavel
- persistencia e uso de mesh node id
- ACL diff e apply por node
- install URL builder
- cleanup remoto de device
- falhas de configuracao e mensagens de erro

Documentacao a atualizar:

- docs/MESHCENTRAL.md
- docs/CONFIGURATION.md
- checklists operacionais de rollout

## Sequencia de Rollout Recomendada

1. validar ambiente MeshCentral em homologacao
2. implantar apenas refatoracao de configuracao e separacao interna sem applyChanges destrutivo
3. ativar diagnostico e health-check
4. introduzir persistencia de mesh node id e rotinas de backfill
5. executar reconcile em dry-run para identidade, grupo e ACL por node
6. revisar diffs gerados
7. habilitar applyChanges de forma controlada
8. validar embed de usuario, deploy-token, fluxo agent-auth e cleanup remoto

## Arquivos Prioritarios

- src/Discovery.Core/Configuration/MeshCentralOptions.cs
- src/Discovery.Api/Program.cs
- src/Discovery.Infrastructure/Services/MeshCentralEmbeddingService.cs
- src/Discovery.Infrastructure/Services/MeshCentralApiService.cs
- src/Discovery.Infrastructure/Services/MeshCentralIdentitySyncService.cs
- src/Discovery.Infrastructure/Services/MeshCentralGroupPolicySyncService.cs
- src/Discovery.Infrastructure/Services/MeshCentralPolicyResolver.cs
- src/Discovery.Infrastructure/Services/MeshCentralProvisioningService.cs
- src/Discovery.Infrastructure/Services/MeshCentralInstallUrlBuilder.cs
- src/Discovery.Api/Controllers/MeshCentralController.cs
- src/Discovery.Api/Controllers/AgentAuthController.cs
- src/Discovery.Api/Controllers/DeployTokensController.cs
- src/Discovery.Core/Entities/Agent.cs
- src/Discovery.Core/Entities/Identity/User.cs
- src/Discovery.Infrastructure/Data/DiscoveryDbContext.cs
- src/Discovery.Migrations/Migrations/
- docs/MESHCENTRAL.md
- docs/CONFIGURATION.md

## Decisoes Propostas para Revisao

1. manter grupo por site como ancora de provisioning, mas nao como autorizacao final
2. adotar conta tecnica separada para backend administrativo
3. adotar login token por usuario para embed web
4. introduzir mesh node id como campo obrigatorio de integracao no Agent
5. migrar autorizacao efetiva para ACL por node
6. preservar MeshCentralUserId como ancora de migracao dos usuarios ja sincronizados
7. manter rollout progressivo com dry-run antes de applyChanges

## Escopo Fora da Primeira Entrega

Nao entram na primeira entrega deste plano:

- noVNC com getcookie e meshrelay
- runcommands para recovery remoto
- bootstrap completo do servidor MeshCentral
- backup, restore e update automatizado do MeshCentral
- SSO/OIDC com MeshCentral
- ingestao de eventos MeshCentral para auditoria expandida

## Riscos Principais

1. reutilizar rights profiles atuais sem recalibrar semantica para ACL por node
2. expor a chave raiz de token fora do backend
3. misturar URL publica e URL interna do MeshCentral
4. migrar usernames remotos em massa sem janela controlada
5. ativar reconcile destrutivo antes de validar o estado desejado
6. depender de node id arbitrario informado pelo caller em vez de node persistido

## Checklist de Revisao Antes de Implantar

### Arquitetura

- o desenho separa claramente navegador e backend
- o browser nao depende de auth token administrativo
- o backend e a fonte de verdade da autorizacao

### Dominio

- Agent passa a armazenar mesh node id
- User continua armazenando MeshCentralUserId e MeshCentralUsername
- group binding por site continua suportado

### Operacao

- existe health-check protegido
- existe modo dry-run para reconcile
- existe plano de rollback da migracao

### Testes

- existe cobertura de token, embed, enrollment, ACL e cleanup
- existe validacao funcional em homologacao

## Conclusao

O projeto nao parte do zero. A base atual ja entrega parte importante da integracao, mas ainda esta mais proxima de um modelo de grupos por site com embed assinado do que do desenho robusto do playbook.

O caminho recomendado e evolutivo, nao destrutivo:

1. formalizar a configuracao
2. separar responsabilidades internas
3. estabilizar identidade remota
4. persistir mesh node id
5. migrar autorizacao para node ACL
6. somente depois expandir para capacidades mais avancadas

Esse plano deve ser revisado tecnicamente antes de qualquer rollout, especialmente nas partes de migracao de username remoto, rights profiles e estrategia de backfill de node id.