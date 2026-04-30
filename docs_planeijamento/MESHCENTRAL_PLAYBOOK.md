# MeshCentral Integration Playbook

## Objetivo

Este documento explica como o TacticalRMM integra com o MeshCentral e transforma essa analise em um plano reutilizavel para implementar o mesmo tipo de integracao em outro projeto.

O ponto principal e este:

1. O TacticalRMM nao usa MeshCentral apenas como uma UI remota.
2. Ele trata o MeshCentral como um servico de acesso remoto, distribuicao de agent, relay e automacao administrativa.
3. A integracao e dividida em bootstrap da infraestrutura, autenticacao server-to-server, sincronizacao de usuarios/permissoes, instalacao do MeshAgent, registro do node id e geracao de links/tokens para sessoes remotas.

## Resumo Executivo

O desenho real do TacticalRMM com MeshCentral tem 6 camadas:

1. Provisionamento do MeshCentral: instala o servico, escreve o `config.json`, cria a conta admin e o device group.
2. Persistencia da integracao no backend: guarda `mesh_site`, `mesh_username`, `mesh_token` e `mesh_device_group` em `CoreSettings`.
3. Automacao administrativa por WebSocket: usa `control.ashx` com token de autenticacao derivado do `MESH_TOKEN_KEY`.
4. Distribuicao do MeshAgent: resolve o device group no MeshCentral, gera a URL correta de `meshagents` e injeta o instalador no fluxo do agent do produto.
5. Sincronizacao de usuarios e ACLs: cria/remove usuarios no MeshCentral e ajusta permissao por node com `adddeviceuser`.
6. Sessoes remotas para a UI: gera login tokens por usuario e monta URLs para control, terminal, files e noVNC.

Se voce quiser reproduzir isso em outro sistema, a melhor leitura e:

1. Use MeshCentral como servico remoto especializado.
2. Mantenha toda autenticacao sensivel do lado do servidor.
3. Controle a autorizacao por device no seu proprio dominio e replique isso no MeshCentral.
4. Trate `mesh_node_id` como a chave de ligacao entre o seu inventario e o MeshCentral.

## Onde a integracao esta no codigo

### Bootstrap e deploy

- `install.sh`
- `docker/containers/tactical-meshcentral/entrypoint.sh`
- `docker/containers/tactical/entrypoint.sh`
- `ansible/roles/trmm_dev/tasks/main.yml`
- `ansible/roles/trmm_dev/templates/mesh.cfg.j2`

### Configuracao persistida no backend

- `api/tacticalrmm/core/models.py`
- `api/tacticalrmm/core/views.py`
- `api/tacticalrmm/core/management/commands/initial_mesh_setup.py`
- `api/tacticalrmm/core/management/commands/check_mesh.py`
- `api/tacticalrmm/core/management/commands/get_mesh_login_url.py`
- `api/tacticalrmm/core/management/commands/get_mesh_exe_url.py`

### Cliente MeshCentral no backend

- `api/tacticalrmm/core/utils.py`
- `api/tacticalrmm/core/mesh_utils.py`

### Fluxo de agent e sessao remota

- `api/tacticalrmm/apiv3/views.py`
- `api/tacticalrmm/agents/utils.py`
- `api/tacticalrmm/agents/views.py`
- `api/tacticalrmm/agents/models.py`
- `api/tacticalrmm/tacticalrmm/constants.py`

### Gatilhos de sincronizacao

- `api/tacticalrmm/core/tasks.py`
- `api/tacticalrmm/core/management/commands/post_update_tasks.py`
- `api/tacticalrmm/core/management/commands/sync_mesh_with_trmm.py`
- `api/tacticalrmm/accounts/views.py`
- `api/tacticalrmm/ee/sso/adapter.py`

### Operacao, backup, restore e update

- `backup.sh`
- `restore.sh`
- `update.sh`

## Como o TacticalRMM faz a integracao

