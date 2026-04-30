# Discovery RMM - Guia de API para Frontend e Site

> Ultima atualizacao: 2026-04-30 (revisao completa de integracao frontend-backend)
> Fonte: controllers, DTOs, validators e middleware em src/Discovery.Api e src/Discovery.Core.
> Escopo: contratos HTTP e SignalR consumiveis por painel web, portal/site e telas administrativas.

## 1. Contrato global da API

| Item | Contrato atual |
|---|---|
| Base HTTP | `/api/v1` por versionamento em URL segment. O projeto ja esta preparado para `/api/v2` quando houver nova versao. |
| OpenAPI | `/openapi/v1.json` |
| UI de referencia | `/scalar/v1` |
| Serializacao JSON | Requests aceitam `camelCase` e `PascalCase`; responses saem em `camelCase`; enums usam string; propriedades `null` sao omitidas. |
| Auth principal | `Authorization: Bearer <jwt>` |
| Auth alternativa | `Authorization: ApiKey <tokenIdPublic>.<accessKey>` ou `X-Api-Key` + `X-Api-Secret` |
| CORS HTTP | Policy `DefaultApi` |
| CORS hubs | Policy `SignalR` |
| Realtime | `/hubs/notifications`, `/hubs/remote-debug` |
| Erro global nao tratado | `{ error, timestamp, traceId }` |
| Validacao automatica | `FluentValidation` + comportamento padrao de model validation do ASP.NET Core com `400` |

### 1.1 O que o frontend deve assumir

- Nem todo endpoint usa um envelope padrao de resposta.
- Erros de negocio aparecem em formatos diferentes: `{ message }`, `{ error }`, `{ code, message }` ou `ValidationProblemDetails`.
- O front deve ter uma camada de normalizacao de erro antes de exibir mensagens.
- A maioria dos controllers depende do filtro global `RequireUserAuth`; endpoints publicos fazem opt-out com `AllowAnonymous`.
- Tokens com claims `mfa_pending=true` ou `mfa_setup=true` nao passam no filtro `RequireUserAuth`.

### 1.2 Rotas que o browser deve evitar

Estas rotas existem no backend, mas nao sao o alvo principal de um frontend web convencional:

- `/api/v1/agent-auth/**`: fluxo machine-to-machine do agente.
- `/api/v1/nats-auth/**`: autenticacao de infraestrutura/NATS.
- `/api/v1/ops/p2p/**`: operacao interna.
- `/hubs/agent`: hub do agente, nao do painel.

## 2. Autenticacao, MFA e sessao

### 2.1 Login e refresh

| Endpoint | Request | Response | Observacoes |
|---|---|---|---|
| `POST /api/v1/auth/login` | `LoginRequestDto` `{ loginOrEmail, password }` | `LoginResponseDto` | Pode devolver sessao completa ou exigir MFA/setup. |
| `POST /api/v1/auth/refresh` | `RefreshTokenRequestDto` `{ refreshToken }` | `TokenPairDto` | Publico. Retorna `401` quando o refresh token e invalido. |
| `POST /api/v1/auth/logout` | `RefreshTokenRequestDto` | `204 No Content` | Requer JWT completo. A implementacao usa a sessao do JWT; o body existe por compatibilidade do contrato. |

`LoginResponseDto` e o ponto central do estado de autenticacao do front:

```json
{
  "mfaToken": "jwt-curto-ou-vazio",
  "mfaRequired": true,
  "roleMfaRequirement": "Totp",
  "mfaConfigured": true,
  "firstAccessRequired": false,
  "mustChangePassword": false,
  "mustChangeProfile": false,
  "sessionEstablished": false,
  "accessToken": null,
  "refreshToken": null,
  "expiresInSeconds": null
}
```

Regras praticas para o front:

- Se `sessionEstablished=true`, usar `accessToken` e `refreshToken` normalmente.
- Se `mfaRequired=true` e `sessionEstablished=false`, usar `mfaToken` no header Bearer para os endpoints de MFA.
- Se `firstAccessRequired=true`, exibir onboarding antes de liberar a area autenticada.

### 2.2 Etapa MFA de login

| Endpoint | Header | Body | Response |
|---|---|---|---|
| `POST /api/v1/auth/mfa/fido2/begin` | `Authorization: Bearer <mfaPendingToken>` | vazio | `{ options }` com challenge FIDO2 |
| `POST /api/v1/auth/mfa/fido2/complete` | mesmo header | `CompleteFido2AssertionDto` `{ assertionResponseJson }` | `LoginResponseDto` com sessao completa |
| `POST /api/v1/auth/mfa/otp/complete` | mesmo header | `CompleteOtpAssertionDto` `{ code }` | `LoginResponseDto` com sessao completa |

### 2.3 Primeiro acesso

| Endpoint | Header | Body | Response |
|---|---|---|---|
| `POST /api/v1/auth/first-access/complete` | `Bearer <mfaSetupToken ou sessao completa>` | `CompleteFirstAccessRequestDto` `{ newLogin, newEmail, newFullName, currentPassword, newPassword }` | `200` com `{ message }` |
| `GET /api/v1/auth/first-access/status` | mesmo header | - | `FirstAccessStatusDto` |

Observacao: esse endpoint nao retorna `LoginResponseDto`. Depois do onboarding, o front ainda precisa concluir o setup de MFA quando aplicavel.

### 2.4 Cadastro e gestao de MFA

Base: `/api/v1/mfa`

| Endpoint | Requisito | Request | Response |
|---|---|---|---|
| `GET /keys` | JWT completo | - | `MfaKeyDto[]` |
| `POST /fido2/register/begin` | `mfaSetupToken` ou JWT completo | vazio | `{ options }` |
| `POST /fido2/register/complete` | `mfaSetupToken` ou JWT completo | `CompleteFido2RegistrationDto` `{ attestationResponseJson, keyName }` | `{ keyId, message }` |
| `POST /totp/register/begin` | `mfaSetupToken` ou JWT completo | vazio | `{ secretBase32, qrCodeUri, message }` |
| `POST /totp/register/complete` | `mfaSetupToken` ou JWT completo | `CompleteTotpRegistrationDto` `{ secretBase32, verificationCode, keyName }` | `{ keyId, message, backupCodes }` |
| `DELETE /keys/{keyId}` | JWT completo | - | `204` ou `400` se for a ultima chave ativa |
| `PATCH /keys/{keyId}/name` | JWT completo | `RegisterMfaKeyNameDto` `{ keyName }` | `204` |

## 3. API tokens para integracoes

Base: `/api/v1/api-tokens`

| Endpoint | Request | Response | Validacao |
|---|---|---|---|
| `GET /api-tokens` | - | `ApiTokenSummaryDto[]` | Lista apenas tokens do usuario autenticado. |
| `POST /api-tokens` | `CreateApiTokenRequestDto` `{ name, expiresAt? }` | `CreateApiTokenResponseDto` | `name` obrigatorio, max 200; `expiresAt` deve estar no futuro. |
| `DELETE /api-tokens/{tokenId}` | - | `204` | Revoga o token. |

Importante para UX: `accessKey` so aparece na resposta de criacao. O front deve obrigar o usuario a copiar/baixar essa credencial na hora.

## 4. Usuarios, roles e autorizacao

### 4.1 Usuarios

Base: `/api/v1/users`

| Endpoint | Request | Response |
|---|---|---|
| `GET /users` | - | `UserSummaryDto[]` |
| `GET /users/{id}` | - | `UserDto` |
| `GET /users/me` | - | `UserDto` |
| `PUT /users/me` | `UpdateMyProfileDto` `{ email?, fullName? }` | `204` |
| `GET /users/me/security` | - | `MySecurityProfileDto` |
| `POST /users/me/change-password` | `ChangePasswordDto` `{ currentPassword, newPassword }` | `204` |
| `POST /users` | `CreateUserDto` `{ login, email, fullName, password, mfaRequired }` | `201` com `{ id, meshCentralSync }` |
| `PUT /users/{id}` | `UpdateUserDto` | `204` |
| `POST /users/{id}/change-password` | `ChangePasswordDto` | `204` |
| `GET /users/{id}/mfa/keys` | - | `AdminUserMfaKeyDto[]` |
| `DELETE /users/{id}/mfa` | - | `204` |
| `DELETE /users/{id}/mfa/keys/{keyId}` | - | `204` |
| `POST /users/{id}/force-password-reset` | - | `204` |
| `DELETE /users/{id}` | - | `204` |

