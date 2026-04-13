# App Store

Consolidated from:
- `APP_STORE_API_INTEGRATION.md`

## Current status

- Catalog search, package lookup, custom package upsert, approval rules, audit, effective policy views, diff views, and catalog sync are implemented.
- Agents already have a dedicated effective policy endpoint and also receive App Store revisions through the sync manifest.
- Configuration still controls whether the App Store is effectively enabled for a given scope.

## Main API surfaces

| Area | Endpoints | Notes |
| --- | --- | --- |
| Catalog | `GET /api/app-store/catalog`, `GET /api/app-store/catalog/{packageId}`, `POST /api/app-store/catalog/custom` | Supports `Winget`, `Chocolatey`, and `Custom`. |
| Approval rules | `GET/POST /api/app-store/approvals`, `DELETE /api/app-store/approvals/{ruleId}` | Rules are scoped by `Global`, `Client`, `Site`, or `Agent`. |
| Audit and diff | `GET /api/app-store/approvals/audit`, `GET /api/app-store/diff/effective`, `GET /api/app-store/diff/{packageId}` | Used to compare effective policy and inspect changes. |
| Effective policy | `GET /api/app-store/effective` | Paged effective app list for a specific scope. |
| Sync | `POST /api/app-store/sync` | Triggers catalog refresh and publishes invalidation events. |
| Agent consumption | `GET /api/agent-auth/me/app-store` | Returns the effective app set for the authenticated agent. |
| Agent manifest | `GET /api/agent-auth/me/sync-manifest` | Publishes per-installation-type revision markers for sync loops. |

## Types in use

| Type | Values |
| --- | --- |
| `AppInstallationType` | `Winget`, `Chocolatey`, `Custom` |
| `AppApprovalScopeType` | `Global`, `Client`, `Site`, `Agent` |
| `AppApprovalActionType` | `Allow`, `Deny` |

## Operational notes

- `AppStorePolicy` from hierarchical configuration determines whether the feature is effectively enabled for a scope.
- Custom catalog items can carry object storage metadata, so custom packages should stay aligned with `OBJECT_STORAGE.md`.
- Sync publishes invalidation events so agents and control-plane consumers can refresh without manual cache busting.

## Roadmap

- Keep vendor-specific install examples and operational playbooks lightweight here; expand them only when new package providers are introduced.
