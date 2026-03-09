# 🔍 GUIA DE DIAGNÓSTICO - Problemas no Download de Relatórios

## ✅ Status Atual
- ✓ API está funcionando
- ✓ Geração de relatórios funcionando
- ✓ Download dos arquivos funcionando
- ✓ Ambos endpoints funcionando (`/download` e `/download-stream`)

---

## 🚨 Identificar Seu Erro Específico

Dependendo do **status code** da resposta, o erro é diferente:

### ❌ **Status 404 - Not Found**
```
GET /api/reports/executions/{id}/download → 404
```

**Causa Possível:**
- `executionId` está errado
- Execução não existe no banco de dados
- Execução foi deletada

**Solução:**
1. Verifique o `executionId` está correto:
   ```javascript
   // ❌ ERRADO com hífens extras
   019cceea-cb47-737b-977a-a47da8972606
   
   // ✓ CORRETO (GUID padrão)
   019cceea-cb47-737b-977a-a47da8972606
   ```

2. Verifique se execução existe:
   ```bash
   curl -X GET "http://localhost:5299/api/reports/executions/019cceea-cb47-737b-977a-a47da8972606"
   ```

3. Se retornar 404, a execução não foi criada ou foi deletada

---

### ❌ **Status 202 ao Fazer Download**
```
GET /api/reports/executions/{id}/download → 202 (Accepted)
```

**Causa:**
Relatório ainda está **em processamento** (status = "Pending" ou "Running")

**Solução:**
```javascript
// ❌ ERRADO - tentar download logo após criar
const { executionId } = await runReport();
const download = await fetch(`/api/reports/executions/${executionId}/download`); // Status 202!

// ✓ CORRETO - aguardar conclusão
const { executionId } = await runReport();
await waitForCompletion(executionId); // Aguardar status === "Completed"
const download = await fetch(`/api/reports/executions/${executionId}/download`); // Status 200!
```

---

### ❌ **Status 500 - Internal Server Error**
```
GET /api/reports/executions/{id}/download → 500
```

**Causa Possível:**
- Arquivo foi deletado do disco
- Permissão de leitura negada
- Disco cheio
- Crash no serviço

**Solução:**
1. Verifique se arquivo existe:
   ```bash
   # Verificar diretório de relatórios
   dir "C:\Projetos\SRV_Meduza_2\src\Meduza.Api\bin\Debug\net10.0\report-exports\"
   ```

2. Regenere o relatório:
   ```javascript
   const result = await fetch('/api/reports/run', {
     method: 'POST',
     json: { templateId, format: 'Pdf', runAsync: true }
   });
   ```

3. Verifique logs da API

---

### ❌ **Status 400 - Bad Request**
```
GET /api/reports/executions/{id}/download → 400
```

**Causa:**
- `clientId` inválido (se fornecido)
- Formato de GUID incorreto

**Solução:**
```javascript
// ❌ ERRADO - clientId em formato inválido
fetch(`/api/reports/executions/${executionId}/download?clientId=abc-123`);

// ✓ CORRETO - GUID válido
fetch(`/api/reports/executions/${executionId}/download?clientId=019ccaa6-2d6d-71d5-b41f-45cb7e0deffe`);
```

---

## 🧪 Teste Rápido

```bash
# 1. Listar templates
curl -X GET http://localhost:5299/api/reports/templates

# 2. Executar relatório (pegar o primeiro template)
curl -X POST http://localhost:5299/api/reports/run \
  -H "Content-Type: application/json" \
  -d '{"templateId":"a47f4f44-1b06-4a9c-b180-20b9b0074c8b","format":"Pdf","runAsync":true}'

# 3. Copie o executionId e aguarde 5 segundos

# 4. Verificar status
curl -X GET http://localhost:5299/api/reports/executions/{executionId}

# 5. Quando status="Completed", fazer download
curl -X GET http://localhost:5299/api/reports/executions/{executionId}/download \
  -o relatorio.pdf

# 6. Verificar se arquivo foi salvo
dir relatorio.pdf
```

---