Observacoes de contrato:

- `PUT /users/me` e `PUT /users/{id}` retornam `204`, nao o objeto atualizado.
- `POST /users` valida unicidade de login/email e politica de senha no backend.
- `DELETE /users/{id}` faz soft delete (`isActive=false`).
- O DTO de criacao possui `mfaRequired`, mas a implementacao atual cria usuarios com `MfaRequired = true` independentemente do valor recebido. Trate isso como comportamento atual do backend.

### 4.2 Roles

Base: `/api/v1/roles`

| Endpoint | Request | Response |
|---|---|---|
| `GET /roles` | - | `RoleDto[]` |
| `GET /roles/{id}` | - | `RoleDto` |
| `POST /roles` | `CreateRoleDto` `{ name, description?, mfaRequirement, meshRightsMask?, meshRightsProfile? }` | `201` com `{ id }` |
| `PUT /roles/{id}` | `UpdateRoleDto` | `204` |
| `DELETE /roles/{id}` | - | `204` |
| `GET /roles/{id}/permissions` | - | `PermissionDto[]` |
| `GET /roles/permissions` | - | `PermissionDto[]` |
| `POST /roles/{id}/permissions` | `AssignPermissionToRoleDto` `{ permissionId }` | `204` |
| `DELETE /roles/{id}/permissions/{permissionId}` | - | `204` |

Regras relevantes:

- Roles de sistema nao podem ser excluidas nem ter nome/descricao alterados.
- `meshRightsProfile`, quando informado, precisa existir no cadastro MeshCentral.

### 4.3 User Groups

Base: `/api/v1/user-groups`

| Endpoint | Request | Response |
|---|---|---|
| `GET /user-groups` | - | `UserGroupDto[]` |
| `GET /user-groups/{id}` | - | `UserGroupDto` |
| `POST /user-groups` | `CreateUserGroupDto` | `201` com grupo criado |
| `PUT /user-groups/{id}` | `UpdateUserGroupDto` | `204` |
| `DELETE /user-groups/{id}` | - | `204` |
| `GET /user-groups/{id}/members` | - | membros do grupo |
| `POST /user-groups/{id}/members` | `{ userId }` | `204` |
| `DELETE /user-groups/{id}/members/{userId}` | - | `204` |
| `GET /user-groups/{id}/roles` | - | roles atribuidas ao grupo |
| `POST /user-groups/{id}/roles` | `AssignRoleToGroupDto` | `204` |
| `DELETE /user-groups/{id}/roles/{assignmentId}` | - | `204` |

### 4.4 Permissoes e escopo

O frontend deve considerar tres niveis de erro de acesso:

- `401`: nao autenticado, token expirado, MFA nao concluido ou token inadequado para o endpoint.
- `403`: autenticado, mas sem permissao suficiente.
- `409`: conflito de estado em operacoes administrativas e de workflow.

## 5. Cadastros base: clientes, sites e custom fields

### 5.1 Clientes

Base: `/api/v1/clients`

| Endpoint | Request | Response | Validacao |
|---|---|---|---|
| `GET /clients?includeInactive=false` | - | array de clientes | - |
| `GET /clients/{id}` | - | cliente | - |
| `POST /clients` | `CreateClientRequest` `{ name, notes? }` | cliente criado | `name` 2-200, `notes` max 2000 |
| `PUT /clients/{id}` | `UpdateClientRequest` `{ name, notes?, isActive }` | cliente atualizado | mesmas regras |
| `DELETE /clients/{id}` | - | `204` | - |
| `GET /clients/{id}/custom-fields` | query `includeSecrets?` | valores | validacao no service |
| `PUT /clients/{id}/custom-fields/{definitionId}` | `{ value }` | valor salvo | validacao no service |

### 5.2 Sites

Base: `/api/v1/clients/{clientId}/sites`

| Endpoint | Request | Response |
|---|---|---|
| `GET /clients/{clientId}/sites?includeInactive=false` | - | array de sites |
| `GET /clients/{clientId}/sites/{id}` | - | site |
| `POST /clients/{clientId}/sites` | `CreateSiteRequest` `{ name, notes? }` | site criado |
| `PUT /clients/{clientId}/sites/{id}` | `UpdateSiteRequest` `{ name, notes?, isActive }` | site atualizado |
| `DELETE /clients/{clientId}/sites/{id}` | - | `204` |
| `GET /clients/{clientId}/sites/{id}/custom-fields` | query `includeSecrets?` | valores |
| `PUT /clients/{clientId}/sites/{id}/custom-fields/{definitionId}` | `{ value }` | valor salvo |

### 5.3 Custom fields genericos

Base: `/api/v1/custom-fields`

Esses endpoints permitem montar formularios dinamicos sem hardcode no frontend.

| Endpoint | Request | Response |
|---|---|---|
| `GET /custom-fields/definitions` | query `scopeType?`, `includeInactive?` | definicoes |
| `GET /custom-fields/definitions/{id}` | - | definicao |
| `POST /custom-fields/definitions` | `UpsertCustomFieldDefinitionRequest` | definicao criada |
| `PUT /custom-fields/definitions/{id}` | `UpsertCustomFieldDefinitionRequest` | definicao atualizada |
| `DELETE /custom-fields/definitions/{id}` | - | `204` |
| `GET /custom-fields/values/{scopeType}` | query `entityId?`, `includeSecrets?` | valores |
| `GET /custom-fields/schema/{scopeType}` | query `entityId?`, `includeInactive?`, `includeSecrets?` | `CustomFieldSchemaItemDto[]` |
| `PUT /custom-fields/values/{definitionId}` | `UpsertCustomFieldValueRequest` | valor salvo |

Campos relevantes de `UpsertCustomFieldDefinitionRequest`:

- `name`, `label`, `description?`
- `scopeType`, `dataType`
- `isRequired`, `isActive`, `isSecret`
- `options?`, `validationRegex?`
- `minLength?`, `maxLength?`, `minValue?`, `maxValue?`
- `allowRuntimeRead`, `allowAgentWrite`, `runtimeAccessMode`
- `accessBindings?`

Recomendacao: construir os formularios dinamicos a partir de `GET /custom-fields/schema/{scopeType}` em vez de replicar regras no front.

### 5.4 Departamentos

Base: `/api/v1/departments`

| Endpoint | Request | Response |
|---|---|---|
| `GET /departments/global` | - | departamentos globais |
| `GET /departments` | query `clientId?` | departamentos |
| `GET /departments/{id}` | - | departamento |
| `POST /departments` | `CreateDepartmentDto` | `201` |
| `PUT /departments/{id}` | `UpdateDepartmentDto` | `204` |
| `DELETE /departments/{id}` | - | `204` |

### 5.5 Workflow - Estados e Transicoes

Base: `/api/v1/workflow`

| Endpoint | Request | Response |
|---|---|---|
| `GET /workflow/states` | - | estados de workflow |
| `GET /workflow/states/{id}` | - | estado |
| `POST /workflow/states` | `CreateWorkflowStateDto` | `201` |
| `PUT /workflow/states/{id}` | `UpdateWorkflowStateDto` | `204` |
| `DELETE /workflow/states/{id}` | - | `204` |
| `GET /workflow/transitions` | - | transicoes |
| `GET /workflow/transitions/from/{fromStateId}` | - | transicoes a partir de um estado |
| `POST /workflow/transitions` | `CreateWorkflowTransitionDto` | `201` |
| `DELETE /workflow/transitions/{id}` | - | `204` |

### 5.6 Workflow Profiles

Base: `/api/v1/workflowprofiles`

| Endpoint | Request | Response |
|---|---|---|
| `GET /workflowprofiles/global` | - | perfis globais |
| `GET /workflowprofiles` | query `clientId?` | perfis |
| `GET /workflowprofiles/by-department/{departmentId}` | - | perfis do departamento |
| `GET /workflowprofiles/{id}` | - | perfil |
| `POST /workflowprofiles` | `CreateWorkflowProfileDto` | `201` |
| `PUT /workflowprofiles/{id}` | `UpdateWorkflowProfileDto` | `204` |
| `DELETE /workflowprofiles/{id}` | - | `204` |

