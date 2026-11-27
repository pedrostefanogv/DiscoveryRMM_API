namespace Discovery.Core.Entities;

/// <summary>
/// Link temporário de upload pelo cliente (File Drop) com validade, contra-senha
/// opcional, modo único/múltiplo e restrições de tamanho, extensão e MIME.
/// </summary>
public class FileUploadLink
{
    public Guid Id { get; set; }
    public string ShortCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? TicketId { get; set; }
    public string TargetFolderPath { get; set; } = "/recebidos";

    public bool RequirePasscode { get; set; }
    public string? PasscodeHash { get; set; }
    public int FailedPasscodeAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
    public bool AllowMultiple { get; set; } = true;
    public int MaxFiles { get; set; } = 10;
    public int UploadedFilesCount { get; set; }

    public long MaxFileSizeBytes { get; set; } = 100L * 1024 * 1024; // 100 MiB
    public long MaxTotalSizeBytes { get; set; } = 500L * 1024 * 1024; // 500 MiB
    public long UploadedTotalBytes { get; set; }

    public string AllowedMimeTypesJson { get; set; } = "[]";
    public string AllowedExtensionsJson { get; set; } = "[]";

    public int? FileRetentionDays { get; set; }
    public bool NotifyOnUpload { get; set; } = true;

    public bool IsRevoked { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
