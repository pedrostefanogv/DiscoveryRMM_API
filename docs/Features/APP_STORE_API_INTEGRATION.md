# App Store API - Documentação de Integração

## Base URL
```
POST/GET /api/app-store/...
```

---

## 1. Enumerações

### AppInstallationType (filtro por tipo)
```csharp
public enum AppInstallationType
{
    Winget = 0,
    Chocolatey = 1,
    Custom = 2
}
```

### AppApprovalScopeType (escopo de aprovação)
```csharp
public enum AppApprovalScopeType
{
    Global = 0,      // Global (todos os agentes)
    Client = 1,      // Client específico
    Site = 2,        // Site específico de um cliente
    Agent = 3        // Agente específico de um site
}
```

### AppApprovalActionType (ação de aprovação)
```csharp
public enum AppApprovalActionType
{
    Allow = 0,       // Autorizar instalação
    Deny = 1         // Negar instalação
}
```

### AppApprovalAuditChangeType (tipo de mudança de auditoria)
```csharp
public enum AppApprovalAuditChangeType
{
    Created = 0,     // Regra criada
    Updated = 1,     // Regra atualizada
    Deleted = 2      // Regra deletada
}
```

---

## 2. Endpoints - Catálogo

### 2.1 Buscar Catálogo com Paginação por Cursor

**Endpoint:** `GET /api/app-store/catalog`

**Parâmetros Query:**
- `installationType` (int, default: 0=Winget): Filtro por tipo de instalação
- `search` (string, opcional): Busca por nome/descrição do pacote
- `architecture` (string, opcional): Filtro por arquitetura (ex: x64, x86)
- `limit` (int, default: 50, max: 200): Quantidade de itens por página
- `cursor` (string, opcional): Cursor para próxima página (encode em Base64)

**Resposta (200 OK):**
```json
{
  "generatedAt": "2026-03-11T10:30:00Z",
  "totalPackagesInSource": 5000,
  "returnedItems": 50,
  "limit": 50,
  "hasMore": true,
  "search": "vs",
  "architecture": "x64",
  "cursor": "YXBwOjA=",
  "nextCursor": "YXBwOjUw",
  "items": [
    {
      "id": "microsoft-visualstudio",
      "name": "Visual Studio 2022",
      "publisher": "Microsoft Corporation",
      "version": "17.8.1",
      "description": "The most comprehensive IDE for .NET and C++ developers",
      "homepage": "https://visualstudio.microsoft.com",
      "license": "Proprietary",
      "category": "Development",
      "icon": "https://...",
      "installCommand": "winget install Microsoft.VisualStudio.2022.Enterprise",
      "lastUpdated": "2026-03-10T08:00:00Z",
      "tags": ["ide", "development", "microsoft"],
      "installerUrlsByArch": {
        "x64": "https://installer-x64.exe",
        "x86": "https://installer-x86.exe"
      }
    }
  ]
}
```

**Exemplo CURL:**
```bash
# Primeira página
curl -X GET "http://localhost:5001/api/app-store/catalog?installationType=0&search=vs&limit=50"

# Próxima página com cursor
curl -X GET "http://localhost:5001/api/app-store/catalog?installationType=0&search=vs&limit=50&cursor=YXBwOjUw"

# Buscar Chocolatey
curl -X GET "http://localhost:5001/api/app-store/catalog?installationType=1&search=notepad"

# Buscar Custom apps
curl -X GET "http://localhost:5001/api/app-store/catalog?installationType=2"
```

---

### 2.2 Obter Pacote Específico

**Endpoint:** `GET /api/app-store/catalog/{packageId}`

**Parâmetros:**
- `packageId` (string, path): ID único do pacote (ex: Microsoft.VisualStudio.2022.Enterprise)
- `installationType` (int, default: 0=Winget, query): Tipo de instalação

**Resposta (200 OK):**
```json
{
  "id": "microsoft-visualstudio",
  "name": "Visual Studio 2022",
  "publisher": "Microsoft Corporation",
  "version": "17.8.1",
  "description": "The most comprehensive IDE for .NET and C++ developers",
  "homepage": "https://visualstudio.microsoft.com",
  "license": "Proprietary",
  "category": "Development",
  "icon": "https://...",
  "installCommand": "winget install Microsoft.VisualStudio.2022.Enterprise",
  "lastUpdated": "2026-03-10T08:00:00Z",
  "tags": ["ide", "development", "microsoft"],
  "installerUrlsByArch": {
    "x64": "https://installer-x64.exe"
  }
}
```