### 5.7 SLA Calendars

Base: `/api/v1/sla-calendars`

| Endpoint | Request | Response |
|---|---|---|
| `GET /sla-calendars` | - | calendarios |
| `GET /sla-calendars/{id}` | - | calendario |
| `POST /sla-calendars` | `CreateSlaCalendarDto` | `201` |
| `PUT /sla-calendars/{id}` | `UpdateSlaCalendarDto` | `204` |
| `DELETE /sla-calendars/{id}` | - | `204` |
| `POST /sla-calendars/{id}/holidays` | `CreateHolidayDto` | `201` |
| `DELETE /sla-calendars/{id}/holidays/{holidayId}` | - | `204` |

## 6. Deploy tokens, instalacao do agent e download de pacote

### 6.1 Deploy tokens

Base: `/api/v1/deploy-tokens`

| Endpoint | Request | Response | Observacoes |
|---|---|---|---|
| `GET /deploy-tokens?clientId=<guid>&siteId=<guid>` | - | lista resumida | `clientId` e `siteId` sao obrigatorios. |
| `POST /deploy-tokens` | `CreateDeployTokenRequest` `{ clientId, siteId, description?, expiresInHours?, multiUse?, delivery? }` | token ou arquivo binario | `delivery=installer` devolve arquivo gerado no ato. |
| `POST /deploy-tokens/installer-options` | `{ rawToken }` | opcoes `online` e `offline` | Bom para UI seletora no site. |
| `POST /deploy-tokens/download-installer` | `{ rawToken, installerType }` | arquivo `.exe` ou `.zip` | `installerType` normaliza `online/installer` e `offline/portable`. |
| `POST /deploy-tokens/{id}/download` | `{ rawToken, artifact }` | pacote do agent | `artifact=installer` ou `portable`. |
| `POST /deploy-tokens/{id}/meshcentral-install` | `{ rawToken, artifact? }` | instrucoes MeshCentral | Se suporte estiver habilitado. |
| `POST /deploy-tokens/prebuild` | `{ forceRebuild }` | `{ success, forceRebuild }` | Operacao administrativa. |
| `POST /deploy-tokens/{id}/revoke` | - | `204` | Revoga token. |

Resposta de `POST /deploy-tokens/installer-options`:

```json
{
  "tokenId": "guid",
  "clientId": "guid",
  "siteId": "guid",
  "expiresAt": "2026-05-01T12:00:00Z",
  "options": [
    {
      "type": "online",
      "displayName": "Online (menor)",
      "requiresInternet": true,
      "fileExtension": ".exe",
      "recommended": true
    },
    {
      "type": "offline",
      "displayName": "Offline (completo)",
      "requiresInternet": false,
      "fileExtension": ".zip",
      "recommended": false
    }
  ]
}
```

### 6.2 Registro do agent via token de deploy

Base: `/api/v1/agent-install` e controller publico (`AllowAnonymous`), mas protegido por bearer token de deploy.

| Endpoint | Header | Request | Response |
|---|---|---|---|
| `POST /agent-install/register` | `Authorization: Bearer <deployToken>` | `RegisterAgentInstallRequest` `{ hostname, displayName?, operatingSystem?, osVersion?, agentVersion?, macAddress? }` | `{ agentId, token, ... }` |
| `POST /agent-install/{agentId}/token` | mesmo header | vazio | `{ token, id, expiresAt }` |

## 7. Tickets e service desk

### 7.1 CRUD principal de tickets

Base: `/api/v1/tickets`

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /tickets` | `TicketFilterQuery` | colecao de tickets |
| `GET /tickets/by-client/{clientId}` | query `workflowStateId?` | colecao de tickets |
| `GET /tickets/{id}` | - | ticket |
| `POST /tickets` | `CreateTicketRequest` | `201` com ticket criado |
| `PUT /tickets/{id}` | `UpdateTicketRequest` | ticket atualizado |
| `PATCH /tickets/{id}/workflow-state` | `UpdateWorkflowStateRequest` | `{ message, ticket }` |
| `DELETE /tickets/{id}` | - | `204` |

`TicketFilterQuery` aceito em query string:

- `clientId`, `siteId`, `agentId`, `departmentId`, `workflowProfileId`, `workflowStateId`, `assignedToUserId`
- `priority`, `slaBreached`, `isClosed`, `text`
- `limit` e `offset`

`CreateTicketRequest`:

```json
{
  "clientId": "guid",
  "siteId": "guid ou null",
  "agentId": "guid ou null",
  "departmentId": "guid ou null",
  "workflowProfileId": "guid ou null",
  "title": "Falha no backup noturno",
  "description": "Detalhes do incidente",
  "priority": "High",
  "category": "Backup",
  "assignedToUserId": "guid ou null"
}
```

Validacoes confirmadas:

- `clientId`: obrigatorio.
- `title`: 3 a 200 caracteres.
- `description`: 3 a 10000 caracteres.
- `priority`: precisa ser enum valido.
- `category`: max 100 caracteres.
- `departmentId` e `workflowProfileId`: validados manualmente no controller.
- O backend rejeita criacao sem estado inicial de workflow configurado.

`UpdateTicketRequest`:

- `title`, `description`, `priority`, `assignedToUserId?`, `category?`
- Mesmas regras de validacao de titulo, descricao, prioridade e categoria.

### 7.2 Comentarios e anexos

| Endpoint | Request | Response | Observacoes |
|---|---|---|---|
| `GET /tickets/{id}/comments` | - | comentarios | - |
| `POST /tickets/{id}/comments` | `AddCommentRequest` `{ author, content, isInternal }` | `201` | `author` 2-100; `content` 3-4000 |
| `GET /tickets/{id}/attachments` | - | anexos | - |
| `POST /tickets/{id}/attachments/presigned-upload` | `{ fileName, contentType, sizeBytes }` | dados de upload presignado | Valida tipo e tamanho. |
| `POST /tickets/{id}/attachments/complete-upload` | `{ attachmentId, objectKey, fileName, contentType, sizeBytes, uploadedBy? }` | anexo criado | `objectKey` precisa seguir o prefixo esperado pelo backend. |

Fluxo de upload recomendado no front:

1. Chamar `presigned-upload`.
2. Subir o binario direto ao object storage com os dados retornados.
3. Confirmar em `complete-upload`.
4. Recarregar `GET /tickets/{id}/attachments`.

### 7.3 KPIs de tickets

Base: `/api/v1/tickets/kpi`

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /tickets/kpi` | filtros por periodo, cliente, etc. | `TicketKpiDto` com metricas agregadas |

### 7.4 Saved Views

Base: `/api/v1/ticket-saved-views`

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /ticket-saved-views` | - | views salvas do usuario |
| `GET /ticket-saved-views/{id}` | - | view |
| `POST /ticket-saved-views` | `CreateTicketSavedViewDto` | `201` |
| `PUT /ticket-saved-views/{id}` | `UpdateTicketSavedViewDto` | `204` |
| `DELETE /ticket-saved-views/{id}` | - | `204` |

### 7.5 Watchers (seguidores do ticket)

Base: `/api/v1/tickets/{ticketId}/watchers`

| Endpoint | Request | Response |
|---|---|---|
| `GET /tickets/{ticketId}/watchers` | - | lista de watchers |
| `POST /tickets/{ticketId}/watchers` | `{ userId }` | `201` |
| `DELETE /tickets/{ticketId}/watchers/{userId}` | - | `204` |

### 7.6 Custom Fields do Ticket

Base: `/api/v1/tickets/{ticketId}/custom-fields`

| Endpoint | Request | Response |
|---|---|---|
| `GET /tickets/{ticketId}/custom-fields` | query `includeSecrets?` | valores |
| `PUT /tickets/{ticketId}/custom-fields/{definitionId}` | `{ value }` | valor salvo |

### 7.7 Audit (trilha de auditoria do ticket)

Base: `/api/v1/tickets/{ticketId}/audit`

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /tickets/{ticketId}/audit/timeline/unified` | - | timeline unificada (atividades + comentarios) |
| `GET /tickets/{ticketId}/audit/timeline` | - | timeline de atividades |
| `GET /tickets/{ticketId}/audit/timeline/activity-type/{activityType}` | - | atividades por tipo |
| `GET /tickets/{ticketId}/audit/timeline/user/{userId}` | - | atividades por usuario |
| `GET /tickets/{ticketId}/audit/timeline/date-range` | query `from`, `to` | atividades por periodo |
| `GET /tickets/{ticketId}/audit/timeline/last` | - | ultimas atividades |
| `GET /tickets/{ticketId}/audit/statistics` | - | estatisticas de atividade |

