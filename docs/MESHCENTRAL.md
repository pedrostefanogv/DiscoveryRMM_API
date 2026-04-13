# MeshCentral

Consolidated from:
- `docs\Meshcentral\PLANO_ACAO_MESHCENTRAL.md`
- MeshCentral-specific sections that were duplicated in auth/deploy docs

## Current status

- Embed URL generation is implemented for authenticated users and agents.
- Identity sync between Discovery users and MeshCentral accounts is implemented, including backfill/reconcile support.
- Group policy reconciliation and rights profile management are implemented.
- Install instructions are available through deploy-token and authenticated-agent flows.

## Main API surfaces

| Area | Endpoints | Notes |
| --- | --- | --- |
| Rights profiles | `GET/POST /api/meshcentral/rights-profiles`, `PUT/DELETE /api/meshcentral/rights-profiles/{id}`, `GET /api/meshcentral/rights-profiles/usage` | CRUD plus visibility into role usage. |
| User embed | `POST /api/meshcentral/embed-url` | Generates the signed URL server-side; the browser must never build MeshCentral auth on its own. |
| Identity sync | `POST /api/meshcentral/identity-sync/backfill` | Supports dry-run and apply modes for reconciliation. |
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
- Site-level fields:
  - `MeshCentralGroupName`
  - `MeshCentralMeshId`
  - applied group-policy metadata

## Operational notes

- Support is only effective when MeshCentral integration is enabled and the resolved scope also has `SupportEnabled`.
- Embed and install flows already use fallback behavior so deploy/support flows do not hard-fail when live provisioning is temporarily unavailable.
- Identity sync is best-effort in the main lifecycle flows and is backed by reconciliation services for eventual repair.

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
