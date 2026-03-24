# NATS Auth Callout - Configuracao e Integracao

## Objetivo
Habilitar autenticacao centralizada no NATS via Auth Callout, delegando a autorizacao para a API Meduza. Isso garante isolamento entre agents, clientes e sites no mesmo broker.

## Como Funciona (Arquitetura)

> **NATS NAO chama a API via HTTP.**  
> A comunicacao acontece inteiramente dentro do proprio NATS:

```
Cliente (agent/usuario)
    |
    | connect(auth_token=mdz_... ou JWT)
    v
NATS Server
    |
    | publica request JWT no subject $SYS.REQ.USER.AUTH
    v
API Meduza (esta conectada ao NATS como subscriber)
    |
    | valida token, gera User JWT com permissoes restritas
    | responde no msg.ReplyTo (via NATS)
    v
NATS Server
    |
    | aceita ou rejeita a conexao do cliente
```

**Portanto nao ha endereco de API no nats-server.conf.** O servidor NATS apenas precisa saber:
- Quem e o **issuer** (ACCOUNT_PUBLIC_KEY) para assinar as respostas
- Quais usuarios estao em **auth_users** (isentos do callout — entre eles, o proprio usuario da API)

## Problema Chicken-and-Egg

Quando o auth callout esta habilitado, **toda conexao nova ao NATS passa pelo callout** — inclusive a da propria API. Para evitar isso, o usuario com o qual a API conecta (ex: `auth`) deve estar na lista `auth_users` do nats-server.conf. Isso o isenta do callout e permite que a API assine e responda requisicoes.

**A API precisa conectar com esse usuario.** Configure em appsettings.json:
```json
"Nats": {
  "Url": "nats://<host>:4222",
  "AuthUser": "auth",
  "AuthPassword": "auth",
  "AuthCallout": {
    "Enabled": true,
    "Subject": "$SYS.REQ.USER.AUTH"
  }
}
```

Em producao, troque `auth`/`auth` por credenciais fortes.

## Requisitos
- NATS Server v2.10+ com auth_callout habilitado.
- API Meduza rodando com o servico NATS Auth Callout habilitado.
- Seed da conta NATS configurado em ServerConfiguration (NatsAccountSeed).

## Configuracao do NATS Server (exemplo)

### Exemplo completo (nats-server.conf)

Use este modelo como base e ajuste os valores entre <>. Este exemplo cria tres contas:
- AUTH: usada apenas para o servico de auth callout.
- APP: conta da aplicacao (Meduza).
- SYS: conta do sistema do NATS.

```
port: 4222

accounts {
  AUTH: {
    users: [ { user: auth, password: <SENHA_FORTE> } ]
  }
  APP: {}
  SYS: {}
}

system_account: SYS

authorization {
  auth_callout {
    issuer: <ACCOUNT_PUBLIC_KEY>
    auth_users: [ auth ]
    account: AUTH
    # opcional: cifra os requests/responses do auth callout
    # xkey: <AUTH_XKEY_PUBLIC>
  }
}

# Opcional: expose monitor/metrics apenas internamente
# http: 8222
```

A API Meduza conecta com `user: auth, password: <SENHA_FORTE>` e assina `$SYS.REQ.USER.AUTH` a partir da conta AUTH. Como `auth` esta em `auth_users`, a conexao da API bypassa o callout.

### Passo a passo rapido

1) Gere a conta do NATS (issuer) via nsc:
```
nsc generate nkey --account
```
- O comando retorna duas chaves:
  - ACCOUNT_SEED (privada, deve ficar na API Meduza)
  - ACCOUNT_PUBLIC_KEY (publica, usada no nats-server.conf)

2) Configure o `ACCOUNT_PUBLIC_KEY` no nats-server.conf no campo `issuer`.

3) Salve o `ACCOUNT_SEED` no ServerConfiguration da API Meduza:
- `NatsAuthEnabled = true`
- `NatsAccountSeed = <ACCOUNT_SEED>`
- `NatsUseScopedSubjects = true`
- `NatsIncludeLegacySubjects = true` (temporario)

4) Configure as credenciais da API no appsettings.json:
- `Nats:AuthUser = auth`
- `Nats:AuthPassword = <SENHA_FORTE>`
- `Nats:AuthCallout:Enabled = true`

