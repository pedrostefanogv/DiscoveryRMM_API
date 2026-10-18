# Autorização do usuário para acesso remoto (desktop)

## Objetivo
Sessões de **desktop** (`screen`/`all`) passam a pedir autorização ao usuário do
computador acessado, com contagem regressiva e ação automática configurável. O
banner local mostra operador/tempo/desconectar e não aparece no stream.

Terminal, arquivos, proxy e processos **não** pedem autorização.

## Modelo de política
Herança: **Servidor → Cliente → Site → Grupos → Usuário**.

Campos: `RemoteConsentMode` (Off/NotifyOnly/Prompt), `RemoteConsentTimeoutSeconds`
(10–600), `RemoteConsentTimeoutAction` (Deny/Approve), `RemoteConsentShowBanner`,
`RemoteConsentMessage`, `RemoteConsentBannerText`.

Overrides de usuário/grupo ficam em `remote_consent_policy_overrides` e podem ser
escopados por cliente/site (a página do console expõe cliente e site opcionais).
`LockedFieldsJson` bloqueia níveis inferiores, inclusive overrides. Ao excluir
um usuário ou grupo, os overrides correspondentes são removidos.

**Rollout:** instalações existentes recebem a coluna em **Off** (comportamento
atual preservado). Instalações novas e o default de fábrica da entidade são
**Prompt / 60s / Deny / banner ligado**. Ligue por cliente/site após atualizar o
parque de agentes para a versão mínima.

## Fluxo
1. `POST /remote-sessions/{agentId}` resolve a política.
2. `Prompt` cria sessão `pending_consent` (deadline) e envia `consent` + `banner`
   no start.
3. Agente mostra prompt (PSADT; fallback nativo) e reporta em
   `POST /agent-auth/me/remote-sessions/{id}/consent`.
4. Servidor marca `active`/`denied`; console faz polling em
   `GET /remote-sessions/{agentId}/{sessionId}/consent` e só então conecta o viewer.
5. Sweeper nega pending vencido (`DecisionGraceSeconds`, default 30s).
6. Botão Desconectar do banner encerra a sessão e reporta
   `POST /agent-auth/me/remote-sessions/{id}/stop`.

## Segurança
- Política decidida no servidor; `debug_config.json` do agente não desliga.
- Agente antigo: `409 agent_update_required` (versão mínima em
  `RemoteAccess:Consent:MinimumAgentVersion`, default 1.3.0).
- Credenciais NATS recusadas enquanto `pending_consent`.
- Decisão tardia após sweeper retorna 409 e o agente não inicia.
- Auditoria em `remote_session_audits` e `configuration_audits`.

## Endpoints administrativos
- `GET /remote-consent/effective?agentId=&userId=`
- `GET/POST/PUT/DELETE /remote-consent/overrides`
- Permissões: `Users.View`/`Users.Edit`.