## 1. Sobe e configura o MeshCentral

### Bare metal

No `install.sh`, o TacticalRMM:

1. cria `/meshcentral/meshcentral-data`
2. escreve `/meshcentral/package.json` com dependencias fixas
3. escreve `/meshcentral/meshcentral-data/config.json`
4. habilita opcoes importantes do MeshCentral:
   - `allowLoginToken: true`
   - `allowFraming: true`
   - `mstsc: true`
   - `tlsOffload: 127.0.0.1`
   - `WANonly: true`
5. usa Postgres como banco do MeshCentral
6. sobe o servico e espera o MeshCentral ficar pronto
7. executa `node node_modules/meshcentral --logintokenkey`
8. grava essa chave em `MESH_TOKEN_KEY` no `local_settings.py`
9. cria a conta admin do MeshCentral
10. cria o device group `TacticalRMM`

### Docker

No Docker, o desenho muda um pouco:

1. o container `tactical-meshcentral` gera `config.json`
2. cria a conta admin do MeshCentral
3. gera a chave de login token e salva em arquivo compartilhado
4. expõe o MeshCentral internamente em `ws://tactical-meshcentral:4443`
5. o backend Django consome esse valor via `MESH_WS_URL`

O `docker-compose.yml` mostra a separacao dos servicos:

1. `tactical-meshcentral`
2. `tactical-mongodb`
3. `tactical` (backend)

### Ansible

O Ansible replica o mesmo processo do install script:

1. instala MeshCentral com `npm install meshcentral@...`
2. sobe o servico
3. gera a login token key
4. grava a chave em `local_settings.py`
5. cria o usuario admin
6. cria o device group `TacticalRMM`
7. executa `python manage.py sync_mesh_with_trmm`

## 2. Persiste a configuracao de integracao no banco da aplicacao

O model `CoreSettings` guarda os dados de integracao:

- `mesh_token`
- `mesh_username`
- `mesh_site`
- `mesh_device_group`
- `mesh_company_name`
- `sync_mesh_with_trmm`

Isso define uma estrategia importante do TacticalRMM:

1. configuracao publica do MeshCentral e configuracao de runtime ficam no banco da aplicacao
2. segredo raiz de login token tambem fica espelhado no backend
3. o backend consegue operar mesmo quando o MeshCentral esta em outro host ou em outro container

O comando `initial_mesh_setup` faz o bootstrap logico:

1. garante `mesh_username`, `mesh_site` e `mesh_token` em `CoreSettings`
2. abre um WebSocket para o MeshCentral
3. checa se existem device groups
4. se nao houver nenhum, cria o group `TacticalRMM`

Observacao importante: no fluxo real de instalacao, o grupo `TacticalRMM` tambem e criado explicitamente pelos scripts de install/Ansible usando `meshctrl.js`. Isso reduz o risco do bootstrap automatico nao encontrar o estado esperado.

## 3. Usa dois tipos de token, cada um com um papel diferente

Esse e um dos pontos mais importantes para replicar em outro projeto.

### Token 1: auth token para `control.ashx`

No backend, `get_mesh_ws_url()` faz:

1. pega o `mesh_api_superuser` em lowercase
2. usa `meshctrl.utils.get_auth_token(core.mesh_api_superuser, core.mesh_token)`
3. monta a URL `.../control.ashx?auth=TOKEN`

Esse token serve para automacao server-to-server via WebSocket.

### Token 2: login token para links de sessao remota

Para abrir a UI do MeshCentral sem login manual, o TacticalRMM usa `get_login_token()`.

Ele monta URLs como:

```text
https://mesh.example.com/?login=TOKEN&gotonode=NODEID&viewmode=11&hide=31
```

O TacticalRMM usa isso para:

1. remote control
2. terminal
3. file browser

Conclusao de arquitetura:

1. `auth token` e para o backend falar com `control.ashx`
2. `login token` e para o navegador do usuario entrar direto numa view do MeshCentral

