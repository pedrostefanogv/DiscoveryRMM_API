using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

/// <summary>
/// Repositório genérico para entidade Attachment.
/// Suporta qualquer tipo de entidade que tenha anexos.
/// </summary>
public interface IAttachmentRepository
{
    /// <summary>Obter anexo por ID</summary>
    Task<Attachment?> GetByIdAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>Criar novo anexo</summary>
    Task<Attachment> CreateAsync(Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>Atualizar anexo existente</summary>
    Task<Attachment> UpdateAsync(Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>Fazer soft delete de anexo (marcar como deletado)</summary>
    Task<Attachment> SoftDeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>Fazer hard delete permanente (remover registro)</summary>
    Task<bool> HardDeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    /// <summary>Listar anexos de uma entidade (excluindo soft-deletados)</summary>
    Task<List<Attachment>> GetByEntityAsync(
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default);

    /// <summary>Listar anexos por cliente</summary>
    Task<List<Attachment>> GetByClientIdAsync(
        Guid clientId,
        CancellationToken cancellationToken = default);

    /// <summary>Listar anexos deletados (soft) há más de X dias para limpeza</summary>
    Task<List<Attachment>> GetSoftDeletedOlderThanAsync(
        int days,
        CancellationToken cancellationToken = default);

    /// <summary>Contar anexos de uma entidade</summary>
    Task<int> CountByEntityAsync(
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default);
}
