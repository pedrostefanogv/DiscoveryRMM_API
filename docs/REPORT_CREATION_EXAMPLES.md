# 💻 Exemplos Práticos - Criação de Relatórios

## 🚀 Exemplo 1: Criar Relatório de Software (cURL)

```bash
#!/bin/bash
# Criar relatório de software por site

TEMPLATE_ID=$(curl -s -X POST http://localhost:5299/api/reports/templates \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Software por Site",
    "description": "Inventário de software agrupado por site",
    "datasetType": 0,
    "defaultFormat": 0,
    "layoutJson": {
      "title": "Inventário de Software",
      "columns": [
        {"field": "siteName", "header": "Site", "width": 25},
        {"field": "softwareName", "header": "Software", "width": 35},
        {"field": "publisher", "header": "Fabricante", "width": 25},
        {"field": "version", "header": "Versão", "width": 15}
      ],
      "pageSize": 100,
      "orientation": "landscape"
    },
    "filtersJson": {
      "limit": 10000,
      "orderBy": "siteName",
      "orderDirection": "asc"
    },
    "createdBy": "automation@empresa.com"
  }' | jq -r '.id')

echo "✓ Template criado: $TEMPLATE_ID"

# Usar o template para gerar relatório
EXEC_ID=$(curl -s -X POST http://localhost:5299/api/reports/run \
  -H "Content-Type: application/json" \
  -d "{
    \"templateId\": \"$TEMPLATE_ID\",
    \"format\": \"Xlsx\",
    \"runAsync\": true
  }" | jq -r '.executionId')

echo "✓ Relatório enfileirado: $EXEC_ID"

# Aguardar conclusão
echo "⏳ Aguardando conclusão..."
for i in {1..60}; do
  STATUS=$(curl -s -X GET http://localhost:5299/api/reports/executions/$EXEC_ID | jq -r '.status')
  if [ "$STATUS" = "Completed" ]; then
    echo "✓ Relatório concluído!"
    # Download
    curl -X GET http://localhost:5299/api/reports/executions/$EXEC_ID/download \
      -o software_report.xlsx
    echo "✓ Arquivo salvo: software_report.xlsx"
    break
  elif [ "$STATUS" = "Failed" ]; then
    echo "✗ Falha na geração"
    break
  fi
  sleep 2
done
```

---

## 🐍 Exemplo 2: Criar Relatório com Python