### 7.8 Automation Links (vinculo com automacao)

Base: `/api/v1/tickets/{ticketId}/automation-links`

| Endpoint | Request | Response |
|---|---|---|
| `GET /tickets/{ticketId}/automation-links` | - | links de automacao |
| `POST /tickets/{ticketId}/automation-links` | `CreateAutomationLinkDto` | `201` |
| `PATCH /tickets/{ticketId}/automation-links/{linkId}/approve` | - | link aprovado |
| `PATCH /tickets/{ticketId}/automation-links/{linkId}/reject` | - | link rejeitado |

### 7.9 Remote Sessions (sessoes remotas do ticket)

Base: `/api/v1/tickets/{ticketId}/remote-sessions`

| Endpoint | Request | Response |
|---|---|---|
| `GET /tickets/{ticketId}/remote-sessions` | - | sessoes remotas |
| `POST /tickets/{ticketId}/remote-sessions` | `CreateRemoteSessionLinkDto` | `201` |
| `PATCH /tickets/{ticketId}/remote-sessions/{sessionId}/end` | - | sessao encerrada |

### 7.10 AI Assistant do Ticket

Base: `/api/v1/tickets/{id}/ai`

| Endpoint | Request | Response |
|---|---|---|
| `POST /tickets/{id}/ai/triage` | payload com contexto | resultado de triage |
| `POST /tickets/{id}/ai/summarize` | - | resumo do ticket |
| `POST /tickets/{id}/ai/suggest-reply` | - | sugestao de resposta |
| `POST /tickets/{id}/ai/draft-kb-article` | - | rascunho de artigo KB |

### 7.11 Ticket Alert Rules

Base: `/api/v1/ticket-alert-rules`

| Endpoint | Request | Response |
|---|---|---|
| `GET /ticket-alert-rules` | - | regras de alerta |
| `GET /ticket-alert-rules/{id}` | - | regra |
| `GET /ticket-alert-rules/by-workflow-state/{workflowStateId}` | - | regras por estado |
| `POST /ticket-alert-rules` | `CreateTicketAlertRuleDto` | `201` |
| `PUT /ticket-alert-rules/{id}` | `UpdateTicketAlertRuleDto` | `204` |
| `PATCH /ticket-alert-rules/{id}/toggle` | - | alterna ativo/inativo |
| `DELETE /ticket-alert-rules/{id}` | - | `204` |

### 7.12 Ticket SLA

Base: `/api/v1/tickets/{ticketId}/sla`

| Endpoint | Request | Response |
|---|---|---|
| `GET /tickets/{ticketId}/sla/status` | - | status atual do SLA |
| `GET /tickets/{ticketId}/sla/details` | - | detalhes completos do SLA |

### 7.13 Auto Ticket Rules

Base: `/api/v1/auto-ticket-rules`

| Endpoint | Request | Response |
|---|---|---|
| `GET /auto-ticket-rules` | - | regras |
| `GET /auto-ticket-rules/{id}` | - | regra |
| `POST /auto-ticket-rules` | `CreateAutoTicketRuleDto` | `201` |
| `PUT /auto-ticket-rules/{id}` | `UpdateAutoTicketRuleDto` | `204` |
| `PATCH /auto-ticket-rules/{id}/enable` | - | ativar regra |
| `PATCH /auto-ticket-rules/{id}/disable` | - | desativar regra |
| `DELETE /auto-ticket-rules/{id}` | - | `204` |
| `POST /auto-ticket-rules/{id}/dry-run` | - | simulacao da regra |
| `POST /auto-ticket-rules/seed-defaults` | - | popular regras padrao |
| `GET /auto-ticket-rules/{id}/stats` | - | estatisticas da regra |

### 7.14 Escalation Rules

Base: `/api/v1/escalation-rules`

| Endpoint | Request | Response |
|---|---|---|
| `GET /escalation-rules` | - | regras |
| `GET /escalation-rules/by-profile/{workflowProfileId}` | - | regras do perfil |
| `GET /escalation-rules/{id}` | - | regra |
| `POST /escalation-rules` | `CreateEscalationRuleDto` | `201` |
| `PUT /escalation-rules/{id}` | `UpdateEscalationRuleDto` | `204` |
| `DELETE /escalation-rules/{id}` | - | `204` |

### 7.15 Monitoring Events

Base: `/api/v1/monitoring-events`

| Endpoint | Request | Response |
|---|---|---|
| `POST /monitoring-events` | `CreateMonitoringEventDto` | `201` |
| `POST /monitoring-events/{id}/evaluate` | - | reavaliar evento |
| `GET /monitoring-events/{id}/auto-ticket-decisions` | - | decisoes automaticas |

## 8. Agents, inventario e comandos

Base: `/api/v1/agents`

### 8.1 CRUD e visoes por escopo

| Endpoint | Request | Response |
|---|---|---|
| `GET /agents/by-site/{siteId}` | - | agentes do site |
| `GET /agents/by-client/{clientId}` | - | agentes do cliente |
| `GET /agents/{id}` | - | agente |
| `POST /agents` | `CreateAgentRequest` `{ siteId, hostname, displayName?, operatingSystem?, osVersion?, agentVersion? }` | `201` |
| `PUT /agents/{id}` | `UpdateAgentRequest` `{ siteId, hostname, displayName? }` | agente atualizado |
| `DELETE /agents/{id}` | - | `204` |
| `POST /agents/{agentId}/approve-zero-touch` | - | `{ message, agentId }` |

### 8.2 Custom fields do agent

| Endpoint | Request | Response |
|---|---|---|
| `GET /agents/{id}/custom-fields` | query `includeSecrets?` | valores |
| `PUT /agents/{id}/custom-fields/{definitionId}` | `{ value }` | valor salvo |

### 8.3 Comandos e tokens do agent

| Endpoint | Request | Response |
|---|---|---|
| `GET /agents/{id}/commands?limit=50` | - | comandos recentes |
| `POST /agents/{id}/commands` | `SendCommandRequest` `{ commandType, payload }` | comando criado |
| `GET /agents/{id}/tokens` | - | tokens do agent |
| `POST /agents/{id}/tokens` | `CreateTokenRequest` `{ description? }` | `{ token, id, expiresAt }` |
| `DELETE /agents/{id}/tokens/{tokenId}` | - | `204` |
| `DELETE /agents/{id}/tokens` | - | `204` |

### 8.4 Automacao, inventario e debug remoto

| Endpoint | Request | Response |
|---|---|---|
| `POST /agents/{id}/automation/tasks/{taskId}/run-now` | vazio | resultado da execucao |
| `POST /agents/{id}/automation/scripts/{scriptId}/run-now` | vazio | resultado da execucao |
| `POST /agents/{id}/automation/force-sync` | `ForceAutomationSyncRequest` `{ policies, inventory, software, appStore }` | `200` |
| `GET /agents/{id}/automation/executions` | - | historico |
| `GET /agents/{id}/hardware` | - | `{ hardware, disks, networkAdapters, memoryModules, printers, listeningPorts, openSockets }` |
| `GET /agents/{id}/software` | query `cursor?`, `limit?`, `search?`, `order=asc|desc` | pagina cursor-based |
| `GET /agents/{id}/software/snapshot` | - | snapshot |
| `POST /agents/{id}/remote-debug/start` | `StartRemoteDebugRequest` | `RemoteDebugStartResponse` |
| `POST /agents/{id}/remote-debug/{sessionId}/stop` | vazio | `200/204` conforme implementacao |

Formato da pagina de software:

```json
{
  "items": [],
  "count": 0,
  "cursor": "guid ou null",
  "nextCursor": "guid ou null",
  "hasMore": false,
  "limit": 100,
  "search": null,
  "order": "asc"
}
```

### 8.5 Agent Labels

Base: `/api/v1/agent-labels`

| Endpoint | Request | Response |
|---|---|---|
| `GET /agent-labels/agents/{agentId}` | - | labels do agent |
| `GET /agent-labels/rules/{ruleId}/agents` | - | agents que satisfazem a regra |
| `GET /agent-labels/rules` | - | regras de label |
| `POST /agent-labels/rules` | `CreateLabelRuleDto` | `201` |
| `PUT /agent-labels/rules/{id}` | `UpdateLabelRuleDto` | `204` |
| `DELETE /agent-labels/rules/{id}` | - | `204` |
| `POST /agent-labels/reprocess` | - | reprocessar labels |
| `POST /agent-labels/rules/dry-run` | `DryRunLabelRuleDto` | resultado simulado |
| `GET /agent-labels/rules/available-custom-fields` | - | campos disponiveis para regras |

### 8.6 Agent Alerts

Base: `/api/v1/agent-alerts`

| Endpoint | Request | Response |
|---|---|---|
| `GET /agent-alerts` | - | alertas |
| `GET /agent-alerts/{id}` | - | alerta |
| `POST /agent-alerts` | `CreateAgentAlertDto` | `201` |
| `POST /agent-alerts/{id}/dispatch` | - | disparar alerta |
| `POST /agent-alerts/{id}/create-ticket` | - | criar ticket do alerta |
| `DELETE /agent-alerts/{id}` | - | `204` |
| `GET /agent-alerts/scope-options` | - | opcoes de escopo |
| `POST /agent-alerts/test-dispatch` | `TestDispatchDto` | resultado do teste |

### 8.7 Agent Updates

Base: `/api/v1/agent-updates`

| Endpoint | Request | Response |
|---|---|---|
| `GET /agent-updates/releases` | - | releases |
| `GET /agent-updates/releases/{releaseId}` | - | release |
| `POST /agent-updates/releases` | `CreateReleaseDto` | `201` |
| `PUT /agent-updates/releases/{releaseId}` | `UpdateReleaseDto` | `204` |
| `DELETE /agent-updates/releases/{releaseId}` | - | `204` |
| `POST /agent-updates/releases/{releaseId}/promote` | - | promover release |
| `POST /agent-updates/releases/{releaseId}/artifacts` | multifile upload | artefatos |
| `DELETE /agent-updates/artifacts/{artifactId}` | - | `204` |
| `GET /agent-updates/agents/{agentId}/events` | - | eventos de update |
| `GET /agent-updates/dashboard/rollout` | - | dashboard de rollout |
| `POST /agent-updates/agents/{agentId}/force-check` | - | forcar verificacao |
| `POST /agent-updates/releases/{releaseId}/build-artifact` | `BuildArtifactDto` | build disparado |
| `POST /agent-updates/repository/sync` | - | sincronizar repositorio |
| `POST /agent-updates/repository/sync-and-build` | - | sincronizar e buildar |

### 8.8 Software Inventory (global)

Base: `/api/v1/software-inventory`

Endpoints globais para visao consolidada de software alem do escopo de um unico agent.

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /software-inventory` | `cursor?`, `limit?`, `search?` | pagina cursor-based global |
| `GET /software-inventory/snapshot` | - | snapshot global |
| `GET /software-inventory/top` | - | top software |
| `GET /software-inventory/by-client/{clientId}` | `cursor?`, `limit?`, `search?` | pagina por cliente |
| `GET /software-inventory/by-client/{clientId}/snapshot` | - | snapshot do cliente |
| `GET /software-inventory/by-site/{siteId}` | `cursor?`, `limit?`, `search?` | pagina por site |
| `GET /software-inventory/by-site/{siteId}/snapshot` | - | snapshot do site |
| `GET /software-inventory/by-site/{siteId}/top` | - | top software do site |

### 8.9 Notes

Base: `/api/v1`

Notas vinculaveis a clientes, sites ou agents.

| Endpoint | Request | Response |
|---|---|---|
| `GET /clients/{clientId}/notes` | - | notas do cliente |
| `POST /clients/{clientId}/notes` | `CreateNoteDto` | `201` |
| `GET /sites/{siteId}/notes` | - | notas do site |
| `POST /sites/{siteId}/notes` | `CreateNoteDto` | `201` |
| `GET /agents/{agentId}/notes` | - | notas do agent |
| `POST /agents/{agentId}/notes` | `CreateNoteDto` | `201` |
| `GET /notes/{id}` | - | nota por ID |
| `PUT /notes/{id}` | `UpdateNoteDto` | `204` |
| `DELETE /notes/{id}` | - | `204` |

## 9. App Store e aprovacoes de software

Base: `/api/v1/app-store`

### 9.1 Catalogo

| Endpoint | Query/Request | Response |
|---|---|---|
| `GET /app-store/catalog` | `installationType`, `search?`, `architecture?`, `limit?`, `cursor?` | `AppCatalogSearchResultDto` |
| `GET /app-store/catalog/{packageId}` | `installationType` | `AppCatalogPackageDto` |
| `POST /app-store/catalog/custom` | `UpsertCustomAppCatalogPackageRequest` | item upsertado |
| `POST /app-store/sync` | query `installationType` | `AppCatalogSyncResultDto` |

Campos de `UpsertCustomAppCatalogPackageRequest`:

- `packageId`, `name`
- `publisher?`, `version?`, `description?`
- `iconUrl?`, `siteUrl?`, `installCommand?`, `metadataJson?`
- `fileObjectKey?`, `fileBucket?`, `filePublicUrl?`, `fileContentType?`, `fileSizeBytes?`, `fileChecksum?`

Validacoes confirmadas:

- `packageId`: obrigatorio, max 300.
- `name`: obrigatorio, max 500.
- `publisher`: max 500.
- `version`: max 100.
- `iconUrl` e `siteUrl`: max 2000.
- `installCommand`: max 1000.

### 9.2 Regras de aprovacao

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /app-store/approvals` | `scopeType`, `scopeId?`, `installationType` | `{ scopeType, scopeId, installationType, count, items }` |
| `POST /app-store/approvals` | `UpsertAppApprovalRuleRequest` | regra salva |
| `DELETE /app-store/approvals/{ruleId}` | query `reason?` | `204` |
| `GET /app-store/approvals/audit` | filtros + cursor | pagina de auditoria |
| `GET /app-store/effective` | `scopeType`, `scopeId?`, `installationType`, `search?`, `limit?`, `cursor?` | `EffectiveApprovedAppPageDto` |
| `GET /app-store/diff/effective` | mesmos filtros | pagina de diff |
| `GET /app-store/diff/{packageId}` | escopo + installationType | diff de um pacote |

`UpsertAppApprovalRuleRequest`:

```json
{
  "scopeType": "Client",
  "scopeId": "guid",
  "installationType": "Winget",
  "packageId": "Google.Chrome",
  "action": "Allow",
  "autoUpdateEnabled": true,
  "reason": "Aprovado para todos os sites do cliente"
}
```

Regras de validacao:

- `packageId`: obrigatorio, max 300.
- `reason`: max 2000.
- `scopeId`: obrigatorio para `Client`, `Site` e `Agent`.
- `scopeId`: deve ser `null` quando `scopeType=Global`.

## 10. Relatorios

Base: `/api/v1/reports`

O modulo foi desenhado para o frontend descobrir schema e filtros dinamicamente, sem acoplar layout e datasets no cliente.

### 10.1 Descoberta de schema

| Endpoint | Uso |
|---|---|
| `GET /reports/datasets` | Lista datasets, campos, tipos e formatos suportados |
| `GET /reports/layout-schema` | Contrato para editor visual de layout |
| `GET /reports/autocomplete` | Sugestao de campos `alias.field` |

### 10.2 Templates, execucao e preview