## 4. Fala com o MeshCentral diretamente pelo WebSocket administrativo

O TacticalRMM nao depende do CLI `meshctrl.js` no runtime da aplicacao.

No runtime ele fala direto com `control.ashx` via `websockets.connect(...)`.

O encapsulamento principal esta em `MeshSync` dentro de `api/tacticalrmm/core/mesh_utils.py`.

### Operacoes usadas pelo TacticalRMM

O projeto usa estas acoes do MeshCentral:

1. `meshes`
2. `users`
3. `adduser`
4. `edituser`
5. `deleteuser`
6. `adddeviceuser`
7. `getcookie`
8. `removedevices`
9. `runcommands`
10. `createmesh`
11. `createInviteLink`

### Exemplos de payloads reais

Listar device groups:

```json
{
  "action": "meshes",
  "responseid": "meshctrl"
}
```

Listar usuarios:

```json
{
  "action": "users",
  "responseid": "meshctrl"
}
```

Criar usuario:

```json
{
  "action": "adduser",
  "username": "john___12",
  "email": "johnabc123@tacticalrmm-do-not-change-xyz.local",
  "pass": "generated-password",
  "resetNextLogin": false,
  "randomPassword": false,
  "removeEvents": false,
  "emailVerified": true,
  "responseid": "meshctrl"
}
```

Atualizar display name:

```json
{
  "action": "edituser",
  "id": "user//john___12",
  "realname": "John Doe - My Company",
  "responseid": "meshctrl"
}
```

Remover usuario:

```json
{
  "action": "deleteuser",
  "userid": "user//john___12",
  "responseid": "meshctrl"
}
```

Adicionar usuarios a um device:

```json
{
  "action": "adddeviceuser",
  "nodeid": "node//...",
  "usernames": ["john___12", "mary___13"],
  "rights": 4088024,
  "remove": false,
  "responseid": "meshctrl"
}
```

Remover usuarios de um device:

```json
{
  "action": "adddeviceuser",
  "nodeid": "node//...",
  "userids": ["user//john___12"],
  "rights": 0,
  "remove": true,
  "responseid": "meshctrl"
}
```

Pedir cookie para relay/noVNC:

```json
{
  "action": "getcookie",
  "name": null,
  "nodeid": "node//...",
  "tag": "novnc",
  "tcpaddr": null,
  "tcpport": 5900,
  "responseid": "meshctrl"
}
```

Remover devices do MeshCentral:

```json
{
  "action": "removedevices",
  "nodeids": ["node//..."],
  "responseid": "trmm"
}
```

Executar comando remoto via MeshCentral:

```json
{
  "action": "runcommands",
  "cmds": "systemctl restart tacticalagent.service",
  "nodeids": ["node//..."],
  "runAsUser": 0,
  "type": 3,
  "responseid": "trmm"
}
```

## 5. Usa o device group como ancora da distribuicao do MeshAgent

O TacticalRMM precisa descobrir o ID interno do device group para baixar o agent correto.

Fluxo:

1. abre `control.ashx` com auth token
2. envia `{"action": "meshes"}`
3. procura pelo group com nome igual a `core.mesh_device_group`
4. pega o `_id`
5. remove o prefixo `mesh//`

Esse valor vira `mesh_device_id`.

### Como a URL de download do MeshAgent e montada

`get_meshagent_url()` trata 3 cenarios:

1. install local bare metal: `http://127.0.0.1:<mesh_port>/meshagents?...`
2. Docker: `http://tactical-meshcentral:4443/meshagents?...`
3. Mesh externo: `https://mesh.example.com/meshagents?...`

### Parametros por plataforma

#### Windows

```text
/meshagents?id=<agent-ident>&meshid=<device-group-id>&installflags=0
```

#### Linux e macOS

```text
/meshagents?id=<device-group-id>&installflags=2&meshinstall=<agent-ident>
```

