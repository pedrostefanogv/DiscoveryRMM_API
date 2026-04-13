# Deployment and offline install

Consolidated from:
- backend deployment/package surfaces that were previously spread across feature plans

## Current status

- Deploy tokens are implemented and can be delivered as a raw token or directly as an installer artifact.
- The backend can generate a portable ZIP package or a native installer from a previously issued deploy token.
- Agent self-registration through deploy token is implemented.
- Zero-touch deploy tokens are implemented for discovery/bootstrap scenarios.

## Administrative API

| Endpoint | Purpose |
| --- | --- |
| `GET /api/deploy-tokens?clientId={id}&siteId={id}` | Lists deploy tokens for a client/site. |
| `POST /api/deploy-tokens` | Creates a deploy token and returns the raw token. When `delivery = installer`, returns the installer binary immediately. |
| `POST /api/deploy-tokens/{id}/download` | Rebuilds a portable ZIP or installer from the original raw token without consuming it. |
| `POST /api/deploy-tokens/{id}/meshcentral-install` | Returns MeshCentral install instructions for the token scope. |
| `POST /api/deploy-tokens/prebuild` | Prebuilds the base agent binary. |
| `POST /api/deploy-tokens/{id}/revoke` | Revokes a deploy token. |

## Agent installation API

| Endpoint | Purpose |
| --- | --- |
| `POST /api/agent-install/register` | Registers an agent using a deploy token passed in `Authorization: Bearer {deployToken}`. |
| `POST /api/agent-install/{agentId}/token` | Issues an install token for an existing agent, still scoped by the deploy token used in the request. |
| `POST /api/agent-auth/me/zero-touch/deploy-token` | Issues a short-lived zero-touch token from an already authenticated agent. |

## Delivery modes

- Raw deploy token for scripted or custom installers.
- Portable package (`portable` artifact) for offline distribution.
- Native installer (`installer` artifact) when server-side installer resources are available.
- MeshCentral install instructions as an alternative provisioning path for support-enabled scopes.

## Important rules

- The raw deploy token is returned only when it is created; keep it securely if later downloads are required.
- `MultiUse` controls whether the created deploy token can be consumed more than once.
- Zero-touch deploy tokens are intentionally short lived and tied to discovery scenarios.
- When a zero-touch token is used during registration, the created agent is marked as pending until the bootstrap flow completes.
- Server-side package creation can return `503` when installer resources or package configuration are missing.

## Related docs

- `MESHCENTRAL.md` for support install instructions and remote support provisioning.
- `P2P.md` for cloud bootstrap and swarm-related behavior.