**Resposta (404 Not Found):**
```json
{
  "error": "Package not found."
}
```

**Exemplo CURL:**
```bash
curl -X GET "http://localhost:5001/api/app-store/catalog/Microsoft.VisualStudio.2022.Enterprise?installationType=0"
```

---

### 2.3 Criar/Atualizar App Customizado

**Endpoint:** `POST /api/app-store/catalog/custom`

**Corpo (JSON):**
```json
{
  "packageId": "custom-app-001",
  "name": "Custom Enterprise App",
  "publisher": "Internal IT",
  "version": "1.0.0",
  "description": "Internal company application",
  "iconUrl": "https://s3.bucket.com/icon.png",
  "siteUrl": "https://internal.company.com",
  "installCommand": "msiexec /i app.msi",
  "metadataJson": "{\"department\": \"Finance\", \"internal\": true}",
  "fileObjectKey": "s3://bucket/custom-app.msi",
  "fileBucket": "discovery-apps",
  "filePublicUrl": "https://s3.bucket.com/custom-app.msi",
  "fileContentType": "application/x-msi",
  "fileSizeBytes": 104857600,
  "fileChecksum": "sha256:abc123..."
}
```

**Resposta (200 OK):**
```json
{
  "id": "custom-app-001",
  "name": "Custom Enterprise App",
  "publisher": "Internal IT",
  "version": "1.0.0",
  "description": "Internal company application",
  "homepage": "https://internal.company.com",
  "license": "",
  "category": "Custom",
  "icon": "https://s3.bucket.com/icon.png",
  "installCommand": "msiexec /i app.msi",
  "lastUpdated": "2026-03-11T10:30:00Z",
  "tags": [],
  "installerUrlsByArch": {}
}
```

**Exemplo CURL:**
```bash
curl -X POST "http://localhost:5001/api/app-store/catalog/custom" \
  -H "Content-Type: application/json" \
  -d '{
    "packageId": "custom-app-001",
    "name": "Custom App",
    "publisher": "IT",
    "version": "1.0.0"
  }'
```

---

## 3. Endpoints - Síncronização

### 3.1 Sincronizar Catálogo (Winget/Chocolatey)

**Endpoint:** `POST /api/app-store/sync`

**Parâmetros Query:**
- `installationType` (int, required): Tipo de instalação (0=Winget, 1=Chocolatey)

**Resposta (200 OK):**
```json
{
  "installationType": 0,
  "success": true,
  "packagesUpserted": 1523,
  "pagesProcessed": 15,
  "syncedAt": "2026-03-11T10:45:22Z",
  "sourceGeneratedAt": "2026-03-10T08:00:00Z",
  "duration": "00:05:30.123000",
  "error": null
}
```

**Exemplo CURL:**
```bash
# Sincronizar Winget
curl -X POST "http://localhost:5001/api/app-store/sync?installationType=0"

# Sincronizar Chocolatey
curl -X POST "http://localhost:5001/api/app-store/sync?installationType=1"
```

---

## 4. Endpoints - Aprovações e Políticas

### 4.1 Listar Regras de Aprovação

**Endpoint:** `GET /api/app-store/approvals`

**Parâmetros Query:**
- `scopeType` (int, required): Escopo (0=Global, 1=Client, 2=Site, 3=Agent)
- `scopeId` (guid, opcional): ID do escopo (obrigatório para Client/Site/Agent)
- `installationType` (int, default: 0=Winget): Tipo de instalação

**Resposta (200 OK):**
```json
{
  "scopeType": 0,
  "scopeId": null,
  "installationType": 0,
  "count": 3,
  "items": [
    {
      "ruleId": "01abc123-abc1-abc1-abc1-abc1abc1abc1",
      "scopeType": 0,
      "scopeId": null,
      "installationType": 0,
      "packageId": "Microsoft.VisualStudio.2022.Enterprise",
      "action": 0,
      "autoUpdateEnabled": true,
      "updatedAt": "2026-03-10T14:30:00Z"
    }
  ]
}
```

**Exemplo CURL:**
```bash
# Global rules (Winget)
curl -X GET "http://localhost:5001/api/app-store/approvals?scopeType=0&installationType=0"

# Rules for specific client
curl -X GET "http://localhost:5001/api/app-store/approvals?scopeType=1&scopeId=550e8400-e29b-41d4-a716-446655440000&installationType=0"
```

---

### 4.2 Criar/Atualizar Regra de Aprovação

