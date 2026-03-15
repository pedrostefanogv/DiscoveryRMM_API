# First Access Front Flow

## Objetivo
Definir o fluxo de frontend para onboarding do primeiro acesso de usuarios seeded (ex.: admin inicial).

## Endpoints

### 1) Login inicial
`POST /api/auth/login`

Request:
```json
{
  "loginOrEmail": "admin",
  "password": "Mudar@123"
}
```

Resposta esperada para primeiro acesso:
```json
{
  "mfaToken": "...",
  "mfaRequired": true,
  "mfaConfigured": false,
  "firstAccessRequired": true,
  "mustChangePassword": true,
  "mustChangeProfile": true
}
```

Guardar `mfaToken` temporariamente e usar como `Authorization: Bearer {mfaToken}`.

### 2) Status de onboarding
`GET /api/auth/first-access/status`

Header:
`Authorization: Bearer {mfaToken}`

Resposta:
```json
{
  "firstAccessRequired": true,
  "mustChangePassword": true,
  "mustChangeProfile": true,
  "mfaRequired": true,
  "mfaConfigured": false
}
```

### 3) Concluir primeiro acesso (perfil + senha)
`POST /api/auth/first-access/complete`

Header:
`Authorization: Bearer {mfaToken}`

Request:
```json
{
  "newLogin": "admin.master",
  "newEmail": "admin@empresa.com",
  "newFullName": "Administrador Master",
  "currentPassword": "Mudar@123",
  "newPassword": "Senha@Forte2026"
}
```

Resposta:
`200 OK` com mensagem de sucesso.

### 4) Cadastro MFA (FIDO2)
Fluxo ja existente:
- `POST /api/mfa/fido2/register/begin`
- `POST /api/mfa/fido2/register/complete`

Header:
`Authorization: Bearer {mfaToken}`

### 5) Login normal apos onboarding/MFA
- `POST /api/auth/login`
- se `mfaPending`, executar:
  - `POST /api/auth/mfa/fido2/begin`
  - `POST /api/auth/mfa/fido2/complete`

## Regras de UI
1. Se `firstAccessRequired = true`: abrir wizard de primeiro acesso.
2. Bloquear acesso ao dashboard ate finalizar:
- troca de perfil/senha
- cadastro MFA
3. Se `mfaConfigured = false`: direcionar obrigatoriamente para tela de cadastro FIDO2.
4. Exibir erros de API inline por campo (login/email/senha atual/politica de senha).

## Estados sugeridos no frontend
- `auth.initialLogin`
- `auth.firstAccess.editProfilePassword`
- `auth.firstAccess.setupMfa`
- `auth.mfa.pendingAssertion`
- `auth.ready`

## Erros esperados
- `401 Unauthorized`: senha atual invalida/token invalido
- `400 BadRequest`: login ou email em uso, politica de senha nao atendida
