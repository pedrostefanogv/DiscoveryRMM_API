# Guia de Implementacao - Autenticacao no Front

Data: 2026-03-15
Status: Baseado na implementacao atual do backend

## Objetivo

Documentar os contratos reais da API para o frontend implementar:
- login
- primeiro acesso
- MFA FIDO2/WebAuthn
- refresh/logout
- listagem e gestao de chaves MFA
- abertura segura do MeshCentral embutido

Este documento descreve o comportamento atual do backend, incluindo detalhes de payload, tipos de dados, validacoes efetivas e inconsistencias relevantes para a implementacao do front.

---

## Escopo Atual do Backend

O backend ja suporta:
- autenticacao de usuario com login ou email + senha
- onboarding de primeiro acesso
- MFA obrigatorio via FIDO2/WebAuthn
- emissao de access token e refresh token
- listagem, cadastro, remocao e rename de chaves MFA
- endpoint protegido para gerar URL de embed do MeshCentral

O backend ainda nao expoe:
- OTP/TOTP para uso produtivo no front
- endpoint de cadastro publico de usuario
- endpoint de perfil do usuario autenticado no fluxo de auth
- politica de senha publicada por endpoint proprio

---

## Convencoes Gerais

### Base path

Todos os endpoints abaixo usam a API principal do Discovery.

### Formato JSON

- O backend aceita propriedades em camelCase ou PascalCase.
- Para o frontend, padronizar sempre camelCase.
- Os exemplos deste documento usam camelCase.

### Autorizacao

Existem 3 tipos de token relevantes para o front:

1. mfa_pending
- JWT temporario para concluir MFA no login.
- Nao pode acessar endpoints protegidos de sessao completa.

2. mfa_setup
- JWT temporario para onboarding e cadastro inicial de chave FIDO2.
- Pode acessar endpoints de first access e de registro MFA.

3. accessToken
- JWT de sessao completa.
- Deve ser usado em endpoints protegidos normais.

O refreshToken nao vai em header. Ele e enviado apenas no corpo do endpoint de refresh.

### Header padrao

Quando houver autenticacao por bearer token:

```http
Authorization: Bearer {token}
```

### Duracao de sessao

- accessToken: 900 segundos
- refresh token: sessao persistida por 7 dias no backend

---

## Fluxo Resumido

### Login padrao

1. POST /api/auth/login
2. Receber mfaToken
3. Se firstAccessRequired = true:
   - GET /api/auth/first-access/status
   - POST /api/auth/first-access/complete
   - POST /api/mfa/fido2/register/begin
   - POST /api/mfa/fido2/register/complete
4. Se mfaConfigured = true:
   - POST /api/auth/mfa/fido2/begin
   - POST /api/auth/mfa/fido2/complete
5. Receber accessToken + refreshToken
6. Operar normalmente
7. Quando expirar accessToken, chamar POST /api/auth/refresh

---

## Modelos TypeScript

Sugestao de modelos para uso direto no front.

```ts
export interface LoginRequest {
  loginOrEmail: string;
  password: string;
}

export interface LoginResponse {
  mfaToken: string;
  mfaRequired: boolean;
  mfaConfigured: boolean;
  firstAccessRequired: boolean;
  mustChangePassword: boolean;
  mustChangeProfile: boolean;
}

export interface TokenPair {
  accessToken: string;
  refreshToken: string;
  expiresInSeconds: number;
}

export interface RefreshTokenRequest {
  refreshToken: string;
}

export interface FirstAccessStatus {
  firstAccessRequired: boolean;
  mustChangePassword: boolean;
  mustChangeProfile: boolean;
  mfaRequired: boolean;
  mfaConfigured: boolean;
}

export interface CompleteFirstAccessRequest {
  newLogin: string;
  newEmail: string;
  newFullName: string;
  currentPassword: string;
  newPassword: string;
}

export interface BeginFido2Response {
  options: string;
}

export interface CompleteFido2AssertionRequest {
  assertionResponseJson: string;
}

export interface CompleteFido2RegistrationRequest {
  attestationResponseJson: string;
  keyName: string;
}

export interface CompleteFido2RegistrationResponse {
  keyId: string;
  message: string;
}

export interface MfaKey {
  id: string;
  name: string;
  keyType: MfaKeyType;
  isActive: boolean;
  createdAt: string;
  lastUsedAt: string | null;
}

export type MfaKeyType = 0 | 1;
// 0 = Fido2
// 1 = Totp (preparado para futuro, nao usar como fluxo principal hoje)

export interface RenameMfaKeyRequest {
  keyName: string;
}

export interface MeshCentralEmbedUrlRequest {
  clientId: string;
  siteId: string;
  meshUsername: string;
  agentId?: string | null;
  viewMode?: number | null;
  hideMask?: number | null;
  meshNodeId?: string | null;
  gotoDeviceName?: string | null;
}

export interface MeshCentralEmbedUrlResponse {
  url: string;
  expiresAtUtc: string;
  viewMode: number;
  hideMask: number;
  clientId: string;
  siteId: string;
  agentId?: string | null;
  meshUsername: string;
}

export interface ApiMessageResponse {
  message: string;
}

export interface ApiErrorResponse {
  error?: string;
  message?: string;
  timestamp?: string;
  traceId?: string;
}
```

