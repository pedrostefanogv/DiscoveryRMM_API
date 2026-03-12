using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Serviço genérico de gerenciamento de anexos para qualquer escopo.
/// Reutilizável em tickets, notas, conhecimento, e outros contextos.
/// 
/// Responsabilidades:
/// - Validação de arquivo (tipo MIME, tamanho)
/// - Composição de ObjectKey com isolamento por cliente/escopo
/// - Persistência em Attachment
/// - Geração de URLs pré-assinadas para download seguro
/// - Gerenciamento de lifecycle (soft delete, limpeza)
/// </summary>
public class AttachmentService : IAttachmentService
{
    private readonly IAttachmentRepository _attachmentRepository;
    private readonly IObjectStorageService _storageService;
    private readonly IObjectStorageProviderFactory _storageFactory;
    private readonly ILogger<AttachmentService> _logger;

    // Limites e validações
    private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100 MB
    private static readonly HashSet<string> AllowedMimeTypes = new()
    {
        // Documentos
        "application/pdf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "text/plain",
        "text/csv",
        // Imagens
        "image/jpeg",
        "image/png",
        "image/gif",
        "image/webp",
        // Arquivos comprimidos
        "application/zip",
        "application/x-rar-compressed",
        "application/gzip",
        // JSON, XML
        "application/json",
        "application/xml",
        "text/xml"
    };

