# 📋 Guia de Criação e Edição de Relatórios Manualmente

## 🔌 Endpoints de Templates

### 1️⃣ **Listar Templates**
```http
GET /api/reports/templates
```

**Query Parameters:**
- `datasetType` (opcional): Filtra por tipo de dataset
- `isActive` (opcional, default: `true`): Filtra ativos/inativos

**Exemplo:**
```bash
GET /api/reports/templates?datasetType=Logs&isActive=true
```

**Resposta (200 OK):**
```json
[
  {
    "id": "a47f4f44-1b06-4a9c-b180-20b9b0074c8b",
    "name": "Agent Hardware Inventory",
    "description": "Current hardware specifications of all agents",
    "datasetType": 4,
    "defaultFormat": 0,
    "layoutJson": "{...}",
    "filtersJson": "{...}",
    "isActive": true,
    "version": 1,
    "createdAt": "2026-03-07T23:00:12Z",
    "updatedAt": "2026-03-07T23:00:12Z",
    "createdBy": "migration",
    "updatedBy": "migration"
  }
]
```

---

### 2️⃣ **Obter Template Específico**
```http
GET /api/reports/templates/{id}
```

**Exemplo:**
```bash
GET /api/reports/templates/a47f4f44-1b06-4a9c-b180-20b9b0074c8b
```

**Resposta (200 OK):** Mesmo formato do item anterior

---

### 3️⃣ **Criar Novo Template**
```http
POST /api/reports/templates
Content-Type: application/json
```

**Request Body:**
```json
{
  "name": "Meu Relatório de Software Customizado",
  "description": "Análise de software instalado por site",
  "datasetType": 0,
  "defaultFormat": 0,
  "layoutJson": {
    "title": "Inventário de Software",
    "columns": [
      {
        "field": "siteName",
        "width": 20,
        "header": "Site"
      },
      {
        "field": "softwareName",
        "width": 30,
        "header": "Software"
      },
      {
        "field": "version",
        "width": 15,
        "header": "Versão"
      }
    ],
    "pageSize": 100,
    "orientation": "landscape"
  },
  "filtersJson": {
    "limit": 5000,
    "orderBy": "softwareName",
    "orderDirection": "asc"
  },
  "createdBy": "usuario@empresa.com"
}
```

**Resposta (201 Created):**
```json
{
  "id": "019ccaab-2617-76fe-b783-34b42040782f",
  "name": "Meu Relatório de Software Customizado",
  "description": "Análise de software instalado por site",
  "datasetType": 0,
  "defaultFormat": 0,
  "layoutJson": "{...}",
  "filtersJson": "{...}",
  "isActive": true,
  "version": 1,
  "createdAt": "2026-03-08T10:30:00Z",
  "updatedAt": "2026-03-08T10:30:00Z",
  "createdBy": "usuario@empresa.com",
  "updatedBy": "usuario@empresa.com"
}
```

---

### 4️⃣ **Atualizar Template**
```http
PUT /api/reports/templates/{id}
Content-Type: application/json
```

**Request Body:** (Todos os campos são opcionais)
```json
{
  "name": "Nome Atualizado",
  "description": "Nova descrição",
  "defaultFormat": 1,
  "layoutJson": { ... },
  "filtersJson": { ... },
  "isActive": true,
  "updatedBy": "usuario@empresa.com"
}
```

**Resposta (200 OK):**
```json
{
  "id": "019ccaab-2617-76fe-b783-34b42040782f",
  "name": "Nome Atualizado",
  "version": 2,
  "updatedAt": "2026-03-08T11:15:00Z",
  ...
}
```

**Nota:** Cada atualização cria um novo snapshot no histórico

---

### 5️⃣ **Deletar Template**
```http
DELETE /api/reports/templates/{id}
```

**Resposta (204 No Content)**

Nota: É um soft delete - mantém o histórico

---

### 6️⃣ **Consultar Histórico de Versões**
```http
GET /api/reports/templates/{id}/history
```

**Query Parameters:**
- `limit` (opcional, default: 50): número máximo de versões

