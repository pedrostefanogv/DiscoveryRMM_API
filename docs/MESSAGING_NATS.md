# Messaging and NATS

Consolidated from:
- `NATS_AUTH_CALLOUT_SETUP.md`
- `NATS_AGENT_AUTHENTICATION_IMPLEMENTATION_PLAN.md`

## Current status

- NATS auth callout support is implemented in the API.
- Server-side helpers for NATS key generation, connection tests, and NATS settings patching are implemented.
- Agents can request scoped NATS credentials through the authenticated API.
- Canonical tenant/site/agent subject naming is defined, but the full multi-tenant rollout is still in progress.

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
- Scoped subjects and legacy subjects can temporarily coexist during migration.

## Canonical subject model

- `tenant.{clientId}.site.{siteId}.agent.{agentId}.command`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.heartbeat`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.result`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.hardware`
- `tenant.{clientId}.site.{siteId}.agent.{agentId}.sync.ping`

## Operational notes

- Agent credentials should be short lived and renewed before expiry.
- TLS pinning support already exists for both the API endpoint and NATS WSS when enabled.
- Legacy subjects should only remain enabled during migration windows.

## Roadmap

- Complete strict broker ACL enforcement for all agent traffic.
- Finish migration away from legacy subjects.
- Expand negative tests and audit coverage for cross-tenant access attempts.
