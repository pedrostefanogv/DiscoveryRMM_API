# Plano de Implementação - Autenticação de Usuários + ACLs + MFA (FIDO2/OTP)

Data: 2026-03-06
Status: Aprovado para planejamento e especificação técnica

## Objetivo

Implementar um sistema completo de autenticação de usuários com controle de acesso (ACLs) e autenticação multifator (MFA) via FIDO2 (WebAuthn) e OTP (TOTP), utilizando exclusivamente bibliotecas abertas (MIT/BSD) para evitar problemas de licenciamento.

---

## 1. ANÁLISE DO ESTADO ATUAL

### 1.1 Estrutura Identificada
- ✅ Stack robusto: ASP.NET Core + PostgreSQL + NATS + Redis + SignalR
- ✅ Arquitetura em camadas: Core → Infrastructure → Api → Migrations
- ✅ Pattern de repositórios: Interfaces em Core, implementação em Infrastructure (Dapper)
- ✅ Auditoria já existe: ConfigurationAuditService
- ❌ Sem camada de usuários: Apenas Agent/Client/Site (entidades de infraestrutura)
- ❌ Sem autenticação de usuários: AgentToken é apenas para agents
- ❌ Sem ACLs: Não há controle de acesso baseado em usuário

### 1.2 Entidades Existentes Identificadas
- Agent, Client, Site (hierarquia de recursos de infraestrutura)
- AgentToken, DeployToken (apenas para agents/deploys)
- Tickets, Logs, SoftwareInventory, ConfigurationAudit
- Controllers bem estruturados: AgentsController, ClientsController, SitesController, etc.

### 1.3 Gaps Principais
1. ❌ Autenticação de usuários (user login/senha)
2. ❌ Controle de acesso role-based (RBAC) / attribute-based (ABAC)
3. ❌ Suporte a MFA (FIDO2, OTP, SMS, etc.)
4. ❌ Session management
5. ❌ Auditoria de login/logout de usuários

---

## 2. ARQUITETURA PROPOSTA

```
┌─────────────────────────────────────────────┐
│           Presentation (Web/API)              │
│  Controllers, DTOs, Validators              │
└──────────────┬──────────────────────────────┘
               │
┌──────────────┴──────────────────────────────┐
│      Authentication & Authorization          │
│  JWT Bearer + Custom Schemes                │
└──────────────┬──────────────────────────────┘
               │
┌──────────────┴──────────────────────────────┐
│         Identity & Access Layer              │
│  User, Role, Permission, MFA Entities      │
│  + FIDO2 Credentials, OTP Secrets          │
└──────────────┬──────────────────────────────┘
               │
┌──────────────┴──────────────────────────────┐
│     Discovery.Core (Entities + Interfaces)     │
│  - User, UserRole, Permission, ...          │
│  - IUserRepository, IRoleRepository, ...    │
│  - IFido2Service, IOtpService, ...          │
└──────────────┬──────────────────────────────┘
               │
┌──────────────┴──────────────────────────────┐
│  Discovery.Infrastructure (Data + Services)    │
│  - Repos (Dapper pattern)                   │
│  - TokenService (JWT generation)            │
│  - Fido2Service, OtpService                 │
│  - SessionService, AuditService             │
└──────────────┬──────────────────────────────┘
               │
┌──────────────┴──────────────────────────────┐
│   PostgreSQL + Redis Cache + Audit Log      │
└─────────────────────────────────────────────┘
```

---

## 3. ENTIDADES CORE A IMPLEMENTAR

### 3.1 Hierarquia de Usuários e Permissões

#### User
```
Id (Guid)
Email (unique)
FullName
PasswordHash
PasswordSalt
IsActive
CreatedAt
UpdatedAt
LastLoginAt
```

#### UserRole
```
Id (Guid)
UserId
RoleId
```

#### Role
```
Id (Guid)
Name (Admin, Manager, Operator, Viewer, Custom)
Description
IsActive
CreatedAt
```

#### Permission
```
Id (Guid)
Resource (Agents, Clients, Sites, Logs, Tickets, etc.)
Action (View, Create, Edit, Delete, Execute)
Description
IsActive
```

#### RolePermission
```
RoleId
PermissionId
```

#### ResourceScope
```
Id (Guid)
UserId
ResourceType (Client, Site, Agent, etc.)
ResourceId (Guid)
Level (Owner, Editor, Viewer)
CreatedAt
```

### 3.2 MFA & Credenciais