5) Configure a mesma senha no nats-server.conf dentro de `accounts.AUTH.users`.

6) Reinicie o NATS Server e a API Meduza.

7) Valide a autenticacao:
- Agent conecta no NATS usando `auth_token` com `mdz_...`
- Usuario conecta no NATS usando `auth_token` com JWT da API

Notas:
- Use o `ACCOUNT_PUBLIC_KEY` gerado pelo `nsc generate nkey --account`.
- O seed (private key) deve ser salvo no ServerConfiguration como `NatsAccountSeed`.

### Opcional: habilitar xkey (criptografia do callout)

Para evitar que o JWT do request/response trafegue em claro no NATS, habilite `xkey`. Com xkey, o payload do auth callout e encriptado com curve25519 (NaCl box). **Ja implementado.**

**Como funciona:**
1. A API gera um par de chaves xkey (curve25519). A chave publica vai no `nats-server.conf`, a privada (seed) fica no `ServerConfiguration`.
2. O NATS server gera um par efemero por request, encripta o payload com a chave publica da API, e envia a chave publica efemera no header `Nats-Server-Xkey`.
3. A API decripta via DH: `myXKeyPair.Open(encryptedPayload, serverEphemeralPublicKey)`.
4. A API encripta a resposta de volta: `myXKeyPair.Seal(responseBytes, serverEphemeralPublicKey)`.

**Passo a passo:**

1) Gere um par xkey (curve25519):
```
nsc generate nkey --curve
```
- Retorna `CURVE_SEED` (privado) e `CURVE_PUBLIC_KEY` (publico).

2) Configure a chave publica no nats-server.conf:
```
authorization {
  auth_callout {
    issuer: <ACCOUNT_PUBLIC_KEY>
    auth_users: [ auth ]
    account: AUTH
    xkey: <CURVE_PUBLIC_KEY>
  }
}
```

3) Salve o `CURVE_SEED` no ServerConfiguration da API Meduza:
- `NatsXKeySeed = <CURVE_SEED>` (via API /api/configurations/server)
- O seed e automaticamente encriptado em repouso.

4) Reinicie o NATS Server e a API Meduza.

Notas:
- Sem `NatsXKeySeed` configurado, o callout funciona normalmente em texto claro.
- Com `NatsXKeySeed`, a API automaticamente detecta o header `Nats-Server-Xkey` e ativa o modo encriptado.
- `NatsXKeySeed` e redacted nas respostas da API de configuracao (nunca exposto).

## Configuracao da API Meduza

`appsettings.json`:
```json
"Nats": {
  "Url": "nats://<host>:4222",
  "AuthUser": "auth",
  "AuthPassword": "<SENHA_FORTE>",
  "AuthCallout": {
    "Enabled": true,
    "Subject": "$SYS.REQ.USER.AUTH"
  }
}
```

No ServerConfiguration (via API /api/configurations/server):
- NatsAuthEnabled = true
- NatsAccountSeed = <ACCOUNT_SEED>
- NatsUseScopedSubjects = true (recomendado)
- NatsIncludeLegacySubjects = true (temporario)

## Fluxo de Autenticacao

### Agents
- O agent conecta no NATS usando `auth_token` com o token `mdz_...`.
- A API valida o token via `IAgentAuthService`.
- O JWT NATS emitido permite apenas:
  - publish: tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat
  - publish: tenant.{clientId}.site.{siteId}.agent.{agentId}.result
  - publish: tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware
  - subscribe: tenant.{clientId}.site.{siteId}.agent.{agentId}.command
  - subscribe: tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping

### Usuarios (Dashboard)
- O usuario conecta no NATS usando `auth_token` com o JWT da API.
- A API valida o JWT e aplica escopo via IPermissionService.
- O JWT NATS emitido permite subscribe apenas nos subjects de dashboard
  dentro do escopo (client/site) permitido.

## Observacoes
- O subject default do auth callout e `$SYS.REQ.USER.AUTH`.
- Para migracao, habilite `NatsIncludeLegacySubjects` ate todos os agents migrarem.
- Troque as credenciais do account AUTH em producao.
- xkey (criptografia curve25519 do payload do callout) esta implementado. Configure `NatsXKeySeed` no ServerConfiguration para ativar.