---

## Endpoints

## 1. Login

### POST /api/auth/login

### Request

```json
{
  "loginOrEmail": "admin",
  "password": "Senha@Forte2026"
}
```

### Response 200

```json
{
  "mfaToken": "jwt-temporario",
  "mfaRequired": true,
  "mfaConfigured": true,
  "firstAccessRequired": false,
  "mustChangePassword": false,
  "mustChangeProfile": false
}
```

### Regras de interpretacao

- firstAccessRequired = true
  - abrir wizard de primeiro acesso
  - armazenar mfaToken como token temporario de onboarding

- mfaRequired = true e mfaConfigured = false
  - abrir cadastro inicial de chave FIDO2
  - usar mfaToken como token de setup

- mfaRequired = true e mfaConfigured = true
  - iniciar assercao FIDO2
  - usar mfaToken como token mfa_pending

### Validacoes efetivas no backend

- loginOrEmail: precisa corresponder a um usuario existente por login ou email
- password: precisa bater com o hash salvo
- usuario precisa estar ativo

### Observacao importante

Hoje o service de login lanca UnauthorizedAccessException para credenciais invalidas ou conta desativada. Esse erro nao e tratado diretamente no controller e passa pelo middleware global de excecao.

Na implementacao atual, isso pode resultar em resposta 500 com payload generico:

```json
{
  "error": "Erro interno do servidor",
  "timestamp": "...",
  "traceId": "..."
}
```

No front, trate qualquer resposta nao-2xx desse endpoint como falha de autenticacao e mostre mensagem generica de credenciais invalidas, sem depender de 401.

---

## 2. Iniciar MFA FIDO2 no login

### POST /api/auth/mfa/fido2/begin

### Header

```http
Authorization: Bearer {mfaToken}
```

### Response 200

```json
{
  "options": "{...json serializado do challenge WebAuthn...}"
}
```

### Uso no front

- fazer JSON.parse em options
- chamar navigator.credentials.get com esse objeto
- serializar a resposta WebAuthn para JSON e enviar no endpoint de complete

### Erros esperados

- 401 com message quando o token nao for mfa_pending
- 401 se o token estiver ausente ou invalido

---

## 3. Concluir MFA FIDO2 no login

### POST /api/auth/mfa/fido2/complete

### Header

```http
Authorization: Bearer {mfaToken}
```

### Request

```json
{
  "assertionResponseJson": "{...json serializado do AuthenticatorAssertionRawResponse...}"
}
```

### Response 200

```json
{
  "accessToken": "jwt",
  "refreshToken": "base64",
  "expiresInSeconds": 900
}
```

### Response 401

```json
{
  "message": "MFA invalido."
}
```

ou a mensagem especifica retornada pelo provider FIDO2.

### Validacoes efetivas

- o token precisa ser mfa_pending
- a assertion precisa ser valida para uma chave ativa do usuario

---

## 4. Refresh de sessao

### POST /api/auth/refresh

### Request

```json
{
  "refreshToken": "base64"
}
```