**Endpoint:** `POST /api/app-store/approvals`

**Corpo (JSON):**
```json
{
  "scopeType": 0,
  "scopeId": null,
  "installationType": 0,
  "packageId": "Microsoft.VisualStudio.2022.Enterprise",
  "action": 0,
  "autoUpdateEnabled": true,
  "reason": "Required for development team"
}
```

**Resposta (200 OK):**
```json
{
  "ruleId": "01abc123-abc1-abc1-abc1-abc1abc1abc1",
  "scopeType": 0,
  "scopeId": null,
  "installationType": 0,
  "packageId": "Microsoft.VisualStudio.2022.Enterprise",
  "action": 0,
  "autoUpdateEnabled": true,
  "updatedAt": "2026-03-11T10:30:00Z"
}
```

**Exemplo CURL:**
```bash
curl -X POST "http://localhost:5001/api/app-store/approvals" \
  -H "Content-Type: application/json" \
  -d '{
    "scopeType": 0,
    "scopeId": null,
    "installationType": 0,
    "packageId": "Microsoft.VisualStudio.2022.Enterprise",
    "action": 0,
    "autoUpdateEnabled": true,
    "reason": "Development team requirement"
  }'
```

---

### 4.3 Deletar Regra de Aprovação

**Endpoint:** `DELETE /api/app-store/approvals/{ruleId}`

**Parâmetros:**
- `ruleId` (guid, path): ID da regra a deletar
- `reason` (string, opcional, query): Motivo da deleção

**Resposta:** `204 No Content`

**Exemplo CURL:**
```bash
curl -X DELETE "http://localhost:5001/api/app-store/approvals/01abc123-abc1-abc1-abc1-abc1abc1abc1?reason=Policy%20change"
```

---

### 4.4 Histórico de Auditoria de Aprovações

**Endpoint:** `GET /api/app-store/approvals/audit`

**Parâmetros Query:**
- `installationType` (int, default: 0=Winget)
- `packageId` (string, opcional)
- `scopeType` (int, opcional)
- `scopeId` (guid, opcional)
- `changedBy` (string, opcional): Usuário que alterou
- `changedFrom` (datetime, opcional): Data/hora inicial
- `changedTo` (datetime, opcional): Data/hora final
- `changeType` (int, opcional): 0=Criada, 1=Atualizada, 2=Deletada
- `limit` (int, default: 100, max: 200)
- `cursor` (guid, opcional): Cursor para pagination

**Resposta (200 OK):**
```json
{
  "installationType": 0,
  "packageId": "Microsoft.VisualStudio.2022.Enterprise",
  "scopeType": 0,
  "scopeId": null,
  "cursor": null,
  "nextCursor": null,
  "limit": 100,
  "returnedItems": 2,
  "hasMore": false,
  "items": [
    {
      "auditId": "01abc123-abc1-abc1-abc1-abc1abc1abc1",
      "installationType": 0,
      "packageId": "Microsoft.VisualStudio.2022.Enterprise",
      "scopeType": 0,
      "scopeId": null,
      "action": 0,
      "autoUpdateEnabled": true,
      "changeType": 0,
      "changedBy": "admin@company.com",
      "changedAt": "2026-03-10T14:30:00Z",
      "reason": "Required for development team"
    }
  ]
}
```

**Exemplo CURL:**
```bash
# Auditoria global
curl -X GET "http://localhost:5001/api/app-store/approvals/audit?installationType=0"

# Auditoria de um pacote específico
curl -X GET "http://localhost:5001/api/app-store/approvals/audit?installationType=0&packageId=Microsoft.VisualStudio.2022.Enterprise"

# Auditoria em período
curl -X GET "http://localhost:5001/api/app-store/approvals/audit?installationType=0&changedFrom=2026-03-01T00:00:00Z&changedTo=2026-03-11T23:59:59Z"
```

---

## 5. Endpoints - Apps Efetivos (Aprovados)

### 5.1 Listar Apps Efetivos por Escopo (com Paginação)

**Endpoint:** `GET /api/app-store/effective`

**Parâmetros Query:**
- `scopeType` (int, required): Escopo (0=Global, 1=Client, 2=Site, 3=Agent)
- `scopeId` (guid, opcional): ID do escopo (obrigatório para Client/Site/Agent)
- `installationType` (int, default: 0=Winget)
- `search` (string, opcional): Busca por nome/packageId
- `limit` (int, default: 50, max: 200)
- `cursor` (string, opcional): Cursor para próxima página (encode em Base64)