```python
import requests
import json
import time

API_URL = "http://localhost:5299"

class ReportManager:
    def __init__(self, base_url=API_URL):
        self.base_url = base_url
        self.headers = {"Content-Type": "application/json"}
    
    def create_template(self, name, dataset_type, columns, filters=None, user="api@automation"):
        """Criar novo template de relatório"""
        payload = {
            "name": name,
            "datasetType": dataset_type,
            "defaultFormat": 0,  # Excel
            "layoutJson": {
                "title": name,
                "columns": columns,
                "pageSize": 200,
                "orientation": "landscape"
            },
            "filtersJson": filters or {},
            "createdBy": user
        }
        
        response = requests.post(
            f"{self.base_url}/api/reports/templates",
            json=payload,
            headers=self.headers
        )
        
        if response.status_code == 201:
            template = response.json()
            print(f"✓ Template criado: {template['id']}")
            return template
        else:
            print(f"✗ Erro: {response.status_code}")
            print(response.text)
            return None
    
    def update_template(self, template_id, updates, user="api@automation"):
        """Atualizar template existente"""
        payload = {**updates, "updatedBy": user}
        
        response = requests.put(
            f"{self.base_url}/api/reports/templates/{template_id}",
            json=payload,
            headers=self.headers
        )
        
        if response.status_code == 200:
            template = response.json()
            print(f"✓ Template atualizado (versão {template['version']})")
            return template
        else:
            print(f"✗ Erro: {response.status_code}")
            return None
    
    def run_report(self, template_id, format_type="Xlsx", async_run=True):
        """Executar relatório baseado em template"""
        payload = {
            "templateId": template_id,
            "format": format_type,
            "runAsync": async_run
        }
        
        response = requests.post(
            f"{self.base_url}/api/reports/run",
            json=payload,
            headers=self.headers
        )
        
        if response.status_code in [200, 202]:
            result = response.json()
            print(f"✓ Relatório iniciado: {result['executionId']}")
            return result
        else:
            print(f"✗ Erro: {response.status_code}")
            return None
    
    def wait_completion(self, execution_id, timeout_seconds=120):
        """Aguardar conclusão do relatório"""
        start = time.time()
        while time.time() - start < timeout_seconds:
            response = requests.get(
                f"{self.base_url}/api/reports/executions/{execution_id}"
            )
            
            if response.status_code == 200:
                execution = response.json()
                status = execution['status']
                
                if status == "Completed":
                    return True, execution
                elif status == "Failed":
                    return False, execution
                
                print(f"  Status: {status}")
            
            time.sleep(2)
        
        return False, {"error": "Timeout"}
    
    def download_report(self, execution_id, output_file):
        """Fazer download do relatório"""
        response = requests.get(
            f"{self.base_url}/api/reports/executions/{execution_id}/download"
        )
        
        if response.status_code == 200:
            with open(output_file, 'wb') as f:
                f.write(response.content)
            print(f"✓ Arquivo salvo: {output_file}")
            return True
        else:
            print(f"✗ Erro ao baixar: {response.status_code}")
            return False

# ============================================================================
# USO PRÁTICO
# ============================================================================

manager = ReportManager()

# 1. Criar template de software
columns = [
    {"field": "siteName", "header": "Site", "width": 25},
    {"field": "agentHostname", "header": "Hostname", "width": 25},
    {"field": "softwareName", "header": "Software", "width": 35},
    {"field": "version", "header": "Versão", "width": 15},
    {"field": "publisher", "header": "Fabricante", "width": 25},
]

template = manager.create_template(
    name="Software Inventory Report",
    dataset_type=0,  # SoftwareInventory
    columns=columns,
    filters={
        "limit": 10000,
        "orderBy": "softwareName",
        "orderDirection": "asc"
    }
)

if template:
    # 2. Executar relatório
    result = manager.run_report(template['id'], format_type="Xlsx")
    
    if result:
        execution_id = result['executionId']
        
        # 3. Aguardar conclusão
        success, execution = manager.wait_completion(execution_id)
        
        if success:
            # 4. Download
            manager.download_report(execution_id, "software_report.xlsx")
        else:
            print(f"✗ Erro: {execution.get('errorMessage')}")
```

---

## 🃏 Exemplo 3: Criar Relatório de Logs (TypeScript/Node.js)