Os `MeshAgentIdent` usados pelo projeto ficam em `api/tacticalrmm/tacticalrmm/constants.py`.

## 6. Captura o `mesh_node_id` e o usa como chave de ligacao entre os dois sistemas

Esse e o elo mais importante entre a sua aplicacao e o MeshCentral.

### Linux

No instalador Linux:

1. o backend gera um script shell a partir de `api/tacticalrmm/core/agent_linux.sh`
2. injeta `meshDL`
3. instala o MeshAgent
4. executa o agent do produto para obter o node id local
5. passa `--meshnodeid <MESH_NODE_ID>` durante a instalacao do Tactical agent

### Windows e macOS

No fluxo `apiv3/meshexe/`:

1. o instalador baixa o MeshAgent via backend
2. o agent depois envia seu `nodeid` para `apiv3/syncmesh/`
3. o backend grava `agent.mesh_node_id`

Conclusao:

1. o MeshAgent e instalado antes ou junto do seu agent principal
2. seu agent principal precisa aprender o `mesh_node_id`
3. esse valor precisa ser salvo no inventario da sua aplicacao

Sem esse passo, voce nao consegue:

1. abrir sessao remota
2. aplicar ACL por device
3. rodar comandos remotos via MeshCentral
4. remover o device do MeshCentral no uninstall

## 7. Faz sincronizacao bidirecional de usuarios e permissoes, mas com a aplicacao como fonte da verdade

O fluxo central esta em `sync_mesh_perms_task`.

### Como o TacticalRMM modela identidade MeshCentral

No model `User`:

- `mesh_username = username_sanitizado + "___" + pk`
- `mesh_user_id = "user//" + mesh_username`

Isso e extremamente importante.

O TacticalRMM nao tenta usar email real nem username puro como identidade no MeshCentral. Ele cria um identificador tecnico, deterministico e estavel.

### Como ele decide quem vai para o MeshCentral

O usuario so entra no sync se:

1. nao estiver vinculado a um agent
2. nao for installer user
3. estiver ativo
4. nao tiver `block_dashboard_login`

Para autorizar o uso de MeshCentral, o usuario precisa:

1. ser superuser
2. ou ter role com `can_use_mesh`

### Como ele decide o acesso por agente

Para cada agent:

1. se o agent nao tiver `mesh_node_id`, ele e ignorado
2. o backend calcula quais usuarios devem ter acesso a esse node
3. compara o estado desejado com o estado atual retornado pelo MeshCentral
4. adiciona ou remove usuarios por node

### Como ele cria usuarios no MeshCentral

Ao criar usuario MeshCentral:

1. gera senha aleatoria
2. gera email artificial para evitar colisao
3. ajusta `realname` com `first_name`, `last_name` e `mesh_company_name`

Exemplo do display name:

```text
John Doe - My Company Inc.
```

### O que o `rights = 4088024` concede

Segundo a documentacao/codigo do MeshCentral, esse bitmask concede um conjunto amplo de direitos por device, incluindo:

1. remote control
2. agent console
3. server files
4. wake device
5. set notes
6. chat notify
7. uninstall
8. remote command
9. reset/power off
10. guest sharing
11. device details
12. relay

Em outras palavras, o TacticalRMM esta concedendo um perfil de operacao remota forte, mas ainda controlado por device.

### Gatilhos que disparam resync

O sync pode ser disparado por:

1. criacao, edicao ou remocao de usuario
2. edicao ou remocao de role
3. alteracao de `CoreSettings`
4. onboarding de novo agent
5. update/delete de agent
6. signup via SSO
7. `post_update_tasks`
8. comando manual `sync_mesh_with_trmm`

### Detalhe operacional importante

Se `sync_mesh_with_trmm` estiver desabilitado, o job remove usuarios Mesh gerenciados pela aplicacao. Isso quer dizer que o sync e destrutivo. A sua aplicacao precisa deixar isso muito claro.