| Endpoint | Request | Response |
|---|---|---|
| `POST /reports/templates` | `CreateReportTemplateRequest` | `ReportTemplateResponse` |
| `GET /reports/templates/library` | query `datasetType?` | templates built-in |
| `POST /reports/templates/library/{id}/install` | query `createdBy?` | template clonado |
| `GET /reports/templates` | query `datasetType?`, `isActive?` | templates |
| `GET /reports/templates/{id}` | - | template |
| `GET /reports/templates/{id}/history` | query `limit?` | historico |
| `PUT /reports/templates/{id}` | `UpdateReportTemplateRequest` | template atualizado |
| `DELETE /reports/templates/{id}` | - | `204` |
| `POST /reports/run` | `RunReportRequest` | `200` com metadados ou `202` se `runAsync=true` |
| `POST /reports/preview` | `PreviewReportRequest` | HTML ou arquivo binario |
| `GET /reports/executions/{id}` | query `clientId?` | execucao |
| `GET /reports/executions` | query `clientId?`, `limit?` | execucoes recentes |
| `GET /reports/executions/{id}/download` | query `clientId?` | redirect para URL presignada |
| `GET /reports/executions/{id}/download-stream` | query `clientId?` | endpoint compativel, depreciado |
| `POST /reports/schedules` | `CreateReportScheduleRequest` | agendamento criado |
| `GET /reports/schedules` | - | agendamentos |
| `GET /reports/schedules/{id}` | - | agendamento |
| `PUT /reports/schedules/{id}` | `UpdateReportScheduleRequest` | agendamento atualizado |
| `DELETE /reports/schedules/{id}` | - | `204` |

Payloads principais:

- `CreateReportTemplateRequest`: `name`, `description?`, `instructions?`, `executionSchemaJson?`, `datasetType`, `defaultFormat`, `layoutJson`, `filtersJson?`, `createdBy?`.
- `RunReportRequest`: `templateId`, `format?`, `filtersJson?`, `createdBy?`, `runAsync`.
- `PreviewReportRequest`: `templateId?`, `template?`, `format?`, `filtersJson?`, `fileName?`, `responseDisposition`, `previewMode`.

Validacoes confirmadas:

- `name`: 2-200.
- `description`: max 2000.
- `instructions`: max 4000.
- `layoutJson`: obrigatorio na criacao e precisa passar no `ReportLayoutValidator`.
- `executionSchemaJson` e `filtersJson`: JSON valido quando informados.
- `run.templateId`: obrigatorio.
- `preview.responseDisposition`: `inline` ou `attachment`.
- `preview.previewMode`: `document` ou `html`.

Observacoes importantes para o front:

- `POST /reports/preview` com `previewMode=html` retorna `text/html` e headers `X-Report-*`.
- `GET /reports/executions/{id}/download` nao baixa o binario diretamente; ele redireciona para URL presignada.
- O frontend deve preferir `GET /reports/datasets` e `GET /reports/layout-schema` para montar selectores, filtros e editores.
- Ha uma inconsistencia atual: o runtime do controller expoe `Markdown` como formato em alguns fluxos, mas os validators aceitam apenas `Xlsx`, `Csv` e `Pdf` quando habilitado. Para UI, prefira liberar so os formatos confirmados pelo endpoint de descoberta ate essa divergencia ser resolvida.

## 11. Knowledge base e vinculo com tickets

Base principal: `/api/v1/knowledge`

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /knowledge` | `clientId?`, `siteId?`, `category?`, `publishedOnly?` | `ArticleListItem[]` |
| `GET /knowledge/{id}` | - | `ArticleResponse` |
| `POST /knowledge` | `CreateArticleRequest` | `201` com `ArticleResponse` |
| `PUT /knowledge/{id}` | `UpdateArticleRequest` | `ArticleResponse` |
| `DELETE /knowledge/{id}` | - | `204` |
| `POST /knowledge/{id}/publish` | - | artigo publicado |
| `POST /knowledge/{id}/unpublish` | - | artigo despublicado |
| `GET /knowledge/search` | `q`, `clientId?`, `siteId?`, `mode`, `maxResults` | `KbSearchResult[]` |

Contratos de ticket x knowledge, expostos no mesmo controller:

| Endpoint | Request | Response |
|---|---|---|
| `GET /api/v1/tickets/{ticketId}/knowledge` | - | `TicketKnowledgeLinkResponse[]` |
| `POST /api/v1/tickets/{ticketId}/knowledge` | `LinkTicketRequest` `{ articleId, linkedBy?, note? }` | `201` |
| `DELETE /api/v1/tickets/{ticketId}/knowledge/{articleId}` | - | `204` |
| `GET /api/v1/tickets/{ticketId}/knowledge/suggest` | `q`, `clientId?`, `siteId?`, `maxResults?` | `KbSearchResult[]` |
| `POST /api/v1/tickets/{ticketId}/knowledge/{articleId}/feedback` | `KbLinkFeedbackRequest` `{ useful }` | `204` |

Validacao relevante:

- Na criacao de artigo, `siteId` so pode existir quando `clientId` tambem existir.
- `GET /knowledge/search` exige `q` nao vazio.
- `GET /tickets/{ticketId}/knowledge/suggest` exige `q` nao vazio.

## 12. Notificacoes, dashboard e realtime

### 12.1 Notificacoes HTTP

Base: `/api/v1/notifications`

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /notifications` | `recipientUserId?`, `recipientAgentId?`, `recipientKey?`, `topic?`, `severity?`, `isRead?`, `limit?` | notificacoes recentes |
| `POST /notifications` | `PublishNotificationRequest` | `201` |
| `PATCH /notifications/{id}/read` | query `recipientUserId?`, `recipientAgentId?`, `recipientKey?` | `204` |

### 12.2 Dashboard e estado do realtime

| Endpoint | Query | Response |
|---|---|---|
| `GET /api/v1/dashboard/global/summary` | `window=24h|7d|30d` | resumo global |
| `GET /api/v1/clients/{clientId}/dashboard/summary` | `window=24h|7d|30d` | resumo do cliente |
| `GET /api/v1/clients/{clientId}/sites/{siteId}/dashboard/summary` | `window=24h|7d|30d` | resumo do site |
| `GET /api/v1/realtime/status` | - | estado basico de SignalR/NATS/Redis |
| `GET /api/v1/realtime/stats` | - | diagnostico expandido |

### 12.3 NotificationHub

Hub: `/hubs/notifications`

Conexao:

- JWT no header `Authorization: Bearer <jwt>` quando possivel.
- Alternativamente, `?access_token=<jwt>` na query string para WebSocket/SignalR.

Metodos cliente -> servidor:

- `SubscribeAll()`
- `SubscribeTopic(topic)`
- `SubscribeUser(userId)`
- `SubscribeAgent(agentId)`
- `SubscribeKey(recipientKey)`
- `UnsubscribeTopic(topic)`
- `UnsubscribeUser(userId)`
- `UnsubscribeAgent(agentId)`
- `UnsubscribeKey(recipientKey)`

## 13. Configuracoes do servidor

Base: `/api/v1/configurations`

O controller de configuracoes e o mais extenso do projeto, cobrindo settings de servidor, cliente, site, AI e retencao.

### 13.1 Configuracoes de servidor

| Endpoint | Request | Response |
|---|---|---|
| `GET /configurations/server` | - | config atual |
| `PUT /configurations/server` | objeto de config | config atualizada |
| `PATCH /configurations/server` | patch parcial | config atualizada |
| `POST /configurations/server/reset` | - | reset para defaults |
| `POST /configurations/server/nats/test` | credenciais NATS | resultado do teste |
| `PATCH /configurations/server/nats` | credenciais NATS | config atualizada |
| `GET /configurations/server/metadata` | - | metadados do servidor |
| `GET /configurations/server/reporting` | - | config de reporting |
| `PUT /configurations/server/reporting` | config reporting | atualizada |
| `GET /configurations/server/ticket-attachments` | - | config de anexos |
| `PUT /configurations/server/ticket-attachments` | config anexos | atualizada |
| `GET /configurations/server/retention` | - | config de retencao |
| `PUT /configurations/server/retention` | config retencao | atualizada |
| `POST /configurations/server/retention/reset` | - | resetar retencao |
| `POST /configurations/server/retention/trigger` | - | disparar limpeza |
| `POST /configurations/server/object-storage/test` | credenciais | resultado do teste |

