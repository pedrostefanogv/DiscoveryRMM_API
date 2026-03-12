# GUIA DE USO - Object Storage Attachment System

## 1. Aplicar Migrations ao Banco de Dados

Execute PRIMEIRO:
```bash
cd c:\Projetos\SRV_Meduza_2
dotnet ef database update --project src/Meduza.Migrations/Meduza.Migrations.csproj
```

Isto criará:
- 9 novas colunas em `server_configurations` table
- Nova tabela `attachments` com 19 colunas e 5 índices
- 8 novas colunas em `report_executions` table

## 2. Configurar Object Storage em Runtime

Na API, adicione um endpoint para configurar o storage (ou via UI):

```csharp
[HttpPost("api/admin/object-storage/config")]
[Authorize(Roles = "Administrator")]
public async Task<IActionResult> ConfigureObjectStorage(
    ObjectStorageConfigRequest request,
    [FromServices] IServerConfigurationRepository repo)
{
    var config = await repo.GetOrCreateDefaultAsync();
    
    // Para desenvolvimento, deixar como Local (padrão)
    // config.ObjectStorageProviderType = (int)ObjectStorageProviderType.Local;
    
    // Para produção, configurar S3:
    config.ObjectStorageProviderType = (int)ObjectStorageProviderType.AwsS3;
    config.ObjectStorageBucketName = "meduza-files";
    config.ObjectStorageEndpoint = "s3.amazonaws.com";
    config.ObjectStorageRegion = "us-east-1";
    config.ObjectStorageAccessKey = request.AccessKey; // from SecureString
    config.ObjectStorageSecretKey = request.SecretKey; // encrypted at rest
    config.ObjectStorageUrlTtlHours = 24;
    config.ObjectStorageUsePathStyle = false;
    config.ObjectStorageSslVerify = true;
    
    await repo.UpdateAsync(config);
    return Ok("Configuration updated");
}
```

## 3. Fazer Upload de Attachment em Qualquer Controller

### Exemplo: Ticket com Anexo

```csharp
[HttpPost("api/tickets/{ticketId}/attachments")]
[Authorize]
public async Task<IActionResult> AttachFile(
    Guid ticketId,
    IFormFile file,
    [FromServices] IAttachmentService attachmentService)
{
    // Validar ticket existe
    var ticket = await _ticketRepository.GetByIdAsync(ticketId);
    if (ticket == null)
        return NotFound();

    // Upload com tipo genérico "Ticket"
    var attachment = await attachmentService.UploadAttachmentAsync(
        entityType: "Ticket",
        entityId: ticketId,
        clientId: CurrentClient.Id,
        fileName: file.FileName,
        content: file.OpenReadStream(),
        contentType: file.ContentType,
        uploadedBy: CurrentUser.Id);

    return Created($"/api/attachments/{attachment.Id}", attachment);
}
```

### Exemplo: Knowledge Article com Anexo

```csharp
[HttpPost("api/knowledge/{articleId}/attachments")]
public async Task<IActionResult> AttachToKnowledge(
    Guid articleId,
    IFormFile file,
    [FromServices] IAttachmentService svc)
{
    var attachment = await svc.UploadAttachmentAsync(
        "KnowledgeArticle",  // ← mesmo padrão, diferente tipo
        articleId,
        CurrentClient.Id,
        file.FileName,
        file.OpenReadStream(),
        file.ContentType,
        CurrentUser.Id);

    return Created($"/api/attachments/{attachment.Id}", attachment);
}
```

## 4. Fazer Download de Attachment

```csharp
[HttpGet("api/attachments/{attachmentId}/download")]
public async Task<IActionResult> Download(
    Guid attachmentId,
    [FromServices] IAttachmentService svc)
{
    // Validar autor/cliente tem acesso
    var attachment = await svc.GetAttachmentAsync(attachmentId);
    if (attachment == null)
        return NotFound();

    var stream = await svc.DownloadAttachmentAsync(attachmentId);
    return File(stream, attachment.ContentType, attachment.FileName);
}
```

## 5. Gerar URL Pré-Assinada (Privada)

```csharp
[HttpGet("api/attachments/{attachmentId}/presigned-url")]
public async Task<IActionResult> GetPresignedUrl(
    Guid attachmentId,
    [FromServices] IAttachmentService svc)
{
    var url = await svc.GetPresignedDownloadUrlAsync(attachmentId);
    return Ok(new { downloadUrl = url });
}

// URL é privada e válida por 24h (configurável)
// Exemplo resposta:
// {
//   "downloadUrl": "http://localhost:5000/app_data/object-storage/clients/..."
// }
```

