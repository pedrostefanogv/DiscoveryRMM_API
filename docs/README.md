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



## Ajustes

- Remote debug opera em modelo dual-channel no backend: NATS e o transporte preferencial, e SignalR permanece como fallback para cenarios em que o agent nao consegue publicar ou consumir pelo broker.
- O fluxo de inicio e encerramento da sessao continua reutilizando a estrategia geral de dispatch em tempo real, com tentativa por NATS e fallback para SignalR quando necessario.
- O fluxo de logs do agent aceita tanto o subject tenant-scoped `remote-debug.log` quanto o metodo autenticado do AgentHub, e ambos convergem para o mesmo relay antes de entregar eventos ao frontend.
- As capacidades de remote debug precisam permanecer equivalentes nos dois canais para garantir continuidade operacional e observabilidade consistente.