#### UserMfaKey
```
Id (Guid)
UserId
KeyType (Fido2, OTP, SMS, Email)
PublicKey (FIDO2)
Aaguid (FIDO2 authenticator info)
CredentialId (FIDO2)
OtpSecret (OTP - encriptado)
BackupCodes (encriptado JSON)
IsVerified
CreatedAt
LastUsedAt
Name (ex: "iPhone Face ID", "Authenticator Google")
```

#### UserSession
```
Id (Guid)
UserId
Token (hashed)
RefreshToken
IpAddress
UserAgent
CreatedAt
ExpiresAt
RevokedAt
```

#### UserAuditLog
```
Id (Guid)
UserId
Action (Login, Logout, MFARegister, PermissionChange, etc.)
ResourceType
ResourceId
IpAddress
UserAgent
ChangesBefore (JSON)
ChangesAfter (JSON)
CreatedAt
```

---

## 4. BIBLIOTECAS ABERTAS RECOMENDADAS

### 4.1 FIDO2 (WebAuthn)
```
Fido2.Models (Microsoft)
- Version: Latest
- License: MIT
- Description: FIDO2 server lib from Microsoft
- Notes: Completo, bem mantido, suporte WebAuthn 2.0
```

### 4.2 OTP (TOTP/HOTP)
```
OtpNet
- Version: Latest
- License: MIT
- Description: TOTP/HOTP implementation
- Notes: QR Code generation, time-based 6-digit codes
```

### 4.3 JWT & Security (Built-in)
```
System.IdentityModel.Tokens.Jwt
Microsoft.IdentityModel.Tokens
- Built-in em ASP.NET Core
- License: MIT
```

### 4.4 Hashing & Encryption (Built-in)
```
System.Security.Cryptography
- Built-in
- Argon2, PBKDF2, AES-256
```

### 4.5 QR Code
```
QRCoder
- Version: Latest
- License: MIT/BSD
- Notes: Geração de QR codes para setup TOTP
```

### 4.6 Email (para OTP via email/recovery)
```
FluentEmail
- Version: Latest
- License: MIT
- Notes: Email templating, suporte SMTP
```

---

## 5. FLUXOS DE AUTENTICAÇÃO

### 5.1 Cadastro de Usuário + Senha
```
User → POST /api/auth/register
  ├─ Email, FullName, Password
  ├─ Validate (email unique, pwd strength)
  ├─ Hash password (Argon2id)
  ├─ Create User entity
  ├─ Trigger: Send verification email
  └─ Response: 201 + message "Verify email"
```

### 5.2 Login com Usuário/Senha
```
User → POST /api/auth/login
  ├─ Email, Password
  ├─ Verify hash
  ├─ Check if MFA enabled
  │  ├─ YES → Return 202 + MFA_REQUIRED
  │  │        (temporary token 5min)
  │  └─ NO → Go to 5.5
  └─ Log: UserAuditLog (Login Attempt)
```

### 5.3 Registro de Chave FIDO2
```
User (authenticated) → POST /api/mfa/fido2/register/begin
  ├─ Backend gera credentialOptions via Fido2.Models
  ├─ Armazena challenge em Redis (TTL 10min)
  ├─ Response: CredentialCreationOptions (JSON)
  │
  └─ Client: WebAuthn.create(options)
       │
       └─ User → POST /api/mfa/fido2/register/verify
           ├─ Recebe AuthenticatorAttestationResponse
           ├─ Valida signature + challenge
           ├─ Salva credentialId + publicKey em UserMfaKey
           ├─ Marca como IsVerified=true
           └─ Response: 201 + "FIDO2 key registered"
```

### 5.4 Registro de OTP (TOTP)
```
User (authenticated) → GET /api/mfa/otp/setup
  ├─ Gera secret random (OtpNet.KeyGeneration)
  ├─ Encripta secret (AES-256) + armazena em Redis temp
  ├─ Gera QR code (QRCoder) com otpauth:// URI
  ├─ Response: { qrCode, secret (masked), manualKey }
  │
  └─ User escaneia QR code no authenticator (Google Authenticator, etc.)
       │
       └─ User → POST /api/mfa/otp/verify
           ├─ Recebe código 6-dígito
           ├─ Valida contra OtpNet.Totp
           ├─ Salva OTP secret em UserMfaKey.OtpSecret (encrypted)
           ├─ Gera 8 backup codes (JSON), encripta
           ├─ Armazena em UserMfaKey.BackupCodes
           └─ Response: 201 + { backupCodes }
```