**Resposta (200 OK):**
```json
[
  {
    "id": "019ccaab-3456-7890-abcd-1234567890ab",
    "templateId": "019ccaab-2617-76fe-b783-34b42040782f",
    "version": 2,
    "eventType": "Updated",
    "name": "Nome Atualizado",
    "datasetType": 0,
    "defaultFormat": 1,
    "layoutJson": "{...}",
    "filtersJson": "{...}",
    "isActive": true,
    "createdAt": "2026-03-08T11:15:00Z",
    "createdBy": "usuario@empresa.com"
  },
  {
    "id": "019ccaab-2617-76fe-b783-34b42040782f",
    "templateId": "019ccaab-2617-76fe-b783-34b42040782f",
    "version": 1,
    "eventType": "Created",
    "createdAt": "2026-03-08T10:30:00Z",
    "createdBy": "usuario@empresa.com"
  }
]
```

---

## ✅ Validações Requeridas

### **Campo `name`**
- ✅ Obrigatório
- ✅ Mínimo: 2 caracteres
- ✅ Máximo: 200 caracteres
- ❌ Não pode ser vazio

**Exemplo válido:**
```json
{ "name": "Relatório de Logs" }
```

**Exemplo inválido:**
```json
{ "name": "A" }  // Muito curto
```

---

### **Campo `description`**
- ✅ Opcional
- ✅ Máximo: 2000 caracteres
- ✅ Pode ser nulo ou vazio

**Exemplo:**
```json
{ "description": "Análise detalhada de todas as atividades do sistema nos últimos 30 dias" }
```

---

### **Campo `datasetType`**
- ✅ Obrigatório
- ✅ Deve ser um enum válido

**Valores Permitidos:**
```
0 = SoftwareInventory
1 = Logs
2 = ConfigurationAudit
3 = Tickets
4 = AgentHardware
```

**Exemplo:**
```json
{ "datasetType": 1 }  // Logs
```

---

### **Campo `defaultFormat`**
- ✅ Obrigatório
- ✅ Deve estar nos formatos habilitados

**Valores Permitidos:**
```
0 = Xlsx (Excel)
1 = Csv
2 = Pdf
```

**Exemplo:**
```json
{ "defaultFormat": 0 }  // Excel
```

**Nota:** PDF precisa estar habilitado nas configurações da API

---

### **Campo `layoutJson`**
- ✅ Obrigatório
- ✅ Deve ser JSON válido
- ✅ Pode ser string JSON ou objeto

**Estrutura Mínima:**
```json
{
  "layoutJson": {
    "title": "Título do Relatório",
    "columns": [
      {
        "field": "fieldName",
        "header": "Cabeçalho",
        "width": 20
      }
    ],
    "pageSize": 100,
    "orientation": "landscape"
  }
}
```

**Campos do Layout:**
- `title` (string): Título do relatório
- `columns` (array): Lista de colunas a exibir
  - `field` (string): Nome do campo no dataset
  - `header` (string): Texto do cabeçalho
  - `width` (number): Largura em caracteres/percentual
  - `format` (string, opcional): `text`, `datetime`, `number`, `currency`
- `pageSize` (number, opcional): Linhas por página
- `orientation` (string, opcional): `landscape` ou `portrait`

---

### **Campo `filtersJson`**
- ✅ Opcional
- ✅ Deve ser JSON válido ou null
- ✅ Aplicado como filtro padrão à execução

**Exemplo:**
```json
{
  "filtersJson": {
    "limit": 5000,
    "orderBy": "timestamp",
    "orderDirection": "desc"
  }
}
```

---

### **Campo `createdBy` / `updatedBy`**
- ✅ Opcional
- ✅ Máximo: 256 caracteres
- ✅ Recomendado para auditoria (email do usuário)

**Exemplo:**
```json
{ "createdBy": "joao.silva@empresa.com" }
```

---

### **Campo `isActive`**
- ✅ Booleano (true/false)
- ✅ Default: true
- ✅ Permite desabilitar template sem deletar

**Exemplo:**
```json
{ "isActive": false }  // Template desativado
```

---

## 📊 Datasets Disponíveis

### **0 - SoftwareInventory**
Inventário de software instalado em agentes

**Campos Disponíveis:**
```
clientId, siteId, agentId, softwareName, publisher, version, 
installedAt, lastSeenAt, agentHostname, siteName
```

**OrderBy Permitidos:**
```
softwareName, publisher, version, lastSeenAt, agentHostname, siteName
```

---

### **1 - Logs**
Logs de sistema e eventos

**Campos Disponíveis:**
```
clientId, siteId, agentId, type, level, source, from, to, 
createdAt, message, timestamp
```

