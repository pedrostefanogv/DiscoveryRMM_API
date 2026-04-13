# P2P distribution and bootstrap

Consolidated from:
- current P2P API surfaces in the backend

## Current status

- Cloud bootstrap for peer discovery is implemented.
- Seed planning, swarm telemetry ingestion, and distribution status endpoints are implemented.
- P2P capability is already tied into hierarchical configuration and deploy/bootstrap flows.

## Main API surfaces

| Endpoint | Purpose |
| --- | --- |
| `POST /api/agent-auth/me/p2p/bootstrap` | Registers the authenticated agent in cloud bootstrap and returns up to 3 online peers from the same client scope. |
| `GET /api/agent-auth/me/p2p-seed-plan` | Returns the recommended seeder count for the agent site. |
| `POST /api/agent-auth/me/p2p-telemetry` | Accepts P2P health/distribution telemetry from the agent. |
| `GET /api/agent-auth/me/p2p-distribution-status` | Returns artifact-level distribution visibility based on recent telemetry. |
| `POST /api/agent-auth/me/zero-touch/deploy-token` | Issues the short-lived deploy token used in zero-touch/bootstrap scenarios. |

## Configuration dependencies

- `P2PFilesEnabled` controls file-distribution behavior.
- `CloudBootstrapEnabled` controls cloud peer discovery/bootstrap.
- `AgentOnlineGraceSeconds` is used when deciding whether a peer is considered online.

These values flow to the agent through `GET /api/agent-auth/me/configuration`.

## Operational rules

- Bootstrap requires authenticated agent context and is blocked when cloud bootstrap is disabled for the resolved scope.
- Bootstrap payload must include `agentId`, `peerId`, `addrs`, and `port`.
- Telemetry is rate limited per agent.
- Distribution status is derived from recent telemetry and is intended for operational visibility, not authoritative delivery accounting.

## Related docs

- `DEPLOYMENT_OFFLINE_INSTALL.md` for zero-touch deploy tokens and package delivery.
- `CONFIGURATION.md` for the flags that enable or disable P2P behavior.

## Roadmap

- Keep expanding operational observability before adding more complex orchestration on top of the existing bootstrap/telemetry surface.