### 5.5 Login com MFA (FIDO2)
```
User (com MFA_REQUIRED temp token) → POST /api/auth/mfa/fido2/assert/begin
  ├─ Backend gera assertionOptions
  ├─ Armazena challenge em Redis (TTL 5min)
  └─ Response: AssertionOptions
     │
     └─ Client: WebAuthn.get(options)
          │
          └─ User authenticates (biometric/PIN)
               │
               └─ POST /api/auth/mfa/fido2/assert/verify
                   ├─ Recebe AuthenticatorAssertionResponse
                   ├─ Valida against public key em DB
                   ├─ Gera JWT token
                   ├─ Cria UserSession
                   ├─ Log: UserAuditLog (Login Success)
                   └─ Response: 200 + { accessToken, refreshToken }
```

### 5.6 Login com MFA (OTP/TOTP)
```
User (com MFA_REQUIRED temp token) → POST /api/auth/mfa/otp/verify
  ├─ Recebe código 6-dígito OR backup code
  ├─ Valida TOTP.IsValid(token, secret, window=1)
  ├─ Se backup code: remove da lista
  ├─ Gera JWT + RefreshToken
  ├─ Cria UserSession
  ├─ Log: UserAuditLog (Login Success)
  └─ Response: 200 + { accessToken, refreshToken }
```

### 5.7 Refresh Token
```
Client → POST /api/auth/refresh
  ├─ Valida RefreshToken
  ├─ Gera novo JWT (curta vida, ex 15min)
  └─ Response: 200 + { accessToken }
```

### 5.8 Logout
```
User → POST /api/auth/logout
  ├─ Marca UserSession.RevokedAt = now
  ├─ Invalida RefreshToken em Redis
  ├─ Log: UserAuditLog (Logout)
  └─ Response: 204
```

---

## 6. MIDDLEWARE & SEGURANÇA

### 6.1 JwtAuthMiddleware
- Extract JWT from Authorization header
- Valida signature + expiration
- Extrai claims (UserId, email, roles)
- Popula HttpContext.User (ClaimsPrincipal)
- Se inválido → return 401 Unauthorized

### 6.2 AuthorizationFilter (Baseado em Roles/Permissions)
```csharp
[Authorize(Roles = "Admin, Manager")]
[Authorize(Policy = "CanViewLogs")]
```
- Valida roles do user
- Valida permissions específicas
- Checa resource scopes (user só pode ver logs de seu client)
- Se falha → return 403 Forbidden

### 6.3 Rate Limiting (Prevenção de Brute Force)
- Limita 5 tentativas de login falhadas por IP/email em 15min
- Limita 100 requisições por minuto por usuário autenticado
- Usa Redis para estado distribuído

---

## 7. PLANO DE IMPLEMENTAÇÃO - FASES

### FASE 1: Infraestrutura Base (2-3 semanas)
**Atividades:**
- [ ] **Core**: User, UserRole, Role, Permission entities + value objects
- [ ] **Core**: Enums (RoleType, ActionType, ResourceType, ScopeLevel)
- [ ] **Core**: Interfaces (IUserRepository, IRoleRepository, ITokenService, IAuthService)
- [ ] **Infrastructure**: UserRepository, RoleRepository (Dapper pattern)
- [ ] **Infrastructure**: PasswordHashingService (Argon2id)
- [ ] **Infrastructure**: JwtTokenService (Microsoft.IdentityModel)
- [ ] **Migrations**: M025_CreateUserAndRolesTables.cs
- [ ] **Tests**: Unit tests para hashing e JWT

**Entregáveis**: 
- NuGets adicionados (System.IdentityModel.Tokens.Jwt)
- Tabelas criadas em PostgreSQL
- Serviços básicos funcionais

---

### FASE 2: Autenticação Básica (1-2 semanas)
**Atividades:**
- [ ] **Controllers**: AuthController (register, login, logout, refresh)
- [ ] **Validators**: UserValidators, LoginRequestvalidators
- [ ] **Middleware**: JwtAuthMiddleware
- [ ] **Filters**: AuthorizationFilter (anotações [Authorize])
- [ ] **Enums**: LoginResult, TokenType
- [ ] **Services**: UserService, AuthenticationService
- [ ] **Migrations**: M026_CreateUserSessionTable.cs

**Entregáveis**:
- Usuários podem se registrar com email/senha
- Login retorna JWT
- JWT valida requests
- Logout revoga sessão

---