## 🔧 Checklist de Diagnóstico

Execute este checklist para identificar o problema:

- [ ] Relatório foi criado com `POST /api/reports/run`?
  - Verifique resposta: status 200 ou 202?
  - Pegue o `executionId`?

- [ ] Relatório concluiu?
  - `GET /api/reports/executions/{executionId}`
  - Status é "Completed"?
  - Tem `resultPath`?

- [ ] Arquivo existe no disco?
  - Caminho está certo?
  - Arquivo não está corrompido?

- [ ] Arquivo está acessível?
  - Permissões de leitura OK?
  - Disco tem espaço?

- [ ] Request está correto?
  - `executionId` é válido?
  - Se usa `clientId`, está no formato GUID?

---

## 📝 Exemplo Completo (JavaScript/TypeScript)

```typescript
interface DownloadResult {
  success: boolean;
  file?: Blob;
  fileName?: string;
  error?: string;
}

async function downloadReport(executionId: string): Promise<DownloadResult> {
  try {
    // 1. Verificar status
    const statusResponse = await fetch(
      `/api/reports/executions/${executionId}`
    );
    
    if (!statusResponse.ok) {
      return { 
        success: false, 
        error: `Status check failed: ${statusResponse.status}` 
      };
    }
    
    const execution = await statusResponse.json();
    
    // 2. Verificar se concluído
    if (execution.status !== "Completed") {
      return { 
        success: false, 
        error: `Report not completed. Status: ${execution.status}` 
      };
    }
    
    // 3. Fazer download
    const downloadResponse = await fetch(
      `/api/reports/executions/${executionId}/download`
    );
    
    if (!downloadResponse.ok) {
      return { 
        success: false, 
        error: `Download failed: ${downloadResponse.status}` 
      };
    }
    
    const blob = await downloadResponse.blob();
    const fileName = execution.resultPath 
      ? execution.resultPath.split('\\').pop() 
      : `report-${executionId}.pdf`;
    
    return {
      success: true,
      file: blob,
      fileName
    };
    
  } catch (error) {
    return { 
      success: false, 
      error: error instanceof Error ? error.message : String(error) 
    };
  }
}

// Uso:
const result = await downloadReport("019cceea-cb47-737b-977a-a47da8972606");

if (result.success) {
  // Salvar arquivo
  const url = URL.createObjectURL(result.file!);
  const a = document.createElement('a');
  a.href = url;
  a.download = result.fileName || 'relatório.pdf';
  a.click();
  URL.revokeObjectURL(url);
} else {
  console.error(`Erro: ${result.error}`);
}
```

---

## 💾 Possíveis Locais de Erro

### Backend (C#)
- [ ] ReportsController.DownloadExecution → retornar NotFound
- [ ] ReportService.GetDownloadAsync → retornar null
- [ ] File.Exists(execution.ResultPath) → false
- [ ] Arquivo > 50MB (carrega na memória)

### Frontend (JavaScript/TypeScript)
- [ ] Request mal formatado
- [ ] CORS bloqueando
- [ ] Timeout curto demais
- [ ] Authentication headers faltando

### Sistema de Arquivos
- [ ] Diretório não existe
- [ ] Permissões insuficientes
- [ ] Disco cheio
- [ ] Path inválido no Windows

---

## 📞 Próximas Ações

Se ainda não conseguir resolver:

1. **Compartilhe o erro exato que está recebendo** (status code, response body)
2. **Verifique os logs da API** (linha de erro no backend)
3. **Teste o curl acima** e compartilhe o resultado
4. **Confirme** se está usando `/download` ou `/download-stream`

Exemplo de resposta esperada ideal:

```bash
$ curl -v http://localhost:5299/api/reports/executions/019cceea-cb47-737b-977a-a47da8972606/download

> GET /api/reports/executions/019cceea-cb47-737b-977a-a47da8972606/download HTTP/1.1
< HTTP/1.1 200 OK
< Content-Type: application/pdf
< Content-Length: 45555
< Content-Disposition: attachment; filename=report-019cceea...pdf

[Binary PDF data...]
```
