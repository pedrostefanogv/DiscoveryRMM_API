# Deployment and offline install

Este guia cobre a instalacao inicial do servidor Discovery API em Linux (Ubuntu 22.04/24.04), com PostgreSQL + NATS no mesmo host, escolha de acesso interno/externo e self-update do servidor.

## Scripts adicionados

- `scripts/linux/install_discovery_server.sh`
- `scripts/linux/selfupdate_discovery_api.sh`
- `scripts/linux/discovery-install.env.example`

## Regras de seguranca adotadas

1. O instalador falha se for executado como root.
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
- repositorio da API e do Agent
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
- Source do Agent: `/opt/discovery-agent-src`
- Artefatos do Agent: `/opt/discovery-agent-artifacts`
- Operacao (scripts/locks): `/opt/discovery-ops`

## Self-update do servidor

O servico systemd da API executa `ExecStartPre=/opt/discovery-ops/selfupdate-discovery-api.sh`.

Fluxo de update:
1. Busca atualizacoes no branch configurado.
2. Se houver novo commit, publica nova release com `dotnet publish`.
3. Troca o link simbolico `current` de forma atomica.
4. Mantem apenas as ultimas releases (default: 5).

Variaveis de update ficam em `/etc/discovery-api/discovery.env`.
O token GitHub fica em `/etc/discovery-api/github.token`.

## Servicos esperados

- `postgresql`
- `nats-server`
- `discovery-api`
- `cloudflared` (somente modo `external` ou `hybrid`)

## PostgreSQL para IA (embeddings)

O backend usa `pgvector` para embeddings (`vector(1536)`).

O instalador garante:
- instalacao do pacote `postgresql-<major>-pgvector`
- execucao de `CREATE EXTENSION IF NOT EXISTS vector;` no database `discovery`

## Verificacao rapida

```bash
sudo systemctl status discovery-api --no-pager
sudo systemctl status nats-server --no-pager
sudo systemctl status postgresql --no-pager
curl -k https://127.0.0.1:8443/health
```

## Observacao importante

No primeiro ciclo, o setup utiliza PAT para clone e update de repositorios privados. Para producao, recomenda-se migrar para mecanismo de acesso mais restritivo (deploy key read-only, mirror interno ou token de curta duracao com rotacao automatizada).