### Response 200

```json
{
  "accessToken": "jwt",
  "refreshToken": "base64",
  "expiresInSeconds": 900
}
```

### Observacao importante

O service de refresh tambem lanca UnauthorizedAccessException para token invalido ou expirado. Na implementacao atual, isso pode resultar em 500 generico em vez de 401.

No front:
- se refresh falhar com qualquer status nao-2xx, limpar sessao local
- redirecionar para login

---

## 5. Status de primeiro acesso

### GET /api/auth/first-access/status

### Header

```http
Authorization: Bearer {mfaSetupToken ou accessToken}
```

### Response 200

```json
{
  "firstAccessRequired": true,
  "mustChangePassword": true,
  "mustChangeProfile": true,
  "mfaRequired": true,
  "mfaConfigured": false
}
```

### Regras de acesso

- aceita mfa_setup
- aceita accessToken
- rejeita mfa_pending

---

## 6. Concluir primeiro acesso

### POST /api/auth/first-access/complete

### Header

```http
Authorization: Bearer {mfaSetupToken ou accessToken}
```

### Request

```json
{
  "newLogin": "admin.master",
  "newEmail": "admin@empresa.com",
  "newFullName": "Administrador Master",
  "currentPassword": "Mudar@123",
  "newPassword": "Senha@Forte2026"
}
```

### Response 200

```json
{
  "message": "Primeiro acesso concluido. Finalize o cadastro do MFA para liberar o login completo."
}
```

### Response 400

```json
{
  "message": "Login ja em uso."
}
```

ou:

```json
{
  "message": "E-mail ja em uso."
}
```

ou mensagem da politica de senha.

### Response 401

```json
{
  "message": "Senha atual invalida."
}
```

### Validacoes efetivas no backend

- currentPassword precisa coincidir com a senha atual
- newLogin deve ser unico quando alterado
- newEmail deve ser unico quando alterado
- newPassword precisa atender a politica configurada

### Politica de senha atual

Implementada em UserPasswordService:

- minimo padrao: 12 caracteres
- requer maiuscula: sim
- requer numero: sim
- requer caractere especial: sim

Mensagens reais hoje:

- A senha deve ter no minimo 12 caracteres.
- A senha deve conter pelo menos uma letra maiuscula.
- A senha deve conter pelo menos um numero.
- A senha deve conter pelo menos um caractere especial.

Observacao:
- esses valores podem ser sobrescritos por configuracao em Authentication:PasswordPolicy
- o frontend deve validar localmente pelo menos essa regra minima padrao, mas continuar exibindo a mensagem retornada pela API como fonte de verdade

---

## 7. Listar chaves MFA

### GET /api/mfa/keys

### Header

```http
Authorization: Bearer {accessToken}
```

### Response 200

```json
[
  {
    "id": "guid",
    "name": "YubiKey 5C",
    "keyType": 0,
    "isActive": true,
    "createdAt": "2026-03-15T18:00:00Z",
    "lastUsedAt": "2026-03-15T18:10:00Z"
  }
]
```

### Tipo de keyType

- 0 = Fido2
- 1 = Totp

---

## 8. Iniciar registro FIDO2

### POST /api/mfa/fido2/register/begin

### Header

```http
Authorization: Bearer {mfaSetupToken ou accessToken}
```

### Response 200

```json
{
  "options": "{...json serializado do challenge de registro WebAuthn...}"
}
```

### Regras de acesso

- aceita mfa_setup
- aceita accessToken
- rejeita mfa_pending

---

## 9. Concluir registro FIDO2

### POST /api/mfa/fido2/register/complete

### Header

```http
Authorization: Bearer {mfaSetupToken ou accessToken}
```

### Request

```json
{
  "attestationResponseJson": "{...json serializado do AuthenticatorAttestationRawResponse...}",
  "keyName": "Notebook Pedro"
}
```

### Response 200

```json
{
  "keyId": "guid",
  "message": "Chave registrada com sucesso."
}
```

### Response 400

```json
{
  "message": "Falha ao registrar a chave."
}
```

ou a mensagem especifica do provider FIDO2.

### Validacoes efetivas

