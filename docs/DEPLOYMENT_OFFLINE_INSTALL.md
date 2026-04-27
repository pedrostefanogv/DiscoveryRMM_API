# Deployment and offline install

Este guia cobre a instalacao inicial do Discovery RMM em Linux (Ubuntu 22.04/24.04), incluindo API + portal web, com PostgreSQL + NATS no mesmo host, escolha de acesso interno/externo e self-update do servidor.

## Suporte de arquitetura (fase atual)

- Servidor/API + portal: Linux `x64` e Linux `arm64`.
- O instalador detecta a arquitetura e define automaticamente `DISCOVERY_DOTNET_RUNTIME` (`linux-x64` ou `linux-arm64`).
- Override manual continua disponivel via `DISCOVERY_DOTNET_RUNTIME`.
- Build/distribuicao do Agent permanece Windows (`x86/x64`) nesta fase.

## Scripts adicionados

- `scripts/linux/install_discovery_server.sh`
- `scripts/linux/selfupdate_discovery_api.sh`
- `scripts/linux/discovery-install.env.example`

## Regras de seguranca adotadas

1. Se o instalador for executado como root, ele reexecuta sob um usuario comum com sudo.
2. O fluxo exige usuario comum com sudo.
3. Secrets nao sao impressos no terminal.
4. O token GitHub usado para bootstrap deve ser temporario e rotacionado apos a instalacao.

## Modo interativo

```bash
chmod +x scripts/linux/install_discovery_server.sh scripts/linux/selfupdate_discovery_api.sh
./scripts/linux/install_discovery_server.sh
```

Ao iniciar, o wizard pergunta qual operacao executar:
- `1` Instalacao completa
- `2` Atualizar somente configuracao do NATS

O wizard pergunta:
- repositorio da API, do Agent e do portal web
- branch
- modo de acesso (`internal`, `external`, `hybrid`)
- IP/hostname interno (quando aplicavel)
- hostname externo + token do Cloudflare Tunnel (quando aplicavel)
- credenciais de PostgreSQL e NATS
- usuario/senha de bypass da API no NATS (`auth_users`)
- habilitacao de `auth_callout` e issuer publico do account (quando habilitado)

## NATS em rede + autenticacao

O instalador agora suporta:
- bind de cliente NATS na rede (`NATS_BIND_HOST`, padrao `0.0.0.0`)
- monitor HTTP em host separado (`NATS_MONITOR_HOST`, padrao `127.0.0.1`)
- usuario de bypass da API (`NATS_AUTH_USER` / `NATS_AUTH_PASSWORD`)
- `auth_callout` no NATS para validar tokens de Agent/User no handshake (`NATS_AUTH_CALLOUT_ENABLED=1`)

Quando `NATS_AUTH_CALLOUT_ENABLED=1`, e obrigatorio informar `NATS_AUTH_CALLOUT_ISSUER` (account public key) e o subject default e `\$SYS.REQ.USER.AUTH`.

O instalador grava em `/etc/discovery-api/discovery.env`:
- `Nats__AuthUser`
- `Nats__AuthPassword`
- `Nats__AuthCallout__Enabled`
- `Nats__AuthCallout__Subject`

## Modo nao interativo

1. Copie o arquivo exemplo:

```bash
cp scripts/linux/discovery-install.env.example ./discovery-install.env
chmod 600 ./discovery-install.env
```

2. Preencha os valores obrigatorios.

3. Execute:

```bash
./scripts/linux/install_discovery_server.sh --config ./discovery-install.env --non-interactive
```

## Atualizar apenas configuracao do NATS

Para atualizar somente o NATS (incluindo `auth_callout`/issuer) sem executar clone/build da API:

```bash
./scripts/linux/install_discovery_server.sh --update-nats-config
```

Opcionalmente, voce pode forcar o modo por argumento:

```bash
./scripts/linux/install_discovery_server.sh --mode nats
./scripts/linux/install_discovery_server.sh --mode full
```

Com arquivo de configuracao:

```bash
./scripts/linux/install_discovery_server.sh --update-nats-config --config ./discovery-install.env --non-interactive
```

