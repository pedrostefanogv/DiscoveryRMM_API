using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

/// <summary>
/// Repositório genérico para Attachment.
/// Suporta qualquer tipo de entidade que tenha anexos.
/// </summary>
public class AttachmentRepository : IAttachmentRepository
{
    private readonly MeduzaDbContext _db;

    public AttachmentRepository(MeduzaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Obter anexo por ID</summary>
    public async Task<Attachment?> GetByIdAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        return await _db.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
    }

    /// <summary>Criar novo anexo</summary>
    public async Task<Attachment> CreateAsync(Attachment attachment, CancellationToken cancellationToken = default)
    {
        if (attachment == null)
            throw new ArgumentNullException(nameof(attachment));

        if (attachment.Id == Guid.Empty)
            attachment.Id = Guid.NewGuid();

        attachment.CreatedAt = DateTime.UtcNow;
        attachment.UpdatedAt = DateTime.UtcNow;

        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync(cancellationToken);

        return attachment;
    }

    /// <summary>Atualizar anexo existente</summary>
    public async Task<Attachment> UpdateAsync(Attachment attachment, CancellationToken cancellationToken = default)
    {
        if (attachment == null)
            throw new ArgumentNullException(nameof(attachment));

        attachment.UpdatedAt = DateTime.UtcNow;
        _db.Attachments.Update(attachment);
        await _db.SaveChangesAsync(cancellationToken);

        return attachment;
    }

    /// <summary>Fazer soft delete de anexo</summary>
    public async Task<Attachment> SoftDeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await GetByIdAsync(attachmentId, cancellationToken);
        if (attachment == null)
            throw new KeyNotFoundException($"Attachment {attachmentId} not found");

        attachment.DeletedAt = DateTime.UtcNow;
        attachment.UpdatedAt = DateTime.UtcNow;

        _db.Attachments.Update(attachment);
        await _db.SaveChangesAsync(cancellationToken);

        return attachment;
    }

    /// <summary>Fazer hard delete permanente</summary>
    public async Task<bool> HardDeleteAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var attachment = await GetByIdAsync(attachmentId, cancellationToken);
        if (attachment == null)
            return false;

        _db.Attachments.Remove(attachment);
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Listar anexos de uma entidade</summary>
    public async Task<List<Attachment>> GetByEntityAsync(
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Attachments
            .AsNoTracking()
            .Where(a =>
                a.EntityType == entityType &&
                a.EntityId == entityId &&
                a.DeletedAt == null)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Listar anexos por cliente</summary>
    public async Task<List<Attachment>> GetByClientIdAsync(
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Attachments
            .AsNoTracking()
            .Where(a => a.ClientId == clientId && a.DeletedAt == null)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Listar anexos deletados (soft) há mais de X dias para limpeza</summary>
    public async Task<List<Attachment>> GetSoftDeletedOlderThanAsync(
        int days,
        CancellationToken cancellationToken = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-days);

        return await _db.Attachments
            .AsNoTracking()
            .Where(a => a.DeletedAt.HasValue && a.DeletedAt < cutoffDate)
            .OrderBy(a => a.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Contar anexos de uma entidade</summary>
    public async Task<int> CountByEntityAsync(
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        return await _db.Attachments
            .AsNoTracking()
            .CountAsync(a =>
                a.EntityType == entityType &&
                a.EntityId == entityId &&
                a.DeletedAt == null,
                cancellationToken);
    }
}