**Resposta (200 OK):**
```json
{
  "scopeType": 0,
  "scopeId": null,
  "installationType": 0,
  "search": null,
  "cursor": null,
  "nextCursor": "YXBwOjUw",
  "limit": 50,
  "returnedItems": 50,
  "hasMore": true,
  "items": [
    {
      "installationType": 0,
      "packageId": "Microsoft.VisualStudio.2022.Enterprise",
      "name": "Visual Studio 2022",
      "description": "The most comprehensive IDE",
      "iconUrl": "https://...",
      "publisher": "Microsoft Corporation",
      "version": "17.8.1",
      "installCommand": "winget install Microsoft.VisualStudio.2022.Enterprise",
      "installerUrlsByArch": {
        "x64": "https://installer-x64.exe"
      },
      "autoUpdateEnabled": true,
      "sourceScope": 0
    }
  ]
}
```

**Exemplo CURL:**
```bash
# Apps efetivos globais (Winget)
curl -X GET "http://localhost:5001/api/app-store/effective?scopeType=0&installationType=0"

# Apps efetivos para um cliente
curl -X GET "http://localhost:5001/api/app-store/effective?scopeType=1&scopeId=550e8400-e29b-41d4-a716-446655440000"

# Com busca e paginação
curl -X GET "http://localhost:5001/api/app-store/effective?scopeType=0&installationType=0&search=visual&limit=20&cursor=YXBwOjA="
```

---

### 5.2 Comparação de Apps (Diff - Instalados vs Aprovados)

**Endpoint:** `GET /api/app-store/diff/effective`

**Parâmetros Query:**
- `scopeType` (int, required)
- `scopeId` (guid, opcional)
- `installationType` (int, default: 0=Winget)
- `search` (string, opcional)
- `limit` (int, default: 50, max: 200)
- `cursor` (string, opcional)

**Resposta:** Similar a `/effective`, mas inclui diffs e informações de compliance.

---

### 5.3 Diferença de Pacote Específico

**Endpoint:** `GET /api/app-store/diff/{packageId}`

**Parâmetros:**
- `packageId` (string, path)
- `scopeType` (int, required, query)
- `scopeId` (guid, opcional, query)
- `installationType` (int, default: 0=Winget, query)

**Resposta (200 OK):**
```json
{
  "packageId": "Microsoft.VisualStudio.2022.Enterprise",
  "catalogPackage": {
    "id": "Microsoft.VisualStudio.2022.Enterprise",
    "name": "Visual Studio 2022",
    "version": "17.8.1"
  },
  "approvedRules": [
    {
      "ruleId": "01abc123-abc1-abc1-abc1-abc1abc1abc1",
      "scopeType": 0,
      "action": 0,
      "autoUpdateEnabled": true
    }
  ]
}
```

---

## 6. Paginação por Cursor

A API usa **cursor-based pagination** para todas as listagens. O cursor é codificado em Base64.

### Como funciona:

1. **Primeira requisição:** Omita o parâmetro `cursor`
   ```bash
   curl -X GET "http://localhost:5001/api/app-store/catalog?limit=50"
   ```

2. **Resposta contém:**
   - `hasMore`: `true/false` indicando se há mais páginas
   - `nextCursor`: Cursor para próxima página (se `hasMore=true`)
   - `returnedItems`: Quantidade de itens retornados

3. **Próxima página:** Use o `nextCursor` na próxima requisição
   ```bash
   curl -X GET "http://localhost:5001/api/app-store/catalog?limit=50&cursor=YXBwOjUw"
   ```

4. **Parar de paginar:** Quando `hasMore=false`, atingiu-se o fim

### Vantagens:

- ✅ Não depende de offset (mais eficiente em grandes datasets)
- ✅ Seguro contra inserções/deleções durante a paginação
- ✅ Suporta ordenação estável (packageId)

### Estrutura do Cursor (informativo):

```
Cursor encode: YXBwOjUw
Cursor decode: app:50
Significa: começar após posição 50
```

---

## 7. Tratamento de Erros

### Todos os Endpoints retornam:

**Erro Geral (5xx):**
```json
{
  "error": "Internal server error"
}
```

**Erro de Validação (4xx):**
```json
{
  "error": "changedFrom must be less than or equal to changedTo."
}
```

**Recurso não encontrado (404):**
```json
{
  "error": "Package not found."
}
```

---

## 8. Fluxo de Integração Recomendado para Site de Gerenciamento

