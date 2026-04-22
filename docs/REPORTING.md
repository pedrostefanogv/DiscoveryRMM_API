# Reporting

Consolidated from:
- `REPORTING_FRONT_IMPLEMENTATION_GUIDE.md`
- `REPORT_PREVIEW_FRONT_INTEGRATION.md`

## Current status

- Dataset catalog, layout schema, autocomplete, template CRUD, execution, preview, and execution downloads are implemented.
- Multi-source report layouts are supported through `dataSources` and `alias.field` references.
- Preview can return either a binary document or HTML without persisting an execution.

## Main API surfaces

| Area | Endpoints | Notes |
| --- | --- | --- |
| Catalog | `GET /api/reports/datasets`, `GET /api/reports/layout-schema`, `GET /api/reports/autocomplete` | Used by builders and field selectors. |
| Templates | `POST /api/reports/templates`, `GET /api/reports/templates`, `GET /api/reports/templates/{id}`, `GET /api/reports/templates/{id}/history`, `PUT /api/reports/templates/{id}`, `DELETE /api/reports/templates/{id}` | Template management and history. |
| Execution | `POST /api/reports/run`, `GET /api/reports/executions`, `GET /api/reports/executions/{id}` | Runs saved templates and stores execution metadata. |
| Preview | `POST /api/reports/preview` | Supports saved `templateId` or inline template draft. |
| Download | `GET /api/reports/executions/{id}/download`, `GET /api/reports/executions/{id}/download-stream` | Redirects to presigned object-storage URLs. |

## Layout model

- Use `datasetType` instead of old dataset-key contracts when creating or previewing templates.
- Use `defaultFormat` as the persisted default output format.
- In multi-source mode:
  - define `layoutJson.dataSources`
  - use `alias.field` references
  - joins currently allow `left` and `inner`
- `GET /api/reports/autocomplete` is the preferred source for field pickers in builders.

## Preview behavior

- `previewMode = document` returns PDF/XLSX/CSV depending on enabled formats.
- `previewMode = html` returns `text/html` for fast inline rendering.
- `responseDisposition` supports `inline` and `attachment`.
- Preview responses include headers such as:
  - `X-Report-Preview`
  - `X-Report-RowCount`
  - `X-Report-Title`
  - `X-Report-Format`

## Operational notes

- Enabled formats depend on server reporting settings.
- Preview never persists an execution on its own.
- Download routes are compatibility surfaces that now redirect to presigned URLs instead of streaming from local disk.

## Related docs

- `CONFIGURATION.md` for reporting settings.
- `OBJECT_STORAGE.md` for download/storage behavior.
