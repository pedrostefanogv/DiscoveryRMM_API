# Discovery documentation

This directory is organized by capability. Each file contains the current behavior, main endpoints, key configuration points, and a short roadmap only where the capability is still evolving.

Legacy markdown files from `docs\Features\` and `docs\Meshcentral\PLANO_ACAO_MESHCENTRAL.md` were consolidated and removed to eliminate duplicated or outdated guidance.

## Capability map

| Capability | File | Consolidated from |
| --- | --- | --- |
| Authentication and access control | `AUTHENTICATION.md` | auth, first access, MFA, ACL rollout |
| Automation | `AUTOMATION.md` | agent integration, admin API, enum reference |
| App Store | `APP_STORE.md` | catalog, approvals, agent sync |
| Hierarchical configuration | `CONFIGURATION.md` | server/client/site configuration and metadata |
| Deployment and offline install | `DEPLOYMENT_OFFLINE_INSTALL.md` | deploy tokens, agent install, package delivery |
| MeshCentral | `MESHCENTRAL.md` | embed, identity sync, group policy, install flows |
| Messaging and NATS | `MESSAGING_NATS.md` | auth callout, credentials, scoped subjects |
| Object storage and attachments | `OBJECT_STORAGE.md` | S3-compatible storage, ticket attachments, report download |
| P2P distribution and bootstrap | `P2P.md` | cloud bootstrap, seed planning, telemetry |
| Reporting | `REPORTING.md` | datasets, templates, preview, executions |

## Notes

- `docs\Meshcentral\` still contains helper assets used for MeshCentral maintenance (`meshctrl.js`, `package.json`, `package-lock.json`). Those files are not feature docs and were kept as-is.
- Roadmap items now live inside each capability file instead of separate planning documents.
