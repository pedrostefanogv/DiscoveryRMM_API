# Hierarchical configuration

Consolidated from:
- `CONFIGURATION_POLICIES_INTEGRATION.md`

## Current status

- Hierarchical configuration is implemented for `Server -> Client -> Site`.
- Effective values, metadata, and field locks are already exposed by the API.
- Specialized server settings for reporting, ticket attachments, object storage, and NATS are part of the same configuration surface.

## Resolution model

1. `Site` overrides `Client`.
2. `Client` overrides `Server`.
3. Locks from parent scopes block child edits.
4. Metadata exposes source and editability rules per field.

`ConfigurationPriorityType` is currently used as:
- `Block`
- `Global`
- `Client`
- `Site`
- `Agent` is reserved for future expansion

## Main API surfaces

| Scope | Endpoints |
| --- | --- |
| Server | `GET/PUT/PATCH /api/configurations/server`, `POST /api/configurations/server/reset`, `GET /api/configurations/server/metadata` |
| Server extras | `GET/PUT /api/configurations/server/reporting`, `GET/PUT /api/configurations/server/ticket-attachments`, `POST /api/configurations/server/object-storage/test`, `POST /api/configurations/server/nats/generate-account-key`, `POST /api/configurations/server/nats/test`, `PATCH /api/configurations/server/nats` |
| Client | `GET /api/configurations/clients/{clientId}`, `GET /api/configurations/clients/{clientId}/effective`, `GET /api/configurations/clients/{clientId}/metadata`, `PUT/PATCH/DELETE /api/configurations/clients/{clientId}`, `POST /api/configurations/clients/{clientId}/reset/{propertyName}` |
| Site | `GET /api/configurations/sites/{siteId}`, `GET /api/configurations/sites/{siteId}/effective`, `GET /api/configurations/sites/{siteId}/metadata`, `PUT/PATCH/DELETE /api/configurations/sites/{siteId}`, `POST /api/configurations/sites/{siteId}/reset/{propertyName}` |

## Important inherited fields

- `RecoveryEnabled`
- `DiscoveryEnabled`
- `P2PFilesEnabled`
- `CloudBootstrapEnabled`
- `SupportEnabled`
- `MeshCentralGroupPolicyProfile`
- `ChatAIEnabled`
- `KnowledgeBaseEnabled`
- `AppStorePolicy`
- `InventoryIntervalHours`
- `AutoUpdateSettingsJson`
- `AIIntegrationSettingsJson`
- `TicketAttachmentSettingsJson`
- `AgentHeartbeatIntervalSeconds`

## Operational notes

- `LockedFieldsJson` is the source of truth for parent-level edit blocking.
- Object storage credentials stay global at server scope.
- NATS seeds and XKeys are server-level secrets and should only be changed through the dedicated server endpoints.
- Site configuration carries MeshCentral-specific linkage fields such as group name and mesh id.

## Roadmap

- Keep agent-level overrides out of the public configuration contract until there is a concrete use case and conflict model for them.