    public AttachmentService(
        IAttachmentRepository attachmentRepository,
        IObjectStorageService storageService,
        IObjectStorageProviderFactory storageFactory,
        ILogger<AttachmentService> logger)
    {
        _attachmentRepository = attachmentRepository ?? throw new ArgumentNullException(nameof(attachmentRepository));
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _storageFactory = storageFactory ?? throw new ArgumentNullException(nameof(storageFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PresignedUploadRequest> PreparePresignedUploadAsync(
        string entityType,
        Guid entityId,
        Guid? clientId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        int urlTtlMinutes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("EntityType cannot be empty", nameof(entityType));

        if (entityId == Guid.Empty)
            throw new ArgumentException("EntityId cannot be empty", nameof(entityId));

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("FileName cannot be empty", nameof(fileName));

        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("ContentType cannot be empty", nameof(contentType));

        if (fileSizeBytes <= 0)
            throw new ArgumentException("File size must be greater than zero", nameof(fileSizeBytes));

        if (fileSizeBytes > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"File size {fileSizeBytes} bytes exceeds maximum {MaxFileSizeBytes} bytes");

        var attachmentId = IdGenerator.NewId();
        var objectKey = ComposeObjectKey(clientId, entityType, entityId, attachmentId, fileName);
        var presignedUrl = await _storageService.GetPresignedUploadUrlAsync(
            objectKey,
            urlTtlMinutes,
            contentType,
            cancellationToken);

        _logger.LogInformation(
            "Prepared presigned upload for {EntityType}/{EntityId} with attachment {AttachmentId}",
            entityType,
            entityId,
            attachmentId);

        return new PresignedUploadRequest
        {
            AttachmentId = attachmentId,
            ObjectKey = objectKey,
            UploadUrl = presignedUrl,
            HttpMethod = "PUT",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(urlTtlMinutes)
        };
    }

    public async Task<Attachment> CompletePresignedUploadAsync(
        Guid attachmentId,
        string entityType,
        Guid entityId,
        Guid? clientId,
        string fileName,
        string contentType,
        long expectedSizeBytes,
        string objectKey,
        string? uploadedBy = null,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty)
            throw new ArgumentException("AttachmentId cannot be empty", nameof(attachmentId));

        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("EntityType cannot be empty", nameof(entityType));

        if (entityId == Guid.Empty)
            throw new ArgumentException("EntityId cannot be empty", nameof(entityId));

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("FileName cannot be empty", nameof(fileName));

        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("ContentType cannot be empty", nameof(contentType));

        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("ObjectKey cannot be empty", nameof(objectKey));

        if (expectedSizeBytes <= 0)
            throw new ArgumentException("ExpectedSizeBytes must be greater than zero", nameof(expectedSizeBytes));

        var existingAttachment = await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken);
        if (existingAttachment is not null)
            return existingAttachment;

        var exists = await _storageService.ExistsAsync(objectKey, cancellationToken);
        if (!exists)
            throw new InvalidOperationException("Arquivo ainda não encontrado no object storage.");

        var metadata = await _storageService.GetMetadataAsync(objectKey, cancellationToken);
        var sizeBytes = metadata?.SizeBytes ?? expectedSizeBytes;

        if (sizeBytes <= 0)
            throw new InvalidOperationException("Tamanho inválido para arquivo no object storage.");

        var attachment = new Attachment
        {
            Id = attachmentId,
            EntityType = entityType,
            EntityId = entityId,
            ClientId = clientId,
            FileName = fileName,
            StorageObjectKey = objectKey,
            StorageBucket = metadata?.Bucket ?? string.Empty,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            StorageChecksum = metadata?.Checksum,
            StorageProviderType = (int)(metadata?.StorageProvider ?? ObjectStorageProviderType.Local),
            UploadedBy = uploadedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        return await _attachmentRepository.CreateAsync(attachment, cancellationToken);
    }

    /// <summary>
    /// Fazer upload de um anexo para uma entidade específica.
    /// </summary>
    public async Task<Attachment> UploadAttachmentAsync(
        string entityType,
        Guid entityId,
        Guid? clientId,
        string fileName,
        Stream content,
        string contentType,
        string? uploadedBy = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("EntityType cannot be empty", nameof(entityType));

        if (entityId == Guid.Empty)
            throw new ArgumentException("EntityId cannot be empty", nameof(entityId));

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("FileName cannot be empty", nameof(fileName));

        if (content == null)
            throw new ArgumentNullException(nameof(content));

        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("ContentType cannot be empty", nameof(contentType));

        try
        {
            _logger.LogInformation(
                "Uploading attachment {FileName} for {EntityType}/{EntityId}",
                fileName, entityType, entityId);

            // Validar tamanho
            if (content.CanSeek && content.Length > MaxFileSizeBytes)
                throw new InvalidOperationException(
                    $"File size {content.Length} bytes exceeds maximum {MaxFileSizeBytes} bytes");

            // Validar MIME type
            if (!AllowedMimeTypes.Contains(contentType))
                _logger.LogWarning(
                    "Unusual MIME type {MimeType} for file {FileName}",
                    contentType, fileName);

            // Compor object key com isolamento por cliente/escopo
            var objectKey = ComposeObjectKey(clientId, entityType, entityId, fileName);

            // Fazer upload no storage
            var storageObject = await _storageService.UploadAsync(
                objectKey,
                content,
                contentType,
                cancellationToken);

            // Criar entidade Attachment
            var attachment = new Attachment
            {
                Id = IdGenerator.NewId(),
                EntityType = entityType,
                EntityId = entityId,
                ClientId = clientId,
                FileName = fileName,
                StorageObjectKey = storageObject.ObjectKey,
                StorageBucket = storageObject.Bucket,
                ContentType = contentType,
                SizeBytes = storageObject.SizeBytes,
                StorageChecksum = storageObject.Checksum,
                StorageProviderType = (int)storageObject.StorageProvider,
                UploadedBy = uploadedBy,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Persistir no BD
            var created = await _attachmentRepository.CreateAsync(attachment, cancellationToken);

            _logger.LogInformation(
                "Successfully uploaded attachment {AttachmentId} ({SizeBytes} bytes) for {EntityType}/{EntityId}",
                created.Id, created.SizeBytes, entityType, entityId);

            return created;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error uploading attachment {FileName} for {EntityType}/{EntityId}",
                fileName, entityType, entityId);
            throw;
        }
    }

    /// <summary>
    /// Fazer download de um anexo existente.
    /// </summary>
    public async Task<Stream> DownloadAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty)
            throw new ArgumentException("AttachmentId cannot be empty", nameof(attachmentId));

        try
        {
            var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken);
            if (attachment == null)
                throw new KeyNotFoundException($"Attachment {attachmentId} not found");

            if (attachment.IsDeleted)
                throw new InvalidOperationException($"Attachment {attachmentId} has been deleted");

            _logger.LogInformation("Downloading attachment {AttachmentId} ({FileName})",
                attachmentId, attachment.FileName);

            var stream = await _storageService.DownloadAsync(
                attachment.StorageObjectKey,
                cancellationToken);

            return stream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading attachment {AttachmentId}", attachmentId);
            throw;
        }
    }

    /// <summary>
    /// Gerar URL pré-assinada para download de um anexo.
    /// </summary>
    public async Task<string> GetPresignedDownloadUrlAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty)
            throw new ArgumentException("AttachmentId cannot be empty", nameof(attachmentId));

        try
        {
            var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, cancellationToken);
            if (attachment == null)
                throw new KeyNotFoundException($"Attachment {attachmentId} not found");

            if (attachment.IsDeleted)
                throw new InvalidOperationException($"Attachment {attachmentId} has been deleted");

            _logger.LogInformation("Generating presigned URL for attachment {AttachmentId}",
                attachmentId);

            // Use 24 horas como padrão (pode ser configurável via ServerConfiguration)
            var ttlHours = 24;

            var url = await _storageService.GetPresignedDownloadUrlAsync(
                attachment.StorageObjectKey,
                ttlHours,
                cancellationToken);

            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating presigned URL for attachment {AttachmentId}",
                attachmentId);
            throw;
        }
    }

    /// <summary>
    /// Deletar um anexo (soft delete).
    /// </summary>
    public async Task DeleteAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty)
            throw new ArgumentException("AttachmentId cannot be empty", nameof(attachmentId));

        try
        {
            _logger.LogInformation("Soft deleting attachment {AttachmentId}", attachmentId);

            await _attachmentRepository.SoftDeleteAsync(attachmentId, cancellationToken);

            _logger.LogInformation("Successfully soft deleted attachment {AttachmentId}", attachmentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting attachment {AttachmentId}", attachmentId);
            throw;
        }
    }

    /// <summary>
    /// Listar todos os anexos de uma entidade específica.
    /// </summary>
    public async Task<List<Attachment>> GetAttachmentsForEntityAsync(
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("EntityType cannot be empty", nameof(entityType));

        if (entityId == Guid.Empty)
            throw new ArgumentException("EntityId cannot be empty", nameof(entityId));

        return await _attachmentRepository.GetByEntityAsync(entityType, entityId, cancellationToken);
    }

    /// <summary>
    /// Deletar permanentemente attachments expirados ou órfãos (hard delete).
    /// </summary>
    public async Task<int> PermanentlyDeleteExpiredAttachmentsAsync(
        int olderThanDays,
        CancellationToken cancellationToken = default)
    {
        if (olderThanDays <= 0)
            throw new ArgumentException("olderThanDays must be positive", nameof(olderThanDays));

        try
        {
            _logger.LogInformation("Permanently deleting attachments soft-deleted more than {Days} days ago",
                olderThanDays);

            var expiredAttachments = await _attachmentRepository.GetSoftDeletedOlderThanAsync(
                olderThanDays,
                cancellationToken);

            int deleted = 0;

            foreach (var attachment in expiredAttachments)
            {
                try
                {
                    // Deletar do storage primeiro
                    await _storageService.DeleteAsync(
                        attachment.StorageObjectKey,
                        cancellationToken);

                    // Depois do BD
                    await _attachmentRepository.HardDeleteAsync(attachment.Id, cancellationToken);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error permanently deleting attachment {AttachmentId}",
                        attachment.Id);
                    // Continuar com próximos em vez de falhar todo o batch
                }
            }

            _logger.LogInformation(
                "Permanently deleted {Count} expired attachments",
                deleted);

            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error permanently deleting expired attachments");
            throw;
        }
    }

    /// <summary>
    /// Compor object key com isolamento por cliente/escopo profissional.
    /// Padrão: clients/{clientId}/{entityType}/{entityId}/attachments/{guid}/{filename}
    /// </summary>
    private string ComposeObjectKey(
        Guid? clientId,
        string entityType,
        Guid entityId,
        string fileName)
    {
        // Sanitizar nome do arquivo (remover caracteres perigosos)
        var safeName = SanitizeFileName(fileName);

        // Criar ID único para evitar colisão
        var attachmentGuid = Guid.NewGuid().ToString("N");

        return ComposeObjectKey(clientId, entityType, entityId, attachmentGuid, safeName);
    }

    private string ComposeObjectKey(
        Guid? clientId,
        string entityType,
        Guid entityId,
        Guid attachmentId,
        string fileName)
    {
        var safeName = SanitizeFileName(fileName);
        return ComposeObjectKey(clientId, entityType, entityId, attachmentId.ToString("N"), safeName);
    }

    private string ComposeObjectKey(
        Guid? clientId,
        string entityType,
        Guid entityId,
        string attachmentGuid,
        string safeName)
    {
        if (string.IsNullOrWhiteSpace(attachmentGuid))
            throw new ArgumentException("Attachment identifier cannot be empty", nameof(attachmentGuid));

        if (clientId.HasValue && clientId != Guid.Empty)
        {
            return $"clients/{clientId:N}/{entityType.ToLowerInvariant()}/{entityId:N}/attachments/{attachmentGuid.ToLowerInvariant()}/{safeName}";
        }

        return $"global/{entityType.ToLowerInvariant()}/{entityId:N}/attachments/{attachmentGuid.ToLowerInvariant()}/{safeName}";
    }

    /// <summary>
    /// Sanitizar nome do arquivo para segurança.
    /// </summary>
    private string SanitizeFileName(string fileName)
    {
        // Remover path traversal characters
        var sanitized = Path.GetFileName(fileName);

        // Remover caracteres perigosos
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c.ToString(), "");
        }

        // Remover caracteres especiais adicionais
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"[^\w\s\.\-]", "");

        return string.IsNullOrWhiteSpace(sanitized) ? "attachment" : sanitized;
    }
}