## 8. Gera links de sessao remota com token por usuario

O endpoint `AgentMeshCentral` gera tres URLs:

1. control
2. terminal
3. file

Formato:

```text
<mesh_site>/?login=<token>&gotonode=<mesh_node_id>&viewmode=<modo>&hide=31
```

Modos usados:

1. `viewmode=11` para control
2. `viewmode=12` para terminal
3. `viewmode=13` para files

### Comportamento com sync ligado ou desligado

Se `sync_mesh_with_trmm` estiver ligado:

1. o login token e gerado para `request.user.mesh_user_id`
2. o usuario entra no MeshCentral como si mesmo

Se estiver desligado:

1. o login token e gerado para `user//<mesh_api_superuser>`
2. a aplicacao passa a navegar usando a conta admin do MeshCentral

Isso mostra duas estrategias possiveis para outro projeto:

1. modelo forte: usuario por usuario no MeshCentral
2. modelo simples: conta tecnica unica para toda a UI

O TacticalRMM implementa os dois, mas privilegia o primeiro.

## 9. Usa `getcookie` e `meshrelay` para noVNC

O endpoint `WebVNC` faz outro tipo de integracao:

1. chama `getcookie` para um `nodeid`
2. recebe um cookie de relay
3. monta a URL do noVNC usando `meshrelay.ashx?auth=<cookie>`

Esse padrao e muito util se o seu projeto quiser embutir noVNC sem expor autenticao direta do MeshCentral ao browser.

## 10. Usa o MeshCentral como canal de recuperacao do proprio agent

O model `Agent.recover()` mostra uma ideia muito boa de arquitetura:

1. se o agent principal estiver ruim mas o MeshAgent ainda estiver vivo, o sistema consegue se recuperar via MeshCentral
2. para `tacagent`, ele usa `runcommands` no MeshCentral para reiniciar o agent principal
3. para `mesh`, ele usa NATS para pedir ao agent principal que recupere o MeshAgent

Isso cria duas trilhas de recuperacao independentes.

Para outro projeto, essa e uma estrategia fortissima:

1. use seu proprio canal de controle para reparar o MeshAgent
2. use o MeshCentral para reparar o agent da sua aplicacao

## 11. Remove devices do MeshCentral no uninstall

Quando um agent e removido:

1. o backend manda o uninstall para o agent local
2. apaga o registro do agent na aplicacao
3. tenta chamar `removedevices` no MeshCentral
4. dispara novo sync de permissoes

Ou seja, a aplicacao tenta manter o inventario do MeshCentral limpo e alinhado com o inventario principal.

## 12. Tem tratamento operacional especial para backup, restore e update

Esse ponto costuma ser ignorado em integracoes apressadas.

### Backup

O `backup.sh`:

1. faz `--dbexport` no MeshCentral
2. se o `config.json` usa Postgres, faz `pg_dump` do banco `meshcentral`
3. senao faz `mongodump`
4. empacota `/meshcentral`

### Restore

O `restore.sh`:

1. recria o banco do MeshCentral
2. se vier de Mongo, converte o config para Postgres
3. restaura o banco do MeshCentral
4. reinstala as dependencias do Node

### Update

O `update.sh`:

1. revisa `config.json`
2. desabilita `compression`, `wsCompression` e `agentWsCompression` se necessario
3. reinicia o MeshCentral

Isso sugere que a integracao com MeshCentral precisa prever manutencao de compatibilidade, nao apenas CRUD e links.

## Desenho de arquitetura do TacticalRMM

## Fluxo 1: bootstrap da plataforma

```text
Installer/Ansible/Docker
  -> sobe MeshCentral
  -> gera login token key
  -> cria conta admin
  -> cria device group
  -> grava config no backend
  -> executa initial_mesh_setup
  -> executa sync_mesh_with_trmm
```

## Fluxo 2: instalacao de agent