```typescript
import axios, { AxiosInstance } from 'axios';

interface TemplateColumn {
  field: string;
  header: string;
  width: number;
  format?: 'text' | 'datetime' | 'number' | 'currency';
}

interface ReportFilters {
  level?: string[];
  daysBack?: number;
  limit?: number;
  orderBy?: string;
  orderDirection?: 'asc' | 'desc';
}

class ReportAPI {
  private client: AxiosInstance;

  constructor(baseUrl: string = 'http://localhost:5299') {
    this.client = axios.create({
      baseURL: baseUrl,
      headers: { 'Content-Type': 'application/json' }
    });
  }

  /**
   * Criar novo template de relatório de logs
   */
  async createLogsTemplate(
    name: string,
    columns: TemplateColumn[],
    filters?: ReportFilters,
    user: string = 'api@automation'
  ) {
    try {
      const response = await this.client.post('/api/reports/templates', {
        name,
        description: `Relatório de logs: ${name}`,
        datasetType: 1, // Logs
        defaultFormat: 0, // Excel
        layoutJson: {
          title: name,
          columns,
          pageSize: 500,
          orientation: 'landscape'
        },
        filtersJson: filters || {
          level: ['Error', 'Warning'],
          limit: 10000,
          orderBy: 'timestamp',
          orderDirection: 'desc'
        },
        createdBy: user
      });

      console.log('✓ Template criado:', response.data.id);
      return response.data;
    } catch (error) {
      console.error('✗ Erro ao criar template:', error);
      throw error;
    }
  }

  /**
   * Executar relatório
   */
  async executeReport(templateId: string, format: 'Xlsx' | 'Csv' | 'Pdf' = 'Xlsx') {
    try {
      const response = await this.client.post('/api/reports/run', {
        templateId,
        format,
        runAsync: true
      });

      console.log('✓ Relatório iniciado:', response.data.executionId);
      return response.data;
    } catch (error) {
      console.error('✗ Erro ao executar:', error);
      throw error;
    }
  }

  /**
   * Aguardar conclusão e fazer download
   */
  async waitAndDownload(executionId: string, outputPath: string) {
    const maxAttempts = 60;
    const delayMs = 2000;

    for (let attempt = 0; attempt < maxAttempts; attempt++) {
      try {
        const statusResponse = await this.client.get(
          `/api/reports/executions/${executionId}`
        );

        const { status, resultPath } = statusResponse.data;

        if (status === 'Completed') {
          console.log('✓ Relatório concluído!');

          // Download
          const downloadResponse = await this.client.get(
            `/api/reports/executions/${executionId}/download`,
            { responseType: 'arraybuffer' }
          );

          const fs = require('fs');
          fs.writeFileSync(outputPath, downloadResponse.data);
          console.log(`✓ Arquivo salvo: ${outputPath}`);
          return true;
        } else if (status === 'Failed') {
          console.error('✗ Falha na geração');
          return false;
        }

        console.log(`  [${attempt + 1}/${maxAttempts}] Status: ${status}`);
      } catch (error) {
        console.error('✗ Erro ao verificar status:', error);
      }

      await new Promise((r) => setTimeout(r, delayMs));
    }

    console.error('✗ Timeout aguardando relatório');
    return false;
  }
}

// ============================================================================
// USO PRÁTICO
// ============================================================================

async function main() {
  const api = new ReportAPI();

  try {
    // Criar template de logs com erros
    const template = await api.createLogsTemplate(
      'Erros Críticos - Últimos 7 Dias',
      [
        { field: 'timestamp', header: 'Data/Hora', width: 20, format: 'datetime' },
        { field: 'level', header: 'Nível', width: 10 },
        { field: 'source', header: 'Fonte', width: 20 },
        { field: 'message', header: 'Mensagem', width: 60 }
      ],
      {
        level: ['Error', 'Critical'],
        daysBack: 7,
        limit: 10000,
        orderBy: 'timestamp',
        orderDirection: 'desc'
      },
      'admin@empresa.com'
    );

    // Executar relatório
    const execution = await api.executeReport(template.id, 'Xlsx');

    // Aguardar e download
    await api.waitAndDownload(execution.executionId, './error_logs.xlsx');
  } catch (error) {
    console.error('Erro:', error);
  }
}

main();
```

---

## 🎨 Exemplo 4: Criar Relatório de Hardware com Seções

```json
{
  "name": "Hardware Detalhado",
  "description": "Análise completa de hardware dos servidores",
  "datasetType": 4,
  "defaultFormat": 2,
  "layoutJson": {
    "title": "Relatório Detalhado de Hardware",
    "subtitle": "Infraestrutura e Recursos dos Agentes",
    "orientation": "landscape",
    "pageSize": 30,
    "sections": [
      {
        "title": "Localização",
        "columns": [
          {"field": "siteName", "header": "Site", "width": 20},
          {"field": "agentHostname", "header": "Hostname", "width": 20}
        ]
      },
      {
        "title": "Sistema Operacional",
        "columns": [
          {"field": "osName", "header": "SO", "width": 20},
          {"field": "osVersion", "header": "Versão", "width": 12},
          {"field": "osBuild", "header": "Build", "width": 12},
          {"field": "osArchitecture", "header": "Arquitetura", "width": 12}
        ]
      },
      {
        "title": "Processador",
        "columns": [
          {"field": "processor", "header": "Modelo", "width": 25},
          {"field": "processorCores", "header": "Cores", "width": 10, "format": "number"},
          {"field": "processorThreads", "header": "Threads", "width": 10, "format": "number"}
        ]
      },
      {
        "title": "Memória",
        "columns": [
          {"field": "totalMemoryGB", "header": "RAM (GB)", "width": 15, "format": "number"}
        ]
      }
    ]
  },
  "filtersJson": {
    "limit": 5000,
    "orderBy": "siteName",
    "orderDirection": "asc"
  },
  "createdBy": "infrastructure@empresa.com"
}
```

