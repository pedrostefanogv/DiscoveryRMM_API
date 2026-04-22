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

## OpenTelemetry

- Basic OpenTelemetry support is available in `Discovery.Api` for traces and metrics.
- Instrumentation covers ASP.NET Core requests, outbound `HttpClient` calls, and runtime metrics.
- Export uses OTLP and stays disabled unless `OpenTelemetry:Enabled`, `OpenTelemetry:OtlpEndpoint`, or standard `OTEL_EXPORTER_OTLP_ENDPOINT` environment variables are provided.
- `OpenTelemetry:Protocol` accepts `grpc` and `http/protobuf`.

## Secret handling for public source code

- Tracked `appsettings.json` and `appsettings.Development.json` must contain only safe defaults, localhost values, or example domains.
- Discovery.Api already follows the default ASP.NET Core configuration precedence, so environment variables override values from tracked appsettings files.
- `UserSecretsId` is configured for `src/Discovery.Api`, so local development can store secrets outside the repository with `dotnet user-secrets`.
- Production, CI/CD, containers, and publish pipelines should inject secrets through environment variables or the secret store of the target platform.

## Recommended secret sources

- Local development: `dotnet user-secrets`
- CI/CD and production: environment variables
- Never commit private keys, connection string passwords, Redis passwords, MeshCentral login keys, or master encryption keys to source control

## Common environment variable keys

- `ConnectionStrings__DefaultConnection`
- `Nats__Url`
- `Nats__AuthUser`
- `Nats__AuthPassword`
- `Redis__Connection`
- `Redis__Password`
- `Security__Encryption__Enabled`
- `Security__Encryption__MasterKeyBase64`
- `MeshCentral__Enabled`
- `MeshCentral__BaseUrl`
- `MeshCentral__InternalBaseUrl`
- `MeshCentral__PublicBaseUrl`
- `MeshCentral__LoginKeyHex`
- `MeshCentral__TechnicalUsername`
- `MeshCentral__AdministrativeIncludeKeyInQuery`
- `MeshCentral__IdentitySyncMeshMembershipRights`
- `MeshCentral__IdentitySyncDeviceAclRevocationEnabled`
- `AgentPackage__PublicApiScheme`
- `AgentPackage__PublicApiServer`
- `OpenTelemetry__Enabled`
- `OpenTelemetry__ServiceName`
- `OpenTelemetry__ServiceVersion`
- `OpenTelemetry__OtlpEndpoint`
- `OpenTelemetry__Protocol`
- `OpenTelemetry__Headers`
- `OpenTelemetry__MetricExportIntervalMilliseconds`
- `Authentication__Jwt__PrivateKeyPath`
- `Authentication__Jwt__PublicKeyPath`

## Local development examples

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=discovery_dev;Username=postgres;Password=change-me"
dotnet user-secrets set "Nats:AuthUser" "auth"
dotnet user-secrets set "Nats:AuthPassword" "change-me"
dotnet user-secrets set "Redis:Password" "change-me"
dotnet user-secrets set "Security:Encryption:MasterKeyBase64" "<base64-32-byte-key>"
dotnet user-secrets set "MeshCentral:LoginKeyHex" "<hex-login-key>"
dotnet user-secrets set "MeshCentral:TechnicalUsername" "svc-discovery"
```

```powershell
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Database=discovery;Username=postgres;Password=change-me"
$env:Nats__AuthUser = "auth"
$env:Nats__AuthPassword = "change-me"
$env:Redis__Password = "change-me"
```

## Roadmap

- Keep agent-level overrides out of the public configuration contract until there is a concrete use case and conflict model for them.