- attestationResponseJson precisa ser um JSON valido da resposta WebAuthn
- keyName pode vir vazio; nesse caso o backend grava Chave de seguranca

### Validacao recomendada no front

- exigir keyName entre 2 e 80 caracteres para melhorar UX
- bloquear envio de string vazia se a tela pedir nome amigavel obrigatorio

---

## 10. Remover chave MFA

### DELETE /api/mfa/keys/{keyId}

### Header

```http
Authorization: Bearer {accessToken}
```

### Response 204

Sem corpo.

### Response 400

```json
{
  "message": "Nao e possivel remover a unica chave ativa."
}
```

### Response 404

Sem corpo.

### Regra importante

O backend impede remover a ultima chave ativa do usuario.

---

## 11. Renomear chave MFA

### PATCH /api/mfa/keys/{keyId}/name

### Header

```http
Authorization: Bearer {accessToken}
```

### Request

```json
{
  "keyName": "iPhone Passkey"
}
```

### Response 204

Sem corpo.

### Response 404

Sem corpo.

### Validacao recomendada no front

- obrigatorio
- trim
- minimo sugerido de 2 caracteres
- maximo sugerido de 80 caracteres

---

## 12. Logout

### POST /api/auth/logout

### Header

```http
Authorization: Bearer {accessToken}
```

### Request

```json
{
  "refreshToken": "base64"
}
```

### Response 204

Sem corpo.

### Observacao importante

Na implementacao atual, o controller usa o SessionId extraido do accessToken e nao depende do refreshToken enviado no body para revogar a sessao.

Mesmo assim, manter o envio do contrato atual para preservar compatibilidade futura.

### Response 400

```json
{
  "message": "Sessao invalida para logout."
}
```

---

## Implementacao WebAuthn no Front

### Begin e complete de assertion

1. chamar POST /api/auth/mfa/fido2/begin
2. receber options como string JSON
3. converter para objeto WebAuthn
4. chamar navigator.credentials.get
5. serializar ArrayBuffers para base64url ou estrutura equivalente exigida pela biblioteca cliente
6. enviar assertionResponseJson em POST /api/auth/mfa/fido2/complete

### Begin e complete de registration

1. chamar POST /api/mfa/fido2/register/begin
2. receber options como string JSON
3. converter para objeto WebAuthn
4. chamar navigator.credentials.create
5. serializar resposta
6. enviar attestationResponseJson em POST /api/mfa/fido2/register/complete

### Recomendacao tecnica

No front, criar helpers separados para:
- parse de options vindas como string
- serializacao de PublicKeyCredential em JSON transportavel
- desserializacao de campos base64url

---

## Estados Sugeridos no Front

```ts
export type AuthStage =
  | 'anonymous'
  | 'login-submitting'
  | 'first-access'
  | 'mfa-register-begin'
  | 'mfa-register-complete'
  | 'mfa-assert-begin'
  | 'mfa-assert-complete'
  | 'authenticated'
  | 'refreshing'
  | 'logout';
```

### Estrutura sugerida de sessao local

```ts
export interface AuthSessionState {
  stage: AuthStage;
  accessToken: string | null;
  refreshToken: string | null;
  temporaryMfaToken: string | null;
  expiresAt: number | null;
  loginResponse: LoginResponse | null;
}
```

---

## Validacoes de UI Recomendadas

## Login

- loginOrEmail: obrigatorio
- password: obrigatorio

## Primeiro acesso

- newLogin: obrigatorio, trim
- newEmail: obrigatorio, email valido
- newFullName: obrigatorio, trim
- currentPassword: obrigatorio
- newPassword: obrigatorio
- confirmar senha no front mesmo que a API nao tenha campo de confirmacao

## Registro de chave MFA

- keyName: obrigatorio do ponto de vista de UX
- fallback aceitavel: enviar vazio e deixar o backend gravar Chave de seguranca

## Renomear chave

- keyName: obrigatorio
- aplicar trim antes de enviar

---

## Tratamento de Erros

### Formatos possiveis de erro

O frontend deve suportar pelo menos 3 formatos:

1. erro de dominio por controller

```json
{ "message": "Texto do erro" }
```