```text
Backend
  -> consulta MeshCentral para resolver device group id
  -> gera URL de meshagents ou baixa o MeshAgent
  -> instala MeshAgent
  -> captura mesh_node_id
  -> registra agent na aplicacao com mesh_node_id
  -> dispara sync de permissoes
```

## Fluxo 3: sincronizacao de ACL

```text
Aplicacao (fonte da verdade)
  -> calcula usuarios validos
  -> calcula permissao por device
  -> le usuarios/nodes do MeshCentral
  -> cria/remove usuarios Mesh
  -> adiciona/remove links user <-> node
  -> corrige display names
```

## Fluxo 4: sessao remota

```text
Usuario autenticado na aplicacao
  -> backend gera login token MeshCentral
  -> backend monta URL gotonode/viewmode
  -> browser abre MeshCentral diretamente na view correta
```

## O que e especifico do TacticalRMM e o que e reutilizavel

## Especifico do TacticalRMM

1. nome do device group padrao `TacticalRMM`
2. `mesh_username = username___pk`
3. uso de NATS como segundo canal de controle
4. triggers de sync espalhados pelos endpoints do produto
5. naming e UX especificos dos endpoints `meshcentral`, `meshexe`, `syncmesh`
6. `mesh_company_name` para compor display name
7. regra de fake email para evitar colisao no MeshCentral

## Reutilizavel em qualquer outro projeto

1. gerar e armazenar a login token key
2. usar `control.ashx` com auth token server-side
3. usar login token apenas para links de sessao no browser
4. tratar `mesh_node_id` como foreign key tecnica para o device
5. manter a sua aplicacao como fonte da verdade para autorizacao
6. sincronizar usuario e ACL por node com `adduser`, `deleteuser` e `adddeviceuser`
7. usar `getcookie` para relay/noVNC
8. limpar o device no MeshCentral durante uninstall

## Plano de integracao para outro projeto

## Objetivo recomendado

Se voce quer reproduzir esse desenho em outro produto, a meta correta e esta:

1. usar MeshCentral como subsistema de acesso remoto e relay
2. centralizar identidade e autorizacao no seu proprio backend
3. espelhar apenas o necessario no MeshCentral
4. nunca delegar ao browser a geracao de tokens ou a administracao do MeshCentral

## Fase 1: MVP funcional

### Passo 1. Suba o MeshCentral com as opcoes certas

No minimo:

```json
{
  "settings": {
    "allowLoginToken": true,
    "allowFraming": true,
    "WANonly": true
  }
}
```

Se houver proxy reverso/TLS offload, configure isso corretamente.

### Passo 2. Crie uma conta tecnica admin do MeshCentral

Essa conta sera usada pelo backend para:

1. abrir `control.ashx`
2. listar groups e users
3. criar usuarios
4. ajustar ACLs
5. remover devices

Guarde o username sempre em lowercase.

### Passo 3. Gere e armazene a `login token key`

Execute:

```text
node node_modules/meshcentral --logintokenkey
```

Guarde essa chave em um secret store, nao no frontend. Quem possui essa chave pode gerar login tokens para qualquer usuario do MeshCentral.

### Passo 4. Modele a configuracao no seu backend

Crie algo equivalente a:

1. `mesh_site`
2. `mesh_ws_url` interno ou derivavel
3. `mesh_admin_username`
4. `mesh_token_key`
5. `mesh_device_group`
6. `sync_mesh_with_app`

### Passo 5. Crie o device group padrao

Voce pode criar usando CLI no bootstrap ou `createmesh` pelo WebSocket.

Recomendacao pratica:

1. use CLI no bootstrap inicial
2. use WebSocket para validacoes e automacao de runtime

### Passo 6. Implemente um cliente MeshCentral no backend

Crie um modulo parecido com `MeshSync` contendo:

