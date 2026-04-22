using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class ReportExecution
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public Guid? ClientId { get; set; }
    public ReportFormat Format { get; set; }
    public string? FiltersJson { get; set; }
    public ReportExecutionStatus Status { get; set; } = ReportExecutionStatus.Pending;

    // ============ Object Storage (novo) ============

    /// <summary>Tipo de provedor onde o relatório está armazenado</summary>
    public int StorageProviderType { get; set; }

    /// <summary>Nome do bucket onde o relatório está armazenado</summary>
    public string? StorageBucket { get; set; }

    /// <summary>Chave do objeto no storage (ex: clients/{clientId}/reports/{reportId}/{reportName}.{ext})</summary>
    public string? StorageObjectKey { get; set; }

    /// <summary>Tipo MIME do arquivo gerado</summary>
    public string? StorageContentType { get; set; }

    /// <summary>Tamanho em bytes do arquivo armazenado</summary>
    public long? StorageSizeBytes { get; set; }

    /// <summary>Checksum/ETag do arquivo para integridade</summary>
    public string? StorageChecksum { get; set; }

    /// <summary>URL pública pré-assinada para download (gerada sob demanda)</summary>
    public string? StoragePresignedUrl { get; set; }

    /// <summary>Data/hora de quando a URL pré-assinada foi gerada (para rastreamento)</summary>
    public DateTime? StoragePresignedUrlGeneratedAt { get; set; }

    // ============ Execução ============
    public int? RowCount { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ExecutionTimeMs { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? CreatedBy { get; set; }
}