### Dashboard:
1. **GET** `/api/app-store/catalog?installationType=0&limit=10` → Últimos apps Winget
2. **GET** `/api/app-store/approvals?scopeType=0` → Regras globais ativas

### Busca/Filtro:
1. **GET** `/api/app-store/catalog?installationType={type}&search={term}&limit=50`
2. Usar `nextCursor` para paginação

### Gerenciamento de Aprovações:
1. **GET** `/api/app-store/approvals?scopeType={scope}&scopeId={id}`
2. **POST** `/api/app-store/approvals` para criar regra
3. **DELETE** `/api/app-store/approvals/{ruleId}` para remover
4. **GET** `/api/app-store/approvals/audit` para auditoria

### Sincronização:
1. **POST** `/api/app-store/sync?installationType=0` → Winget
2. **POST** `/api/app-store/sync?installationType=1` → Chocolatey
3. Verificar resposta (PackagesUpserted, Duration)

### Apps Efetivos (políticas ativas):
1. **GET** `/api/app-store/effective?scopeType={scope}&scopeId={id}` → Lista de apps aprovados

---

## 9. Rate Limiting e Best Practices

- **Max `limit`:** 200 itens por página
- **Cursor válido:** Sempre use o `nextCursor` retornado
- **Timeout:** Aguarde resposta antes de próxima requisição
- **Busca:** Normalize e trim a query do usuário (API faz internamente)

---

## 10. Lógica de Aprovações e Controle de Acesso

### 10.1 Catálogo vs Efetivos (Aprovados)

**NÃO existe "liberar tudo automaticamente"** — o acesso ao catálogo é controlado por **regras de aprovação explícitas**.

| Endpoint | Retorna | Acesso Controlado |
|----------|---------|---|
| `GET /api/app-store/catalog` | **TODOS os apps** do catálogo (Winget, Chocolatey, Custom) | ❌ NÃO (público) |
| `GET /api/app-store/effective` | **APENAS apps com regra "Allow"** | ✅ SIM (por política) |

**Exemplo:**
```
Catálogo: 1000 apps disponíveis
Regras:   Apenas 50 apps com "Allow"

Agent acessa /effective → Vê apenas 50 apps
Agent acessa /catalog → Vê todos 1000 apps (sem filtro)
```

---

### 10.2 Hierarquia de Escopos (Resolução de Regras)

A ordem de precedência ao resolver qual regra se aplica é:

```
Agent (mais específico)
    ↓
Site
    ↓
Client
    ↓
Global (menos específico)
```

**Como funciona:**
1. Sistema procura regra no escopo **Agent** para o package
2. Se não existir, procura em **Site**
3. Se não existir, procura em **Client**
4. Se não existir, procura em **Global**
5. **Primeira regra encontrada na hierarquia = decisão final**

**Exemplos de Resolução:**

*Exemplo 1: Sobrescrita por Client*
```
Pacote: Microsoft.VisualStudio.2022.Enterprise
InstallationType: Winget (0)

Global:  Allow ✅
Client:  Deny ❌  ← Sobrescreve Global
Site:    (não há regra)
Agent:   (não há regra)

Resultado Final: ❌ Deny (Client venceu)
```

*Exemplo 2: Herança de Global*
```
Pacote: Notepad++

Global:  Allow ✅
Client:  (não há regra)
Site:    (não há regra)
Agent:   (não há regra)

Resultado Final: ✅ Allow (Global se aplica)
```

*Exemplo 3: Sem Regra (Padrão Deny)*
```
Pacote: CustomApp

Global:  (não há regra)
Client:  (não há regra)
Site:    (não há regra)
Agent:   (não há regra)

Resultado Final: ❌ Deny (padrão seguro)
```

---

### 10.3 Auto-Update e Flags

Além da ação (Allow/Deny), cada regra pode ter:
- **`autoUpdateEnabled`**: true/false/null
  - Se a regra define `autoUpdateEnabled = true`, atualizações automáticas são habilitadas
  - Segue a mesma hierarquia (Agent sobrescreve Client, etc)

---

### 10.4 Cenários Práticos para o Site de Gerenciamento

#### Cenário 1: Empresa libera apenas 50 apps globalmente

```json
POST /api/app-store/approvals
{
  "scopeType": 0,  // Global
  "scopeId": null,
  "installationType": 0,
  "packageId": "Microsoft.VisualStudio.2022.Enterprise",
  "action": 0,  // Allow
  "autoUpdateEnabled": true,
  "reason": "Required for development"
}
// Repetir para 50 apps...

GET /api/app-store/effective?scopeType=0
// Retorna: 50 apps aprovados
```

