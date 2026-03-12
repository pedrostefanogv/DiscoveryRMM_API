namespace Meduza.Core.Entities;

/// <summary>
/// Anexo genérico que pode ser associado a qualquer entidade do sistema.
/// Permite reutilizar lógica de armazenamento em múltiplos contextos:
/// - Tickets/TicketComment
/// - EntityNote
/// - ReportExecution
/// - KnowledgeArticle
/// - Ou qualquer outra entidade que precise de arquivos anexados
/// </summary>
public class Attachment
{
    /// <summary>ID único do anexo</summary>
    public Guid Id { get; set; }

    /// <summary>Tipo de entidade pai (ex: "Ticket", "Note", "KnowledgeArticle", "ReportExecution")</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>ID da entidade pai onde este anexo está vinculado</summary>
    public Guid EntityId { get; set; }

    /// <summary>Para contextos multi-tenant, ID do cliente proprietário</summary>
    public Guid? ClientId { get; set; }

    /// <summary>Nome original do arquivo (com extensão)</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Descrição/título do anexo (opcional)</summary>
    public string? Description { get; set; }

    /// <summary>Chave do objeto no storage (ex: clients/{clientId}/tickets/{ticketId}/attachments/{guid}/{filename})</summary>
    public string StorageObjectKey { get; set; } = string.Empty;

    /// <summary>Nome do bucket onde está armazenado</summary>
    public string StorageBucket { get; set; } = string.Empty;

    /// <summary>Tipo MIME do arquivo</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Tamanho em bytes</summary>
    public long SizeBytes { get; set; }

    /// <summary>Checksum/ETag para integridade</summary>
    public string? StorageChecksum { get; set; }

    /// <summary>Tipo de provedor onde está armazenado (serializado como int)</summary>
    public int StorageProviderType { get; set; }

    /// <summary>Quem fez upload do anexo</summary>
    public string? UploadedBy { get; set; }

    /// <summary>Data/hora de criação</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Data/hora de última atualização</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft delete: se marcado para exclusão</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Verificar se o anexo foi deletado (soft delete)</summary>
    public bool IsDeleted => DeletedAt.HasValue;
}
