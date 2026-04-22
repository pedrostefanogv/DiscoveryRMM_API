# MeshCentral

Consolidated from:
- `docs\Meshcentral\PLANO_ACAO_MESHCENTRAL.md`
- MeshCentral-specific sections that were duplicated in auth/deploy docs

## Current status

- Embed URL generation is implemented for authenticated users and agents using server-side login token generation.
- Identity sync between Discovery users and MeshCentral accounts is implemented, including backfill/reconcile support.
- Group policy reconciliation and rights profile management are implemented.
- Node-level ACL reconciliation is implemented for agents with persisted `meshcentral_node_id`.
- Node-link backfill is available to suggest or persist `meshcentral_node_id` for existing agents based on hostname matching inside the site mesh.
- Install instructions are available through deploy-token and authenticated-agent flows.
- Agents can now persist `meshcentral_node_id`, and embed flows prefer the persisted node when available.
- Health diagnostics are available to validate control websocket connectivity and inspect optional site/agent linkage.

## Main API surfaces

| Area | Endpoints | Notes |
| --- | --- | --- |
| Rights profiles | `GET/POST /api/meshcentral/rights-profiles`, `PUT/DELETE /api/meshcentral/rights-profiles/{id}`, `GET /api/meshcentral/rights-profiles/usage` | CRUD plus visibility into role usage. |
| User embed | `POST /api/meshcentral/embed-url` | Generates the signed URL server-side; the browser must never build MeshCentral auth on its own. |
| Identity sync | `POST /api/meshcentral/identity-sync/backfill` | Supports dry-run and apply modes for reconciliation. |
| Node links | `POST /api/meshcentral/node-links/backfill` | Suggests or persists `meshcentral_node_id` for existing agents using the MeshCentral inventory. |
| Diagnostics | `GET /api/meshcentral/diagnostics/health` | Validates control websocket access and can include site or agent context. |
| Group policy | `GET /api/meshcentral/group-policy/sites/{siteId}/status`, `POST /api/meshcentral/group-policy/reconcile` | Site-level visibility and reconciliation. |
| Deploy/install | `POST /api/deploy-tokens/{id}/meshcentral-install`, `GET /api/agent-auth/me/support/meshcentral/install` | Support provisioning path for admin and agent contexts. |
| Agent embed | `POST /api/agent-auth/me/support/meshcentral/embed-url` | Agent-scoped remote support entry point. |

## Persisted linkage

- User-level fields:
  - `meshcentral_user_id`
  - `meshcentral_username`
  - `meshcentral_last_synced_at`
  - `meshcentral_sync_status`
  - `meshcentral_sync_error`
- Agent-level fields:
  - `meshcentral_node_id`
- Site-level fields:
  - `MeshCentralGroupName`
  - `MeshCentralMeshId`
  - applied group-policy metadata

## Operational notes

- Support is only effective when MeshCentral integration is enabled and the resolved scope also has `SupportEnabled`.
- Embed and install flows already use fallback behavior so deploy/support flows do not hard-fail when live provisioning is temporarily unavailable.
- Identity sync is best-effort in the main lifecycle flows and is backed by reconciliation services for eventual repair.
- Administrative control.ashx access now supports a dedicated internal URL and technical username configuration.
- User authorization now reconciles mesh membership with configurable mesh-level rights and sanitizes per-device ACLs to remove bits invalid for `adddeviceuser`.
- Revocation of device ACLs outside the desired scope is configurable and defaults to preserve manual grants until the operator explicitly enables strict cleanup.

## Helper assets

The helper files kept in `docs\Meshcentral\` are operational assets for MeshCentral maintenance and were intentionally not moved into the capability docs:

- `meshctrl.js`
- `package.json`
- `package-lock.json`

## Roadmap

- Optional remote account deletion policy on full user removal.
- MeshCentral event ingestion for audit/timeline use cases.
- Inventory enrichment from MeshCentral node/device data.
- Progressive SSO and governed remote actions with stronger approval/audit controls.