Esse modo:
- atualiza `nats-server.conf`
- solicita o `NATS_AUTH_CALLOUT_ISSUER` quando `NATS_AUTH_CALLOUT_ENABLED=1`
- atualiza somente as variaveis `Nats__*` em `/etc/discovery-api/discovery.env`
- reinicia `nats-server` e, se ativo, `discovery-api`

## Estrutura de diretorios provisionada

- API: `/opt/discovery-api`
- Source da API para self-update: `/opt/discovery-api/source`
- Releases da API: `/opt/discovery-api/releases`
- Link ativo da API: `/opt/discovery-api/current`
- Shared da API: `/opt/discovery-api/shared`
- Site: `/opt/discovery-site`
- Source do Site para self-update: `/opt/discovery-site/source`
- Releases do Site: `/opt/discovery-site/releases`
- Link ativo do Site: `/opt/discovery-site/current`
- Source do Agent: `/opt/discovery-agent-src`
- Artefatos do Agent: `/opt/discovery-agent-artifacts`
- Operacao (scripts/locks): `/opt/discovery-ops`

## Publicacao do portal web

O instalador agora:
- clona o repositiorio `DiscoveryRMM_Site`
- instala Node.js suportado pelo Vite atual
- executa `npm ci` + `npm run build`
- publica a SPA em releases versionadas
- configura Nginx para servir o portal e fazer proxy de `/api`, `/hubs`, `/health`, `/openapi` e `/scalar` para a API local

Por padrao, o portal usa a mesma origem do Nginx (`DISCOVERY_SITE_API_URL=""`) e sobe com realtime em `signalr`, evitando depender de NATS/WebSocket no browser durante a instalacao inicial.

## Self-update do servidor

O servico systemd da API executa `ExecStartPre=/opt/discovery-ops/selfupdate-discovery-api.sh`.

Fluxo de update:
1. Busca atualizacoes da API e do portal web no branch configurado.
2. Se houver novo commit na API, publica nova release com `dotnet publish`.
3. Se houver novo commit no portal web, executa novo build Vite e publica nova release estatica.
4. Troca os links simbolicos `current` de forma atomica.
5. Mantem apenas as ultimas releases (default: 5).

Variaveis de update ficam em `/etc/discovery-api/discovery.env`.
O token GitHub fica em `/etc/discovery-api/github.token`.

Quando `DISCOVERY_DOTNET_RUNTIME` nao e informado, o instalador/self-update detecta automaticamente `linux-x64` ou `linux-arm64` com base na arquitetura do host.

## Servicos esperados

- `postgresql`
- `nats-server`
- `discovery-api`
- `nginx`
- `cloudflared` (somente modo `external` ou `hybrid`)

## PostgreSQL para IA (embeddings)

O backend usa `pgvector` para embeddings (`vector(1536)`).

O instalador garante:
- instalacao do pacote `postgresql-<major>-pgvector`
- execucao de `CREATE EXTENSION IF NOT EXISTS vector;` no database `discovery`

## Verificacao rapida

```bash
sudo systemctl status discovery-api --no-pager
sudo systemctl status nginx --no-pager
sudo systemctl status nats-server --no-pager
sudo systemctl status postgresql --no-pager
curl -k https://127.0.0.1/
curl -k https://127.0.0.1/openapi/v1.json
```

Se o OpenAPI estiver desativado (`OPENAPI_ENABLED=0`), substitua a ultima verificacao por uma checagem de socket/local bind da API:

```bash
sudo ss -ltnp '( sport = :8080 )'
```

## Observacao sobre Cloudflare Tunnel

Quando usar `ACCESS_MODE=external` ou `hybrid`, o tunnel deve apontar para o proxy local que publica o portal/API. O instalador passa a disponibilizar o portal na mesma origem web e mantem a API atras do Nginx.

## Observacao importante

No primeiro ciclo, o setup utiliza PAT para clone e update de repositorios privados. Para producao, recomenda-se migrar para mecanismo de acesso mais restritivo (deploy key read-only, mirror interno ou token de curta duracao com rotacao automatizada).