### 13.2 Configuracoes por cliente

| Endpoint | Request | Response |
|---|---|---|
| `GET /configurations/clients/{clientId}` | - | config do cliente |
| `GET /configurations/clients/{clientId}/effective` | - | config efetiva (com heranca) |
| `GET /configurations/clients/{clientId}/metadata` | - | metadados do cliente |
| `PUT /configurations/clients/{clientId}` | objeto de config | atualizada |
| `PATCH /configurations/clients/{clientId}` | patch parcial | atualizada |
| `DELETE /configurations/clients/{clientId}` | - | remover override |
| `POST /configurations/clients/{clientId}/reset/{propertyName}` | - | resetar propriedade |

### 13.3 Configuracoes por site

| Endpoint | Request | Response |
|---|---|---|
| `GET /configurations/sites/{siteId}` | - | config do site |
| `GET /configurations/sites/{siteId}/effective` | - | config efetiva |
| `GET /configurations/sites/{siteId}/metadata` | - | metadados do site |
| `PUT /configurations/sites/{siteId}` | objeto de config | atualizada |
| `PATCH /configurations/sites/{siteId}` | patch parcial | atualizada |
| `DELETE /configurations/sites/{siteId}` | - | remover override |
| `POST /configurations/sites/{siteId}/reset/{propertyName}` | - | resetar propriedade |

### 13.4 Configuracoes de AI

| Endpoint | Request | Response |
|---|---|---|
| `GET /configurations/ai/providers` | - | providers disponiveis |
| `GET /configurations/ai/credentials` | - | credenciais cadastradas |
| `PUT /configurations/ai/credentials` | credenciais | salvas |
| `DELETE /configurations/ai/credentials/{credentialId}` | - | `204` |
| `POST /configurations/ai/credentials/test` | credenciais | resultado do teste |
| `GET /configurations/ai/models` | - | modelos disponiveis |
| `GET /configurations/ai/models/{modelId}` | - | detalhes do modelo |
| `POST /configurations/ai/models/validate` | config do modelo | validacao |

## 14. Configuration Audit

Base: `/api/v1/configuration-audit`

| Endpoint | Request/Query | Response |
|---|---|---|
| `GET /configuration-audit` | filtros | registros de auditoria |
| `GET /configuration-audit/{entityType}/{entityId}` | - | auditoria por entidade |
| `GET /configuration-audit/{entityType}/{entityId}/field/{fieldName}` | - | auditoria por campo |
| `GET /configuration-audit/by-user/{username}` | - | auditoria por usuario |
| `GET /configuration-audit/report` | filtros | relatorio de auditoria |

## 15. MeshCentral

Base: `/api/v1/meshcentral`

| Endpoint | Request | Response |
|---|---|---|
| `GET /meshcentral/rights-profiles` | - | perfis de direitos |
| `POST /meshcentral/rights-profiles` | `CreateRightsProfileDto` | `201` |
| `PUT /meshcentral/rights-profiles/{id}` | `UpdateRightsProfileDto` | `204` |
| `DELETE /meshcentral/rights-profiles/{id}` | - | `204` |
| `GET /meshcentral/rights-profiles/usage` | - | uso dos perfis |
| `POST /meshcentral/embed-url` | `{ agentId }` | URL de embed |
| `POST /meshcentral/identity-sync/backfill` | - | backfill de identidade |
| `POST /meshcentral/node-links/backfill` | - | backfill de node links |
| `GET /meshcentral/diagnostics/health` | - | health check |
| `GET /meshcentral/group-policy/sites/{siteId}/status` | - | status de group policy |
| `POST /meshcentral/group-policy/reconcile` | - | reconciliar group policy |

## 16. Admin

### 16.1 Jobs (agendador Quartz.NET)

Base: `/api/v1/admin/jobs`

| Endpoint | Request | Response |
|---|---|---|
| `GET /admin/jobs` | - | lista de jobs |
| `GET /admin/jobs/{jobGroup}/{jobName}` | - | detalhes do job |
| `POST /admin/jobs/{jobGroup}/{jobName}/trigger` | - | disparar job |
| `POST /admin/jobs/{jobGroup}/{jobName}/pause` | - | pausar job |
| `POST /admin/jobs/{jobGroup}/{jobName}/resume` | - | resumir job |
| `POST /admin/jobs/scheduler/standby` | - | colocar scheduler em standby |
| `POST /admin/jobs/scheduler/start` | - | iniciar scheduler |

### 16.2 Background Services

Base: `/api/v1/admin/background-services`

| Endpoint | Request | Response |
|---|---|---|
| `GET /admin/background-services` | - | lista de servicos |
| `GET /admin/background-services/{name}` | - | detalhes do servico |
| `GET /admin/dashboard` | - | dashboard administrativo |

### 16.3 Logs

Base: `/api/v1/logs`

| Endpoint | Request | Response |
|---|---|---|
| `GET /logs` | filtros | registros de log |
| `POST /logs` | entrada de log | `201` |

## 17. RemoteDebugHub

Hub: `/hubs/remote-debug`

Conexao: mesma estrategia do NotificationHub (JWT via header ou query string).

Metodos cliente -> servidor:

- `JoinSession(sessionId: Guid)` — entra em uma sessao de debug remoto
- `LeaveSession(sessionId: Guid)` — sai da sessao
- `CloseSession(sessionId: Guid, reason: string?)` — encerra a sessao

Metodos servidor -> cliente:

- `RemoteDebugSessionJoined` — `{ sessionId, agentId, startedAtUtc, expiresAtUtc, preferredTransport, fallbackTransport, natsSubject, signalRMethod }`
- `RemoteDebugSessionEnded` — `{ sessionId, endedAtUtc, reason }`

| Grupo | Rotas base principais |
|---|---|
| Identidade | `/auth`, `/mfa`, `/users`, `/roles`, `/user-groups`, `/api-tokens` |
| Cadastro | `/clients`, `/clients/{clientId}/sites`, `/custom-fields`, `/departments`, `/workflow`, `/workflowprofiles` |
| Service desk | `/tickets`, `/tickets/kpi`, `/ticket-saved-views`, `/tickets/{ticketId}/watchers`, `/tickets/{ticketId}/custom-fields`, `/ticket-alert-rules`, `/knowledge`, `/notifications`, `/monitoring-events`, `/notes` |
| Agents | `/agents`, `/agent-labels`, `/agent-updates`, `/agent-alerts`, `/software-inventory`, `/agent-install`, `/deploy-tokens` |
| Automacao e software | `/automation/scripts`, `/automation/tasks`, `/app-store` |
| Observabilidade e admin | `/reports`, `/dashboard/*`, `/realtime`, `/logs`, `/configuration-audit`, `/configurations`, `/meshcentral`, `/admin/jobs`, `/admin/background-services` |

## 18. Inventario resumido de rotas relevantes ao front

| Grupo | Rotas base principais |
|---|---|
| Identidade | `/auth`, `/mfa`, `/users`, `/roles`, `/user-groups`, `/api-tokens` |
| Cadastro | `/clients`, `/clients/{clientId}/sites`, `/custom-fields`, `/departments`, `/workflow`, `/workflowprofiles`, `/sla-calendars` |
| Service desk | `/tickets`, `/tickets/kpi`, `/ticket-saved-views`, `/tickets/{ticketId}/watchers`, `/tickets/{ticketId}/custom-fields`, `/tickets/{ticketId}/audit`, `/tickets/{ticketId}/automation-links`, `/tickets/{ticketId}/remote-sessions`, `/tickets/{id}/ai`, `/tickets/{ticketId}/sla`, `/ticket-alert-rules`, `/auto-ticket-rules`, `/escalation-rules`, `/monitoring-events`, `/knowledge`, `/notifications`, `/notes` |
| Agents | `/agents`, `/agent-labels`, `/agent-updates`, `/agent-alerts`, `/software-inventory`, `/agent-install`, `/deploy-tokens` |
| Automacao e software | `/automation/scripts`, `/automation/tasks`, `/app-store` |
| Configuracoes | `/configurations`, `/configuration-audit` |
| Observabilidade e admin | `/reports`, `/dashboard/*`, `/realtime`, `/logs`, `/meshcentral`, `/admin/jobs`, `/admin/background-services`, `/admin/dashboard` |
| Realtime (hubs) | `/hubs/notifications`, `/hubs/remote-debug` |

