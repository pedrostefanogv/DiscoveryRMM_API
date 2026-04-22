# Automation

Consolidated from:
- `AUTOMATION_AGENT_INTEGRATION.md`
- `AUTOMATION_FRONT_SITE_INTEGRATION.md`
- `AUTOMATION_ENUMS_REFERENCE.md`

## Current status

- Administrative CRUD for automation scripts and tasks is implemented.
- Agent policy synchronization plus ACK/result callbacks is implemented.
- Scope resolution, include/exclude tags, audit, and correlation IDs are already part of the backend surface.
- App Store is documented separately in `APP_STORE.md`.

## Administrative API

| Area | Endpoints | Notes |
| --- | --- | --- |
| Scripts | `GET/POST /api/automation/scripts`, `GET/PUT/DELETE /api/automation/scripts/{id}` | Script catalog with versioned content and audit support. |
| Script consume/audit | `GET /api/automation/scripts/{id}/consume`, `GET /api/automation/scripts/{id}/audit` | Consume returns the execution payload for active scripts only. |
| Tasks | `GET/POST /api/automation/tasks`, `GET/PUT/DELETE /api/automation/tasks/{id}` | Tasks define action, scope, triggers, tags, and execution metadata. |
| Task extras | `POST /api/automation/tasks/{id}/restore`, `GET /api/automation/tasks/{id}/audit`, `GET /api/automation/tasks/{id}/preview-agents` | Restore deleted tasks, inspect audit, and preview the target agent set. |

## Agent API

| Endpoint | Purpose |
| --- | --- |
| `POST /api/agent-auth/me/automation/policy-sync` | Returns the effective automation policy for the authenticated agent and supports fingerprint-based short-circuiting. |
| `POST /api/agent-auth/me/automation/executions/{commandId}/ack` | Confirms command receipt and start of processing. |
| `POST /api/agent-auth/me/automation/executions/{commandId}/result` | Reports the final execution result and updates the administrative command state. |

## Core behavior

- Scope model: `Global`, `Client`, `Site`, `Agent`.
- Include tags require at least one match; exclude tags block delivery on any match.
- `X-Correlation-Id` is supported on admin and agent flows and should be reused per operational cycle.
- Policy sync can return `UpToDate = true` when the known fingerprint still matches.
- Script content is optional in policy sync and only appears when explicitly requested.

## Important enums

| Enum | Values in use |
| --- | --- |
| `AutomationTaskActionType` | `InstallPackage`, `UpdatePackage`, `RemovePackage`, `UpdateOrInstallPackage`, `RunScript`, `CustomCommand` |
| `AutomationScriptType` | `PowerShell`, `Shell`, `Python`, `Batch`, `Custom` |
| `AutomationExecutionSourceType` | `RunNow`, `Scheduled`, `ForceSync`, `AgentManual` |
| `AutomationExecutionStatus` | `Dispatched`, `Acknowledged`, `Completed`, `Failed` |

## Roadmap

- Keep `RequiresApproval` as contract metadata for now; deeper approval governance can be expanded later without changing the main API shape.
- Continue tightening script/runtime compatibility guidance at the agent side as more executors are added.