**OrderBy Permitidos:**
```
timestamp, level, source, type
```

---

### **2 - ConfigurationAudit**
Auditoria de alterações de configuração

**Campos Disponíveis:**
```
entityType, entityId, fieldName, oldValue, newValue, changedBy, 
changedAt, reason, timestamp
```

**OrderBy Permitidos:**
```
timestamp, entityType, changedBy, fieldName
```

---

### **3 - Tickets**
Tickets de suporte

**Campos Disponíveis:**
```
clientId, siteId, agentId, workflowStateId, priority, createdAt, 
closedAt, slaBreached, timestamp
```

**OrderBy Permitidos:**
```
timestamp, priority, slaBreached, closedAt
```

---

### **4 - AgentHardware**
Informações de hardware dos agentes

**Campos Disponíveis:**
```
siteName, agentHostname, osName, osVersion, osBuild, osArchitecture, 
processor, processorCores, processorThreads, processorArchitecture, 
totalMemoryGB, motherboardManufacturer, motherboardModel, 
biosVersion, biosManufacturer, collectedAt
```

**OrderBy Permitidos:**
```
siteName, agentHostname, collectedAt, osName
```

---

## 🏗️ Exemplo Completo - Criar Relatório de Logs

```bash
curl -X POST http://localhost:5299/api/reports/templates \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Erros dos Últimos 7 Dias",
    "description": "Análise de erros críticos no sistema",
    "datasetType": 1,
    "defaultFormat": 0,
    "layoutJson": {
      "title": "Relatório de Erros",
      "columns": [
        {"field": "timestamp", "header": "Data/Hora", "width": 20, "format": "datetime"},
        {"field": "level", "header": "Nível", "width": 10},
        {"field": "source", "header": "Fonte", "width": 20},
        {"field": "message", "header": "Mensagem", "width": 50}
      ],
      "pageSize": 200,
      "orientation": "landscape"
    },
    "filtersJson": {
      "level": ["Error", "Critical"],
      "daysBack": 7,
      "orderBy": "timestamp",
      "orderDirection": "desc",
      "limit": 10000
    },
    "createdBy": "admin@empresa.com"
  }'
```

**Resposta:**
```json
{
  "id": "507f1f77-bcf8-6f4f-b041-5d3b2c6a46c3",
  "name": "Erros dos Últimos 7 Dias",
  "version": 1,
  "isActive": true,
  "createdAt": "2026-03-08T14:22:00Z"
}
```

---

## 🔄 Exemplo Completo - Atualizar Relatório

```bash
curl -X PUT http://localhost:5299/api/reports/templates/507f1f77-bcf8-6f4f-b041-5d3b2c6a46c3 \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Erros dos Últimos 30 Dias",
    "description": "Análise expandida para todo o mês",
    "defaultFormat": 2,
    "filtersJson": {
      "level": ["Error", "Critical", "Warning"],
      "daysBack": 30,
      "orderBy": "timestamp",
      "orderDirection": "desc",
      "limit": 10000
    },
    "updatedBy": "admin@empresa.com"
  }'
```

**Resposta:**
```json
{
  "id": "507f1f77-bcf8-6f4f-b041-5d3b2c6a46c3",
  "name": "Erros dos Últimos 30 Dias",
  "version": 2,
  "updatedAt": "2026-03-08T15:10:00Z"
}
```

---

## 📦 TypeScript/JavaScript - Classe Helper