---

## 📋 Exemplo 5: Criar Relatório de Auditoria

```json
{
  "name": "Auditoria de Mudanças - 30 Dias",
  "description": "Rastreamento de todas as alterações em configurações",
  "datasetType": 2,
  "defaultFormat": 0,
  "layoutJson": {
    "title": "Relatório de Auditoria",
    "columns": [
      {"field": "changedAt", "header": "Data/Hora", "width": 20, "format": "datetime"},
      {"field": "entityType", "header": "Tipo de Entidade", "width": 20},
      {"field": "fieldName", "header": "Campo Alterado", "width": 25},
      {"field": "oldValue", "header": "Valor Anterior", "width": 30},
      {"field": "newValue", "header": "Valor Novo", "width": 30},
      {"field": "changedBy", "header": "Alterado Por", "width": 20},
      {"field": "reason", "header": "Motivo", "width": 40}
    ],
    "pageSize": 100,
    "orientation": "landscape"
  },
  "filtersJson": {
    "daysBack": 30,
    "limit": 10000,
    "orderBy": "timestamp",
    "orderDirection": "desc"
  },
  "createdBy": "audit@empresa.com"
}
```

---

## 🔄 Exemplo 6: Atualizar Template Existente

```bash
# Atualizar template para adicionar mais colunas

curl -X PUT http://localhost:5299/api/reports/templates/a47f4f44-1b06-4a9c-b180-20b9b0074c8b \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Software Inventory - Versão 2",
    "description": "Adicionado campos de compatibilidade",
    "layoutJson": {
      "title": "Inventário de Software - V2",
      "columns": [
        {"field": "siteName", "header": "Site", "width": 20},
        {"field": "softwareName", "header": "Software", "width": 30},
        {"field": "version", "header": "Versão", "width": 15},
        {"field": "publisher", "header": "Fabricante", "width": 20},
        {"field": "installedAt", "header": "Data Instalação", "width": 18, "format": "datetime"},
        {"field": "lastSeenAt", "header": "Última Visualização", "width": 18, "format": "datetime"}
      ],
      "pageSize": 100,
      "orientation": "landscape"
    },
    "updatedBy": "admin@empresa.com"
  }'
```

---

## ✨ Dicas Importantes

### 1. **Validação de Campos**
Sempre verificar se o campo existe no dataset selecionado:

```python
# ✓ CORRETO - campo existe em SoftwareInventory
"field": "softwareName"

# ✗ ERRADO - campo não existe
"field": "invalidField"
```

### 2. **OrderBy vs Field Names**
```json
{
  "filtersJson": {
    "orderBy": "timestamp"  // ← Use "timestamp", não "changedAt"
  }
}
```

### 3. **Formato de Data**
```json
{
  "filtersJson": {
    "from": "2026-03-01T00:00:00Z",
    "to": "2026-03-31T23:59:59Z"
  }
}
```

### 4. **Limites**
```json
{
  "filtersJson": {
    "limit": 10000  // Máximo: 10000 linhas
  }
}
```

---

## 🐛 Troubleshooting

### Template não criado
```bash
# Verificar erro
curl -X POST http://localhost:5299/api/reports/templates \
  -H "Content-Type: application/json" \
  -d '...' | jq '.'

# Possíveis problemas:
# - name muito curto (< 2 chars)
# - datasetType inválido
# - layoutJson JSON inválido
```

### Relatório não gera
```bash
# Verificar status
curl http://localhost:5299/api/reports/executions/{executionId} | jq '.status'

# Se status = "Failed", verificar erro
curl http://localhost:5299/api/reports/executions/{executionId} | jq '.errorMessage'
```

---

## 📊 Resumo de Datasets

| ID | Nome | Descrição | OrderBy Padrão |
|----|------|-----------|---|
| 0 | SoftwareInventory | Software instalado | softwareName |
| 1 | Logs | Eventos de sistema | timestamp |
| 2 | ConfigurationAudit | Alterações de config | timestamp |
| 3 | Tickets | Tickets de suporte | timestamp |
| 4 | AgentHardware | Hardware dos agentes | siteName |
