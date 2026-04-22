# Messaging and NATS

Consolidated from:
- `NATS_AUTH_CALLOUT_SETUP.md`
- `NATS_AGENT_AUTHENTICATION_IMPLEMENTATION_PLAN.md`

## Current status

- NATS auth callout support is implemented in the API.
- Server-side helpers for NATS key generation, connection tests, and NATS settings patching are implemented.
- Agents can request scoped NATS credentials through the authenticated API.
- Canonical tenant/site/agent subject naming is operational for command, heartbeat, result, hardware, sync.ping and remote debug logs.
- Remote debug uses NATS as the preferred transport, but the backend keeps SignalR fallback enabled for command delivery and log ingress when direct broker communication is not available.

## Main API surfaces

| Endpoint | Purpose |
| --- | --- |
| `POST /api/configurations/server/nats/generate-account-key` | Generates account seed/public key and optional XKey material. |
| `POST /api/configurations/server/nats/test` | Validates NATS connectivity before saving settings. |
| `PATCH /api/configurations/server/nats` | Updates NATS-related server settings such as auth enablement, scoped subjects, and JWT TTLs. |
| `POST /api/agent-auth/me/nats-credentials` | Issues NATS credentials for the authenticated agent. |
| `GET /api/agent-auth/me/configuration` | Exposes NATS host and TLS hash information needed by the agent. |
| `POST /api/agent-auth/me/tls-mismatch` | Lets the agent invalidate cached TLS fingerprints and request a fresh probe. |

## Auth callout baseline

- NATS uses auth callout on `$SYS.REQ.USER.AUTH`.
- The API must connect with a bypass user listed in `auth_users`.
- Account seed and optional XKey seed are stored in server configuration, not in source code.
- Agent traffic now uses only canonical tenant/site/agent subjects in the backend.

## Canonical subject model

- `tenant.{clientId}.site.{siteId}.agent.{agentId}.command`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.result`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.remote-debug.log`

## Operational notes

- Agent credentials should be short lived and renewed before expiry.
- TLS pinning support already exists for both the API endpoint and NATS WSS when enabled.
- Public configuration no longer exposes the legacy scoped-subject toggle.
- `POST /api/agents/{id}/remote-debug/start` advertises `preferredTransport=nats` and `fallbackTransport=signalr`, together with the tenant-scoped subject and the SignalR method name accepted by the authenticated agent connection.
- Remote debug logs arriving from NATS or SignalR are normalized by the same backend relay before being broadcast to the user-facing session group.

## Roadmap

- Complete strict broker ACL enforcement for all agent traffic.
- Expand negative tests and audit coverage for cross-tenant access attempts.