1. `get_auth_token()`
2. `connect_control_socket()`
3. `mesh_action(payload)`
4. `list_meshes()`
5. `list_users()`
6. `add_user()`
7. `edit_user()`
8. `delete_user()`
9. `grant_node_access()`
10. `revoke_node_access()`
11. `get_cookie()`
12. `remove_devices()`

### Passo 7. Armazene `mesh_node_id` no seu inventario

Sem isso a integracao fica incompleta.

Seu agent, instalador ou processo de onboarding precisa reportar o node id de volta para sua API.

## Fase 2: distribuicao de agent

### Passo 8. Resolva o `mesh_device_id` pelo nome do group

Fluxo recomendado:

1. abra `control.ashx`
2. envie `meshes`
3. procure o nome do grupo
4. extraia `_id`

### Passo 9. Gere URLs de download do MeshAgent por plataforma

Implemente as mesmas duas estrategias:

#### Windows

```text
/meshagents?id=<ident>&meshid=<group-id>&installflags=0
```

#### Linux e macOS

```text
/meshagents?id=<group-id>&installflags=2&meshinstall=<ident>
```

### Passo 10. Integre isso ao seu fluxo de onboarding

O padrao mais robusto e:

1. instalar MeshAgent
2. ler `mesh_node_id`
3. instalar seu agent principal
4. enviar `mesh_node_id` para sua API

## Fase 3: autorizacao por usuario

### Passo 11. Defina um `mesh_username` tecnico e estavel

Nao use email como chave principal.

Recomendacao:

```text
<username-normalizado>___<user-id-interno>
```

### Passo 12. Modele o sync como reconciliacao de estado

Nao faca atualizacoes incrementais soltas. Faca diff entre:

1. estado desejado na sua aplicacao
2. estado atual no MeshCentral

Depois aplique:

1. usuarios novos
2. usuarios removidos
3. links user-node para adicionar
4. links user-node para remover
5. display names divergentes

### Passo 13. Mapeie as regras de autorizacao do seu dominio

No TacticalRMM, a permissao final depende de:

1. permissao global `can_use_mesh`
2. escopo do usuario sobre o agent especifico

No seu projeto, voce deve produzir o mesmo resultado logico: um conjunto de usuarios autorizados por device.

### Passo 14. Escolha o mask de rights por device

Se voce quiser reproduzir o comportamento do TacticalRMM, use `4088024`.

Se quiser uma superficie menor, reduza o rights mask e valide cada bit no MeshCentral antes de entrar em producao.

## Fase 4: UX de sessao remota

### Passo 15. Gere links por usuario, nunca no browser

Seu backend deve:

1. validar se o usuario pode acessar o device
2. gerar `login token`
3. devolver URLs prontas de control/terminal/files

### Passo 16. Se precisar noVNC, use `getcookie`

Fluxo recomendado:

1. backend chama `getcookie`
2. backend monta URL do `meshrelay.ashx`
3. frontend apenas abre a URL ja pronta

## Fase 5: operacao e resiliencia

### Passo 17. Implemente uninstall completo

No minimo:

1. desinstalar seu agent
2. remover o device no MeshCentral
3. disparar novo sync de ACL

### Passo 18. Planeje backup e restore desde o inicio

Voce precisa versionar e proteger:

1. banco do MeshCentral
2. `meshcentral-data/config.json`
3. gravações, se usar
4. login token key
5. device groups e contas admin

### Passo 19. Teste os 3 cenarios de topologia

O TacticalRMM suporta:

1. Mesh local no mesmo host
2. Mesh em container interno
3. Mesh externo

Se o seu projeto puder precisar de mais de uma topologia, abstraia isso cedo.

## Recomposicao arquitetural recomendada para outro projeto

Eu recomendo quebrar a implementacao em 6 servicos internos:

1. `MeshCentralConfigService`
   - resolve URLs publicas e internas
   - resolve topologia local, Docker ou externa

2. `MeshCentralTokenService`
   - gera auth token para `control.ashx`
   - gera login token para browser