## 19. Validacao de dados: estrategia recomendada para o frontend

### 19.1 Regras que valem espelhar no cliente

- Tickets: titulo 3-200, descricao 3-10000, categoria max 100, comentario 3-4000.
- Clientes: nome 2-200, notes max 2000.
- API tokens: nome max 200, expiracao futura.
- App Store: `packageId` max 300, `reason` max 2000, `scopeId` depende do `scopeType`.
- Reports: `layoutJson` precisa ser JSON valido e obedecer o schema exposto pelo backend.
- Knowledge: `q` obrigatorio em busca/sugestao; `siteId` depende de `clientId`.

### 19.2 Regras que nao devem ser hardcoded

- Politica de senha: o backend valida via `PasswordService` e retorna a mensagem final.
- Custom fields: regex, min/max, enum e segredos vem do schema dinamico.
- Permissoes por usuario/role/scope: o front deve reagir ao `403`, nao tentar inferir tudo localmente.
- Workflow de tickets: transicoes validas continuam sendo autoridade do backend.

### 19.3 Normalizacao de erro sugerida

Como os formatos nao sao uniformes, uma camada de erro no frontend deve procurar nessa ordem:

1. `response.data.errors` para `ValidationProblemDetails`.
2. `response.data.message`.
3. `response.data.error`.
4. `response.data.code` combinado com `message`.
5. Fallback generico por status code.

## 20. Recomendacoes de implementacao do front

1. Gerar tipos a partir de `/openapi/v1.json`, mas manter overrides locais para endpoints cujo request/response mora inline no controller.
2. Implementar a autenticacao como maquina de estados: `login -> mfa_pending -> mfa_setup -> full_session`.
3. Centralizar refresh de token, tratamento de `401` e redirecionamento para login.
4. Tratar `403` como problema de permissao e nao como problema de sessao.
5. Modelar paginacao por modulo: `limit/offset` em tickets, cursor em app store e software inventory, listas simples em varios CRUDs.
6. Usar endpoints de schema (`custom-fields`, `reports/datasets`, `reports/layout-schema`) para formularios dinamicos.
7. Implementar upload de anexos e download de relatarios com suporte a URL presignada/redirect.

## 21. Arquivos de referencia

- [Program.cs](../src/Discovery.Api/Program.cs)
- [AuthController.cs](../src/Discovery.Api/Controllers/AuthController.cs)
- [MfaController.cs](../src/Discovery.Api/Controllers/MfaController.cs)
- [UsersController.cs](../src/Discovery.Api/Controllers/UsersController.cs)
- [RolesController.cs](../src/Discovery.Api/Controllers/RolesController.cs)
- [UserGroupsController.cs](../src/Discovery.Api/Controllers/UserGroupsController.cs)
- [ApiTokensController.cs](../src/Discovery.Api/Controllers/ApiTokensController.cs)
- [ClientsController.cs](../src/Discovery.Api/Controllers/ClientsController.cs)
- [SitesController.cs](../src/Discovery.Api/Controllers/SitesController.cs)
- [CustomFieldsController.cs](../src/Discovery.Api/Controllers/CustomFieldsController.cs)
- [DepartmentsController.cs](../src/Discovery.Api/Controllers/DepartmentsController.cs)
- [WorkflowController.cs](../src/Discovery.Api/Controllers/WorkflowController.cs)
- [WorkflowProfilesController.cs](../src/Discovery.Api/Controllers/WorkflowProfilesController.cs)
- [SlaCalendarsController.cs](../src/Discovery.Api/Controllers/SlaCalendarsController.cs)
- [DeployTokensController.cs](../src/Discovery.Api/Controllers/DeployTokensController.cs)
- [AgentInstallController.cs](../src/Discovery.Api/Controllers/AgentInstallController.cs)
- [TicketsController.cs](../src/Discovery.Api/Controllers/TicketsController.cs)
- [TicketKpiController.cs](../src/Discovery.Api/Controllers/TicketKpiController.cs)
- [TicketSavedViewsController.cs](../src/Discovery.Api/Controllers/TicketSavedViewsController.cs)
- [TicketWatchersController.cs](../src/Discovery.Api/Controllers/TicketWatchersController.cs)
- [TicketCustomFieldsController.cs](../src/Discovery.Api/Controllers/TicketCustomFieldsController.cs)
- [TicketAuditController.cs](../src/Discovery.Api/Controllers/TicketAuditController.cs)
- [TicketAutomationLinksController.cs](../src/Discovery.Api/Controllers/TicketAutomationLinksController.cs)
- [TicketRemoteSessionsController.cs](../src/Discovery.Api/Controllers/TicketRemoteSessionsController.cs)
- [TicketAiController.cs](../src/Discovery.Api/Controllers/TicketAiController.cs)
- [TicketSlaController.cs](../src/Discovery.Api/Controllers/TicketSlaController.cs)
- [TicketAlertRulesController.cs](../src/Discovery.Api/Controllers/TicketAlertRulesController.cs)
- [AutoTicketRulesController.cs](../src/Discovery.Api/Controllers/AutoTicketRulesController.cs)
- [EscalationRulesController.cs](../src/Discovery.Api/Controllers/EscalationRulesController.cs)
- [MonitoringEventsController.cs](../src/Discovery.Api/Controllers/MonitoringEventsController.cs)
- [NotesController.cs](../src/Discovery.Api/Controllers/NotesController.cs)
- [KnowledgeController.cs](../src/Discovery.Api/Controllers/KnowledgeController.cs)
- [AgentsController.cs](../src/Discovery.Api/Controllers/Agents/AgentsController.cs)
- [AgentLabelsController.cs](../src/Discovery.Api/Controllers/AgentLabelsController.cs)
- [AgentAlertsController.cs](../src/Discovery.Api/Controllers/AgentAlertsController.cs)
- [AgentUpdatesController.cs](../src/Discovery.Api/Controllers/AgentUpdatesController.cs)
- [SoftwareInventoryController.cs](../src/Discovery.Api/Controllers/SoftwareInventoryController.cs)
- [AutomationScriptsController.cs](../src/Discovery.Api/Controllers/AutomationScriptsController.cs)
- [AutomationTasksController.cs](../src/Discovery.Api/Controllers/AutomationTasksController.cs)
- [AppStoreController.cs](../src/Discovery.Api/Controllers/AppStoreController.cs)
- [ReportsController.cs](../src/Discovery.Api/Controllers/ReportsController.cs)
- [DashboardController.cs](../src/Discovery.Api/Controllers/DashboardController.cs)
- [RealtimeController.cs](../src/Discovery.Api/Controllers/RealtimeController.cs)
- [ConfigurationsController.cs](../src/Discovery.Api/Controllers/ConfigurationsController.cs)
- [ConfigurationAuditController.cs](../src/Discovery.Api/Controllers/ConfigurationAuditController.cs)
- [MeshCentralController.cs](../src/Discovery.Api/Controllers/MeshCentralController.cs)
- [JobsController.cs](../src/Discovery.Api/Controllers/JobsController.cs)
- [BackgroundServicesController.cs](../src/Discovery.Api/Controllers/BackgroundServicesController.cs)
- [LogsController.cs](../src/Discovery.Api/Controllers/LogsController.cs)
- [NotificationsController.cs](../src/Discovery.Api/Controllers/NotificationsController.cs)
- [NotificationHub.cs](../src/Discovery.Api/Hubs/NotificationHub.cs)
- [RemoteDebugHub.cs](../src/Discovery.Api/Hubs/RemoteDebugHub.cs)
- [TicketValidators.cs](../src/Discovery.Api/Validators/TicketValidators.cs)
- [ClientValidators.cs](../src/Discovery.Api/Validators/ClientValidators.cs)
- [ApiTokenValidators.cs](../src/Discovery.Api/Validators/ApiTokenValidators.cs)
- [AppStoreValidators.cs](../src/Discovery.Api/Validators/AppStoreValidators.cs)
- [ReportValidators.cs](../src/Discovery.Api/Validators/ReportValidators.cs)