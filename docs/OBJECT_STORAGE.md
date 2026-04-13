# Object storage and attachments

Consolidated from:
- `OBJECT_STORAGE_S3_COMPAT_INTEGRATION_PLAN.md`
- `TICKET_ATTACHMENTS_FRONT_INTEGRATION.md`

## Current status

- Server-side S3-compatible object storage settings and connection tests are implemented.
- Ticket attachments already use presigned uploads and completion validation.
- Report execution downloads already redirect to presigned URLs.
- The capability is active, but the long-tail multi-vendor/storage migration work is still evolving.

## Main API surfaces

| Area | Endpoints | Notes |
| --- | --- | --- |
| Server object storage | `GET/PUT/PATCH /api/configurations/server`, `POST /api/configurations/server/object-storage/test` | Global settings for bucket, endpoint, region, credentials, TTL, path-style, and SSL verification. |
| Ticket attachment settings | `GET/PUT /api/configurations/server/ticket-attachments` | Controls feature enablement, max size, allowed MIME types, and presigned upload TTL. |
| Ticket attachment flow | `POST /api/tickets/{ticketId}/attachments/presigned-upload`, `POST /api/tickets/{ticketId}/attachments/complete-upload`, `GET /api/tickets/{ticketId}/attachments` | Direct upload to object storage with backend validation before persistence. |
| Report download | `GET /api/reports/executions/{id}/download`, `GET /api/reports/executions/{id}/download-stream` | Both routes redirect to a presigned download URL. |

## Effective defaults in use

- Ticket attachments enabled by default.
- Default max ticket attachment size: `10 MB`.
- Default allowed MIME types: `image/jpeg`, `image/png`, `image/webp`, `application/pdf`.
- Default presigned upload TTL for ticket attachments: `15 minutes`.

## Object key and validation rules

- Ticket attachment keys are tenant-scoped under:
  - `clients/{clientId}/ticket/{ticketId}/attachments/{attachmentId}/...`
- `complete-upload` rejects mismatched object prefixes.
- The backend validates feature enablement, MIME type, size, and identity of the uploaded object before persisting metadata.

## Operational notes

- The current contract is S3-compatible; clients do not choose a provider in payloads.
- Keep buckets private and use presigned URLs for upload/download instead of public object URLs.
- Reporting and attachment storage now share the same object-storage direction of travel.

## Roadmap

- Finish the remaining provider hardening and compatibility validation for more S3-compatible vendors.
- Continue moving any remaining legacy file flows to object storage-backed metadata.
