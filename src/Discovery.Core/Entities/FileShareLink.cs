namespace Discovery.Core.Entities;

/// <summary>
/// Link temporário de download (1 ou N arquivos) para compartilhamento externo
/// com data de validade, limite de downloads e contra-senha opcional.
/// </summary>
public class FileShareLink
{
    public Guid Id { get; set; }
    public string ShortCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Message { get; set; }

    public Guid? ClientId { get; set; }
    public Guid? SiteId { get; set; }

    public bool RequirePasscode { get; set; }
    public string? PasscodeHash { get; set; }
    public int FailedPasscodeAttempts { get; set; }
    public DateTime? LockedUntilUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
    public int? MaxDownloads { get; set; }
    public int DownloadCount { get; set; }

    public bool IsRevoked { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<FileShareLinkItem> Items { get; set; } = new List<FileShareLinkItem>();
}

public class FileShareLinkItem
{
    public Guid ShareLinkId { get; set; }
    public Guid ManagedFileId { get; set; }

    public FileShareLink? ShareLink { get; set; }
    public ManagedFile? ManagedFile { get; set; }
}
