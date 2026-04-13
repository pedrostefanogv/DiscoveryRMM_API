# Authentication and access control

Consolidated from:
- `AUTH_FRONT_IMPLEMENTATION_GUIDE.md`
- `FIRST_ACCESS_FRONT_FLOW.md`
- `AUTHENTICATION_ACL_MFA_IMPLEMENTATION_PLAN.md`
- `AUTHORIZATION_PERMISSION_ROLLOUT_PLAN.md`

## Current status

- User login, refresh, logout, first access, and FIDO2/WebAuthn MFA are implemented.
- MFA key management is implemented for authenticated users.
- User authentication is enforced by default through `RequireUserAuth`, with explicit anonymous exceptions for auth/bootstrap flows.
- Permission checks already exist, but the rollout of fully scoped authorization (`Global`, `Client`, `Site`, `Agent`) is still being refined across all controllers.

## Main API surfaces

| Area | Endpoints | Notes |
| --- | --- | --- |
| Session | `POST /api/auth/login`, `POST /api/auth/refresh`, `POST /api/auth/logout` | Login returns a temporary MFA token first; refresh and logout keep the current session model. |
| First access | `GET /api/auth/first-access/status`, `POST /api/auth/first-access/complete` | Used for seeded users that must update profile and password before normal access. |
| MFA assertion | `POST /api/auth/mfa/fido2/begin`, `POST /api/auth/mfa/fido2/complete` | Login challenge flow for users that already have a registered FIDO2 key. |
| MFA registration | `POST /api/mfa/fido2/register/begin`, `POST /api/mfa/fido2/register/complete` | Accepts onboarding token or full access token. |
| MFA key management | `GET /api/mfa/keys`, `DELETE /api/mfa/keys/{keyId}`, `PATCH /api/mfa/keys/{keyId}/name` | The backend prevents deleting the last active key. |

## Effective flow

1. `POST /api/auth/login`
2. If `firstAccessRequired = true`, complete profile/password changes.
3. If MFA is not configured yet, register a FIDO2 key.
4. If MFA is already configured, run the assertion flow.
5. Work normally with `accessToken`; use `refreshToken` only on refresh/logout flows.

## Access control model

- Default protection: user-facing controllers are authenticated unless they explicitly opt out.
- Current anonymous exceptions:
  - `/api/auth/*`
  - `/api/agent-auth/*`
  - `/api/agent-install/*`
- Permission model in use:
  - `ResourceType`
  - `ActionType`
  - `ScopeLevel`
  - `ScopeId`
- Scoped authorization is already present in some surfaces, but the rollout is still not uniform across all endpoints.

## Operational notes

- Current password policy defaults to minimum 12 characters, uppercase, number, and special character. It can still be overridden by configuration.
- FIDO2 is the primary MFA flow today. TOTP is reserved in types/contracts but is not the main user flow.
- Some auth failures still bubble up as generic non-2xx responses instead of a clean `401`; consumers should treat any non-success response from login/refresh as an authentication failure.
- MeshCentral embed is documented separately in `MESHCENTRAL.md`.

## Roadmap

- Finish permission rollout for all mutable and sensitive read endpoints.
- Standardize auth error mapping so login/refresh failures always surface as explicit auth errors.
- Expand optional MFA modalities only after the FIDO2-first flow is considered closed.