2. erro generico do middleware global

```json
{
  "error": "Erro interno do servidor",
  "timestamp": "...",
  "traceId": "..."
}
```

3. erro sem corpo ou vazio

### Estrategia recomendada

- se existir message, mostrar message
- senao, se existir error, mostrar error
- senao, mostrar mensagem generica amigavel

### Mensagens genericas sugeridas

- login/refresh falhou: Nao foi possivel autenticar sua sessao.
- MFA falhou: Nao foi possivel validar sua chave de seguranca.
- primeiro acesso falhou: Nao foi possivel concluir o primeiro acesso.
- erro inesperado: Ocorreu um erro inesperado. Tente novamente.

---

## Armazenamento de Tokens

### Recomendacao minima

- accessToken: manter em memoria quando possivel
- refreshToken: armazenar de acordo com a politica de seguranca do front

### Recomendacao pratica

- SPAs: accessToken em memoria + refreshToken em storage protegido conforme risco aceito
- ao iniciar app, se existir refreshToken, tentar refresh silencioso
- se refresh falhar, limpar tudo e voltar ao login

---

## Refresh Automatizado

### Regra recomendada

- renovar accessToken pouco antes do vencimento
- se uma chamada protegida retornar 401, tentar 1 refresh
- se refresh falhar, encerrar sessao

### Importante

Como o backend atual pode devolver 500 generico em vez de 401 em alguns cenarios de auth, o front deve tratar refresh com regra de falha por status nao-2xx, e nao apenas por 401.

---

## MeshCentral no Front

O endpoint de embed esta protegido e exige sessao autenticada.

### POST /api/meshcentral/embed-url

### Header

```http
Authorization: Bearer {accessToken}
```

### Request

```json
{
  "clientId": "guid",
  "siteId": "guid",
  "meshUsername": "usuario.mesh",
  "agentId": "guid-opcional",
  "viewMode": 11,
  "hideMask": 15,
  "meshNodeId": "nodeid-opcional",
  "gotoDeviceName": "nome-opcional"
}
```

### Response 200

```json
{
  "url": "https://mesh.exemplo.com/?auth=...&viewmode=11&hide=15",
  "expiresAtUtc": "2026-03-15T20:00:00Z",
  "viewMode": 11,
  "hideMask": 15,
  "clientId": "guid",
  "siteId": "guid",
  "agentId": "guid",
  "meshUsername": "usuario.mesh"
}
```

### Uso correto no front

- solicitar a URL ao backend
- abrir a URL recebida em iframe
- nunca gerar auth do MeshCentral no browser
- nunca expor login key ou credenciais do MeshCentral no front

### ViewModes permitidos hoje no backend

- 10: device general
- 11: remote desktop
- 12: terminal
- 13: files
- 16: events

### HideMask default

- 15 = oculta header, tabs, footer e titulo

---

## Ponto de Atencao para o Front

### 1. Nao assumir 401 em todos os erros de autenticacao

Login e refresh ainda dependem de melhoria no backend para padronizar UnauthorizedAccessException como 401. Hoje o front precisa tratar falhas por qualquer status nao-2xx.

### 2. Nao usar mfa_pending como sessao normal

Esse token serve apenas para completar MFA.

### 3. Nao pular first access

Se firstAccessRequired vier true, o usuario ainda nao deve acessar o dashboard.

### 4. Nao abrir MeshCentral sem passar pelo backend

O embed depende de URL assinada e deve continuar mediado pela API.

---

## Checklist de Implementacao do Front

1. Criar modulo auth com tipos deste documento.
2. Implementar login e roteamento por estado.
3. Implementar utilitarios WebAuthn para begin/complete.
4. Implementar wizard de primeiro acesso.
5. Implementar refresh automatico e limpeza de sessao.
6. Implementar pagina de chaves MFA.
7. Implementar wrapper de iframe para MeshCentral.
8. Centralizar parser de erro para suportar message e error.

---

## Referencias Internas

- AuthController
- MfaController
- UserAuthService
- UserPasswordService
- AuthFilters
- UserAuthMiddleware
- ExceptionHandlingMiddleware
- MeshCentralController