## 6. Listar Attachments de uma Entidade

```csharp
[HttpGet("api/tickets/{ticketId}/attachments")]
public async Task<IActionResult> ListTicketAttachments(
    Guid ticketId,
    [FromServices] IAttachmentService svc)
{
    var attachments = await svc.GetAttachmentsForEntityAsync(
        entityType: "Ticket",
        entityId: ticketId,
        clientId: CurrentClient.Id);

    return Ok(attachments);
}
```

## 7. Deletar Attachment (Soft Delete)

```csharp
[HttpDelete("api/attachments/{attachmentId}")]
[Authorize]
public async Task<IActionResult> DeleteAttachment(
    Guid attachmentId,
    [FromServices] IAttachmentService svc)
{
    await svc.DeleteAttachmentAsync(attachmentId);
    return NoContent();
}

// DeletedAt é settado, arquivo NÃO é deletado imediatamente
// Permite recovery por 7 dias (configurável)
```

## 8. Limpeza Automática de Attachments Expirados

Adicione um background service:

```csharp
[BackgroundService]
public class AttachmentExpirationService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AttachmentExpirationService> _logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var attachmentService = scope.ServiceProvider.GetRequiredService<IAttachmentService>();
                
                // Deletar permanentemente attachments soft-deleted há >7 dias
                var deleted = await attachmentService.PermanentlyDeleteExpiredAttachmentsAsync(
                    olderThanDays: 7, 
                    stoppingToken);

                _logger.LogInformation("Permanently deleted {Count} expired attachments", deleted);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning expired attachments");
            }

            // Rodar diariamente
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}

// Em Program.cs:
builder.Services.AddHostedService<AttachmentExpirationService>();
```

## 9. Estrutura de Armazenamento no Disco (Local)

```
c:\Projetos\SRV_Meduza_2\bin\Release\net10.0\app_data\object-storage\
├── clients/
│   └── {clientId}/
│       ├── ticket/
│       │   └── {ticketId}/
│       │       └── attachments/
│       │           └── {attachmentId}/
│       │               └── document.pdf
│       ├── note/
│       │   └── {noteId}/attachments/...
│       ├── knowledgearticle/
│       │   └── {articleId}/attachments/...
│       └── [any future entity type]/...
└── global/
    ├── knowledgearticle/
    │   └── [system-wide attachments]
    └── [other global scopes]/
```

## 10. Estrutura de Armazenamento no S3

```
s3://meduza-files/
├── clients/
│   └── {clientId}/
│       ├── ticket/{ticketId}/attachments/{guid}/file.pdf
│       ├── note/{noteId}/attachments/{guid}/file.docx
│       ├── knowledgearticle/{articleId}/attachments/{guid}/guide.md
│       └── [future types]/
└── global/
    ├── knowledgearticle/{id}/attachments/{guid}/system-guide.pdf
    └── [future global types]/
```

**Padrão é IDÊNTICO localmente e em S3 - sem mudança de código!**

## 11. Monitoramento & Observabilidade

Todos os métodos fazem logging:

```
[Information] Uploading object clients/a1b2c3d4/ticket/12345/attachments/uuid/invoice.pdf to bucket meduza-files
[Information] Successfully uploaded object [...] (2048000 bytes)
[Warning] Using local disk file storage - FOR DEVELOPMENT ONLY!
[Error] Error deleting object [...] from meduza-files
```

Monitore logs para erros de storage via ElasticSearch/Datadog.

## 12. Configuração de Produção Típica

```json
// appsettings.Production.json
{
  "ConnectionStrings": { /* ... */ },
  "ObjectStorage": {
    "ProviderType": "AwsS3",
    "BucketName": "meduza-files-prod",
    "Endpoint": "s3.us-east-1.amazonaws.com",
    "Region": "us-east-1",
    "AccessKey": "${AWS_ACCESS_KEY_ID}",
    "SecretKey": "${AWS_SECRET_ACCESS_KEY}",
    "UrlTtlHours": 1,  // URLs válidas por 1h apenas
    "UsePathStyle": false,
    "SslVerify": true
  }
}
```

---

**PRÓXIMO PASSO:** Execute `dotnet ef database update` e depois implemente os endpoints acima em seus Controllers.
