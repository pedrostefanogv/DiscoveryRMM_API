using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.ValueObjects;

namespace Meduza.Core.Interfaces;

/// <summary>
/// Serviço genérico de gerenciamento de anexos para qualquer escopo.
/// Reutilizável em tickets, notas, conhecimento, e outros contextos que permitam arquivos.
/// 
/// Encapsula:
/// - Validação de arquivo (tipo MIME, tamanho)
/// - Composição de ObjectKey com isolamento por cliente/escopo
/// - Persistência em Attachment
/// - Geração de URLs pré-assinadas para download seguro
/// </summary>
public interface IAttachmentService
{
    /// <summary>
    /// Preparar upload direto no storage via URL pré-assinada.
    /// </summary>
    Task<PresignedUploadRequest> PreparePresignedUploadAsync(
        string entityType,
        Guid entityId,
        Guid? clientId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        int urlTtlMinutes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finalizar upload pré-assinado e persistir o anexo em banco.
    /// </summary>
    Task<Attachment> CompletePresignedUploadAsync(
        Guid attachmentId,
        string entityType,
        Guid entityId,
        Guid? clientId,
        string fileName,
        string contentType,
        long expectedSizeBytes,
        string objectKey,
        string? uploadedBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fazer upload de um anexo para uma entidade específica.
    /// </summary>
    /// <param name="entityType">Tipo da entidade pai (ex: "Ticket", "Note", "KnowledgeArticle")</param>
    /// <param name="entityId">ID da entidade pai</param>
    /// <param name="clientId">ID do cliente proprietário (para isolamento</param>
    /// <param name="fileName">Nome original do arquivo</param>
    /// <param name="content">Stream com conteúdo</param>
    /// <param name="contentType">Tipo MIME</param>
    /// <param name="uploadedBy">Identificador de quem fez upload (usuário, agent, etc.)</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>Entidade Attachment persistida em BD</returns>
    Task<Attachment> UploadAttachmentAsync(
        string entityType,
        Guid entityId,
        Guid? clientId,
        string fileName,
        Stream content,
        string contentType,
        string? uploadedBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fazer download de um anexo existente.
    /// </summary>
    /// <param name="attachmentId">ID do anexo</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>Stream com conteúdo do arquivo</returns>
    Task<Stream> DownloadAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gerar URL pré-assinada para download de um anexo.
    /// Útil para retornar em endpoints que fazem redirect 302.
    /// </summary>
    /// <param name="attachmentId">ID do anexo</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>URL pré-assinada válida por 24 horas</returns>
    Task<string> GetPresignedDownloadUrlAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletar um anexo (soft delete - marca com DeletedAt).
    /// </summary>
    /// <param name="attachmentId">ID do anexo</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    Task DeleteAttachmentAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Listar todos os anexos de uma entidade específica.
    /// </summary>
    /// <param name="entityType">Tipo de entidade</param>
    /// <param name="entityId">ID da entidade</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>Lista de Attachments (excluindo deletados)</returns>
    Task<List<Attachment>> GetAttachmentsForEntityAsync(
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletar permanentemente attachments expirados ou órfãos (hard delete).
    /// Utilizado por background service de retenção.
    /// </summary>
    /// <param name="olderThanDays">Deletar attachments mais antigos que N dias</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>Número de attachments deletados</returns>
    Task<int> PermanentlyDeleteExpiredAttachmentsAsync(int olderThanDays, CancellationToken cancellationToken = default);
}