**Uma configuração "liberar tudo":**
- ❌ Não existe automatic wildcard
- ✅ Solução: Criar 1000+ regras (inviável!)
- ✅ Alternativa: Usar `/catalog` sem filtro de aprovação (ignora políticas)

---

#### Cenário 2: Excetuar um app para um cliente específico

```json
// Global: permite Visual Studio
POST /api/app-store/approvals
{
  "scopeType": 0,
  "packageId": "Microsoft.VisualStudio.2022.Enterprise",
  "action": 0,  // Allow
  "reason": "Global policy"
}

// Mas para um cliente específico, negar acesso:
POST /api/app-store/approvals
{
  "scopeType": 1,  // Client
  "scopeId": "550e8400-e29b-41d4-a716-446655440000",
  "packageId": "Microsoft.VisualStudio.2022.Enterprise",
  "action": 1,  // Deny ← Sobrescreve Global
  "reason": "Client license expired"
}

// Resultado:
// - Outros clients: podem usar Visual Studio (Global Allow)
// - Este client específico: NÃO pode usar (Client Deny)
```

---

#### Cenário 3: Policy por Site dentro de um Client

```
Cliente ABC tem 3 sites:
  - Site 1: precisa de Visual Studio
  - Site 2: precisa de Visual Studio + Docker
  - Site 3: apenas ferramentas básicas

Configurar:
1. Global: Allow 5 apps básicos
2. Client ABC: Allow Visual Studio (vale para todos os sites por padrão)
3. Site 2: permitir Docker adicional (Add regra só para Site 2)
```

---

#### Cenário 4: Excetuar um agente específico

```
Todos têm acesso exceto Agente X:

POST /api/app-store/approvals (Global)
{
  "packageId": "Microsoft.VisualStudio",
  "action": 0  // Allow globally
}

POST /api/app-store/approvals (Agent)
{
  "scopeType": 3,  // Agent
  "scopeId": "{agentId}",
  "packageId": "Microsoft.VisualStudio",
  "action": 1  // Deny só para este agente
}
```

---

### 10.5 Fluxo de Decisão Resumido

```
┌─────────────────────────────────────────────────────────┐
│ Agent chama: GET /api/app-store/effective               │
└─────────────────────────────────────────────────────────┘
                            ↓
        ┌───────────────────────────────────────┐
        │ Carregar todas as regras para escopo: │
        │ - Agent (este agente)                 │
        │ - Site (site do agente)               │
        │ - Client (cliente do site)            │
        │ - Global                              │
        └───────────────────────────────────────┘
                            ↓
        ┌───────────────────────────────────────┐
        │ Para cada package do catálogo:        │
        │ 1. Procura regra em Agent             │
        │ 2. Se não, procura em Site            │
        │ 3. Se não, procura em Client          │
        │ 4. Se não, procura em Global          │
        │ 5. Se não, default = DENY             │
        └───────────────────────────────────────┘
                            ↓
        ┌───────────────────────────────────────┐
        │ Se action == Allow:                   │
        │ Incluir no resultado /effective       │
        │                                       │
        │ Se action == Deny:                    │
        │ Excluir do resultado /effective       │
        └───────────────────────────────────────┘
                            ↓
        ┌───────────────────────────────────────┐
        │ Retornar lista paginada de apps       │
        │ aprovados (apenas Allow)              │
        └───────────────────────────────────────┘
```

---

### 10.6 Como Configurar no Site

**Para "Liberar Tudo" (workaround):**

Opção 1: Usar `/catalog` em vez de `/effective` (ignora aprovações)
```javascript
// Mostra todos os apps (sem filtro de política)
fetch('http://api/app-store/catalog?installationType=0')
```

Opção 2: Criar regra Global Allow com wildcard (se implementado)
```json
POST /api/app-store/approvals
{
  "scopeType": 0,
  "packageId": "*",  // Wildcard (requer implementação)
  "action": 0,
  "reason": "Allow all packages"
}
```
⚠️ **Nota:** Wildcard NÃO está implementado. Seria uma feature opcional.

Opção 3: Criar regra para cada app (escalável com script)
```bash
# Script para criar 1000+ regras
for app in $(curl http://api/app-store/catalog); do
  POST /api/app-store/approvals { packageId: $app, action: 0 }
done
```

---

**Última atualização:** 11 de março de 2026