3. `MeshCentralClient`
   - encapsula o WebSocket administrativo
   - expoe comandos tipados

4. `MeshIdentityMapper`
   - traduz usuario interno em `mesh_username`, `mesh_user_id`, display name, email tecnico

5. `MeshAclSyncService`
   - calcula diff entre estado interno e estado MeshCentral
   - aplica `adduser`, `deleteuser`, `adddeviceuser`

6. `MeshAgentEnrollmentService`
   - resolve group id
   - gera URLs/instaladores do MeshAgent
   - salva `mesh_node_id`

Com isso, seu produto nao fica acoplado ao detalhe bruto do `control.ashx` em toda parte.

## Riscos e armadilhas ao replicar esse modelo

1. `MESH_TOKEN_KEY` exposta fora do backend compromete todo o ambiente MeshCentral.
2. Se o nome do device group nao bater exatamente, `get_mesh_device_id` falha.
3. Se o sync for destrutivo e voce nao tiver uma fonte da verdade confiavel, pode remover acessos validos.
4. Se voce usar email real como identidade MeshCentral, colisao de conta pode virar problema operacional.
5. Se nao armazenar `mesh_node_id`, o resto da integracao fica quebrado.
6. Se misturar URL publica e URL interna do MeshCentral, downloads e WebSocket vao falhar de modo intermitente.
7. Se usar compressao sem validar compatibilidade do seu ambiente, pode ter problema de WebSocket e relay.
8. Se o MeshCentral for externo, o seu backend precisa usar `wss` e `https`, nao `ws` e `http` locais.
9. Se voce quiser embutir a UI em iframe, `allowFraming` precisa estar habilitado e isso precisa ser avaliado do ponto de vista de seguranca.

## Minha recomendacao pratica de implementacao

Se eu fosse construir essa integracao em outro produto hoje, faria nesta ordem:

1. subir o MeshCentral e validar `control.ashx`
2. gerar e armazenar a login token key
3. criar um cliente WebSocket minimo com `meshes`, `users`, `adduser`, `adddeviceuser`
4. modelar `mesh_node_id` e `mesh_username` no dominio da aplicacao
5. entregar onboarding de agent com captura de node id
6. entregar links remotos via login token
7. entregar sync de usuarios e ACL por device
8. entregar relay/noVNC
9. entregar uninstall, backup, restore e update

Essa ordem reduz risco e te da valor cedo, sem pular os fundamentos.

## Checklist de validacao para o outro projeto

Antes de ir para producao, valide:

1. o backend consegue abrir `control.ashx` com auth token
2. o backend consegue listar device groups
3. o backend consegue criar e remover usuario MeshCentral
4. o backend consegue conceder e revogar acesso por node
5. o browser consegue abrir control, terminal e files via login token
6. o onboarding salva `mesh_node_id`
7. o uninstall remove o device do MeshCentral
8. o backup restaura banco e config do MeshCentral
9. o sync nao remove usuarios indevidos
10. a topologia escolhida funciona tanto para URL publica quanto para URL interna

## Conclusao

O TacticalRMM integra com MeshCentral de forma profunda, nao superficial. O MeshCentral funciona como:

1. plano de acesso remoto
2. canal de relay
3. distribuidor de MeshAgent
4. banco de users/device links remoto
5. mecanismo de recuperacao do agent principal

O padrao correto para outro projeto e copiar a arquitetura, nao apenas copiar as URLs:

1. backend como dono dos tokens
2. `mesh_node_id` como chave tecnica
3. sua aplicacao como fonte da verdade para autorizacao
4. reconciliacao automatica de usuarios e ACLs
5. preocupacao operacional com bootstrap, backup, restore e update

Se voce seguir esse desenho, voce nao estara apenas "integrando com MeshCentral". Voce estara construindo um subsistema remoto consistente e controlado, como o TacticalRMM faz.