### FASE 3: FIDO2 (WebAuthn) (3-4 semanas)
**Atividades:**
- [ ] **Core**: UserMfaKey, Fido2Challenge entities
- [ ] **NuGet**: Fido2.Models (Microsoft)
- [ ] **Infrastructure**: Fido2Service (registration + authentication)
- [ ] **Controllers**: MfaController.Fido2Register*, MfaController.Fido2Assert*
- [ ] **Validators**: Fido2RequestValidators
- [ ] **Migrations**: M027_CreateUserMfaKeyTable.cs
- [ ] **Frontend**: WebAuthn.create/get() calls

**Entregáveis**:
- Usuários registram chaves FIDO2 (security keys, biometrics)
- Autenticação com FIDO2
- Suporte a múltiplas chaves por usuário

---

### FASE 4: OTP/TOTP (2-3 semanas)
**Atividades:**
- [ ] **NuGet**: OtpNet, QRCoder
- [ ] **Infrastructure**: OtpService (TOTP generation + verification)
- [ ] **Infrastructure**: BackupCodeService (generation + validation)
- [ ] **Controllers**: MfaController.OtpSetup, MfaController.OtpVerify
- [ ] **Migrations**: Atualizar M027 para incluir OTP fields
- [ ] **Frontend**: QR code scanning, 6-digit input

**Entregáveis**:
- Usuários registram TOTP (Google Authenticator, etc.)
- QR code para setup
- Backup codes para recuperação
- Autenticação com OTP

---

### FASE 5: ACLs & Resource-Based Scopes (3-4 semanas)
**Atividades:**
- [ ] **Core**: ResourceScope, RolePermission entities
- [ ] **Core**: Interfaces (IPermissionService, IScopeService)
- [ ] **Infrastructure**: PermissionService, ScopeService
- [ ] **Controllers**: PermissionsController, ScopesController (admin)
- [ ] **Filters**: ResourceAuthorizationFilter (checa scope)
- [ ] **Migrations**: M028_CreateAclAndScopeTables.cs
- [ ] **Seeds**: DefaultRoles + DefaultPermissions

**Entregáveis**:
- 5 roles padrão (Admin, Manager, Operator, Viewer, Custom)
- Usuários atribuem permissões por recurso (Client, Site, Agent)
- Queries automaticamente filtram por scope

---

### FASE 6: Auditoria & Compliance (2 semanas)
**Atividades:**
- [ ] **Core**: UserAuditLog entity (expandir se necessário)
- [ ] **Infrastructure**: AuditService (log actions)
- [ ] **Controllers**: AuditController (read-only para admins)
- [ ] **Middleware**: AuditLoggingMiddleware (auto-log de actions)
- [ ] **Migrations**: M029_CreateAuditLogTable.cs

**Entregáveis**:
- Log de login/logout por usuário
- Log de mudanças de permissões
- Log de acesso a recursos sensíveis
- Queries para compliance/forense

---

### FASE 7: UI & Integração (4-5 semanas - paralelo)
**Atividades:**
- [ ] **Frontend**: Login/Register forms
- [ ] **Frontend**: MFA setup (FIDO2 + OTP)
- [ ] **Frontend**: User profile (manage keys, backup codes)
- [ ] **Frontend**: Admin panel (users, roles, scopes)
- [ ] **Frontend**: Logout button
- [ ] **Integration**: Atualizar todos controllers com [Authorize]
- [ ] **Integration**: Filtrar dados por ResourceScope automaticamente

**Observações**:
- Pode rodar em paralelo com fases anteriores
- Usar framework UI já adotado no projeto

---

## 8. ESTRUTURA DE PASTAS (Discovery.Core)

```
src/Discovery.Core/
├── Entities/
│   ├── Identity/
│   │   ├── User.cs
│   │   ├── UserRole.cs
│   │   ├── Role.cs
│   │   ├── Permission.cs
│   │   ├── RolePermission.cs
│   │   └── ResourceScope.cs
│   ├── Security/
│   │   ├── UserMfaKey.cs
│   │   ├── UserSession.cs
│   │   └── UserAuditLog.cs
│   └── *.cs (Agent, Client, etc. - já existentes)
├── Enums/
│   ├── Identity/
│   │   ├── RoleType.cs
│   │   ├── ActionType.cs
│   │   ├── ResourceType.cs
│   │   └── ScopeLevel.cs
│   ├── Security/
│   │   ├── MfaKeyType.cs
│   │   ├── AuditAction.cs
│   │   └── LoginResult.cs
│   └── *.cs (já existentes)
├── Interfaces/
│   ├── IUserRepository.cs
│   ├── IRoleRepository.cs
│   ├── IPermissionRepository.cs
│   ├── IUserSessionRepository.cs
│   ├── IUserMfaKeyRepository.cs
│   ├── ITokenService.cs
│   ├── IAuthService.cs
│   ├── IFido2Service.cs
│   ├── IOtpService.cs
│   ├── IPermissionService.cs
│   ├── IScopeService.cs
│   ├── IAuditService.cs
│   └── *.cs (já existentes)
└── ...
```