```typescript
class ReportTemplateAPI {
  private baseUrl: string = '/api/reports/templates';

  // Criar novo template
  async create(request: CreateTemplateRequest): Promise<ReportTemplate> {
    const response = await fetch(this.baseUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request)
    });

    if (!response.ok) {
      throw new Error(`Failed to create template: ${response.statusText}`);
    }

    return response.json();
  }

  // Atualizar template
  async update(id: string, request: UpdateTemplateRequest): Promise<ReportTemplate> {
    const response = await fetch(`${this.baseUrl}/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request)
    });

    if (!response.ok) {
      throw new Error(`Failed to update template: ${response.statusText}`);
    }

    return response.json();
  }

  // Listar templates
  async list(datasetType?: number, isActive?: boolean): Promise<ReportTemplate[]> {
    const params = new URLSearchParams();
    if (datasetType !== undefined) params.append('datasetType', String(datasetType));
    if (isActive !== undefined) params.append('isActive', String(isActive));

    const response = await fetch(
      `${this.baseUrl}${params.toString() ? '?' + params.toString() : ''}`
    );

    if (!response.ok) {
      throw new Error(`Failed to list templates: ${response.statusText}`);
    }

    return response.json();
  }

  // Obter by ID
  async getById(id: string): Promise<ReportTemplate> {
    const response = await fetch(`${this.baseUrl}/${id}`);

    if (!response.ok) {
      throw new Error(`Template not found: ${id}`);
    }

    return response.json();
  }

  // Deletar
  async delete(id: string): Promise<void> {
    const response = await fetch(`${this.baseUrl}/${id}`, {
      method: 'DELETE'
    });

    if (!response.ok) {
      throw new Error(`Failed to delete template: ${response.statusText}`);
    }
  }

  // Histórico de versões
  async getHistory(id: string, limit: number = 50): Promise<TemplateHistory[]> {
    const response = await fetch(`${this.baseUrl}/${id}/history?limit=${limit}`);

    if (!response.ok) {
      throw new Error(`Failed to fetch history: ${response.statusText}`);
    }

    return response.json();
  }
}

// Tipos
interface CreateTemplateRequest {
  name: string;
  description?: string;
  datasetType: number;
  defaultFormat: number;
  layoutJson: any;
  filtersJson?: any;
  createdBy?: string;
}

interface UpdateTemplateRequest {
  name?: string;
  description?: string;
  defaultFormat?: number;
  layoutJson?: any;
  filtersJson?: any;
  isActive?: boolean;
  updatedBy?: string;
}

interface ReportTemplate {
  id: string;
  name: string;
  description?: string;
  datasetType: number;
  defaultFormat: number;
  layoutJson: string;
  filtersJson?: string;
  isActive: boolean;
  version: number;
  createdAt: string;
  updatedAt: string;
  createdBy?: string;
  updatedBy?: string;
}

interface TemplateHistory {
  id: string;
  templateId: string;
  version: number;
  eventType: 'Created' | 'Updated' | 'Deleted';
  createdAt: string;
  createdBy?: string;
}

// Uso
const api = new ReportTemplateAPI();

// Criar
const template = await api.create({
  name: 'Novo Relatório',
  datasetType: 0,
  defaultFormat: 0,
  layoutJson: { title: 'Teste', columns: [] },
  createdBy: 'user@example.com'
});

// Atualizar
await api.update(template.id, {
  name: 'Nome Atualizado',
  updatedBy: 'user@example.com'
});

// Listar
const templates = await api.list(undefined, true);

// Histórico
const history = await api.getHistory(template.id);
```

---

## ⚠️ Mensagens de Erro Comuns

### Validação de Nome
```json
{
  "error": "Name must be between 2 and 200 characters"
}
```

### Dataset Type Inválido
```json
{
  "error": "Invalid datasetType. Valid values: 0-4"
}
```

### Format Não Habilitado
```json
{
  "error": "Format PDF is not enabled. Contact administrator."
}
```

### JSON Inválido
```json
{
  "error": "layoutJson must be valid JSON"
}
```

### Template Não Encontrado
```json
{
  "error": "Template not found"
}
```

---

## 🎯 Checklist para Criar Relatório

- [ ] Nome do relatório (2-200 chars)
- [ ] Descrição (opcional, até 2000 chars)
- [ ] Dataset type selecionado (0-4)
- [ ] Formato padrão (0=Excel, 1=CSV, 2=PDF)
- [ ] Layout JSON com título e colunas
- [ ] Filtros padrão (opcional)
- [ ] Usuário criando arquivo (email)
- [ ] Validar campos do dataset selecionado

---

## 🔗 Workflow Completo

```
1. GET /api/reports/datasets
   ↓ (listar datasets disponíveis)
   
2. Escolher um dataset (ex: Logs)
   ↓
   
3. POST /api/reports/templates
   ├─ name: "Meu Relatório"
   ├─ datasetType: 1
   ├─ layoutJson: {...}
   └─ filtersJson: {...}
   ↓ (receber templateId)
   
4. GET /api/reports/templates/{id}
   ↓ (confirmar criação)
   
5. Usar template para executar relatório
   └─ POST /api/reports/run
      ├─ templateId: (do passo 3)
      └─ runAsync: true
```