---

## 9. ENDPOINTS PRINCIPAIS (Discovery.Api)

### Authentication
```
POST /api/auth/register
POST /api/auth/login
POST /api/auth/logout
POST /api/auth/refresh
```

### MFA - FIDO2
```
POST /api/mfa/fido2/register/begin
POST /api/mfa/fido2/register/verify
POST /api/mfa/fido2/assert/begin
POST /api/mfa/fido2/assert/verify
```

### MFA - OTP
```
GET /api/mfa/otp/setup
POST /api/mfa/otp/verify
POST /api/mfa/otp/disable
```

### User Management (Admin)
```
GET /api/users
POST /api/users
GET /api/users/{id}
PUT /api/users/{id}
DELETE /api/users/{id}
```

### Roles & Permissions (Admin)
```
GET /api/roles
POST /api/roles
GET /api/permissions
POST /api/permissions
```

### Resource Scopes (Admin)
```
GET /api/scopes?userId={id}
POST /api/scopes
DELETE /api/scopes/{id}
```

### Audit Logs (Admin)
```
GET /api/audit-logs
GET /api/audit-logs?user={id}&action={action}
```

---

## 10. CONSIDERAÇÕES DE SEGURANÇA

| Aspecto | Recomendação |
|--------|-------------|
| **Senha** | Argon2id (time=2, memory=19M) |
| **JWT** | RS256 (asymmetric), TTL 15min, refresh token 7d |
| **FIDO2** | Attestation validation, counter checks |
| **OTP** | TOTP (30s window, ±1 step), 6-digit codes |
| **Backup Codes** | Encriptados AES-256, one-time use |
| **Rate Limiting** | 5 logins falhados/15min, 100 req/min |
| **HTTPS** | Obrigatório em produção |
| **CORS** | Restrito a domínios conhecidos |
| **Session Timeout** | 30min idle, 24h máximo |
| **Audit Log Retention** | 2 anos mínimo |
| **Secret Management** | Usar appsettings.json com variables env |

---

## 11. DEPENDÊNCIAS NUGET A ADICIONAR

```xml
<!-- FASE 1 -->
<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="7.x.x" />
<PackageReference Include="Microsoft.IdentityModel.Tokens" Version="7.x.x" />

<!-- FASE 3 -->
<PackageReference Include="Fido2.Models" Version="latest" />

<!-- FASE 4 -->
<PackageReference Include="OtpNet" Version="latest" />
<PackageReference Include="QRCoder" Version="latest" />

<!-- Recomendado -->
<PackageReference Include="FluentEmail.Smtp" Version="latest" />
```

---

## 12. PRÓXIMAS AÇÕES RECOMENDADAS

1. ✅ Revisar this analysis com stakeholders
2. ✅ Validar alinhamento com arquitetura existente
3. ✅ Priorizar fases (ex: talvez FIDO2 seja de baixa prioridade inicialmente)
4. ✅ Definir timeline e sprints
5. ✅ Criar tickets/issues no repositório para cada tarefa
6. ✅ Prototipar schema PostgreSQL em ambiente dev
7. ✅ Revisar compliance (GDPR, etc. se aplicável)
8. ✅ Comunicar com stakeholders sobre breaking changes

---

## 13. RESUMO EXECUTIVO

**O que está proposto:**
- ✅ Autenticação completa de usuários (email/senha)
- ✅ Autenticação forte via FIDO2 (WebAuthn) - open source Microsoft
- ✅ 2FA via TOTP (Google Authenticator) - OtpNet open source
- ✅ ACLs granulares com roles e permissões por recurso
- ✅ Auditoria completa de logins e acessos
- ✅ 100% bibliotecas abertas (MIT/BSD licensed)

**Esforço estimado:**
- **Total**: 15-20 semanas (7 fases)
- **FIDO2**: 3-4 semanas
- **OTP**: 2-3 semanas
- **ACLs**: 3-4 semanas

**Licenças**: Todas MIT/BSD compatíveis ✅

---

## Aprovações & Notas

| Papel | Data | Assinatura | Notas |
|------|------|-----------|-------|
| Product Owner | | | |
| Tech Lead | | | |
| Security | | | |
| Architecture | | | |

