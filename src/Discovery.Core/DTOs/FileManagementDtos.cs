using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

public sealed record ManagedFileDto(
    Guid Id,
    string Name,
    string FileName,
    string? Description,
    string? Category,
    string FolderPath,
    string[] Tags,
    FileScope Scope,
    Guid? ClientId,
    string? ClientName,
    Guid? SiteId,
    string? SiteName,
    FileVisibility Visibility,
    FileSource Source,
    Guid? SourceUploadLinkId,
    string ContentType,
    long SizeBytes,
    string? Sha256,
    long DownloadCount,
    bool IsActive,
    DateTime? ExpiresAtUtc,
    string? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ManagedFileFilter(
    FileScope? Scope = null,
    Guid? ClientId = null,
    Guid? SiteId = null,
    FileVisibility? Visibility = null,
    FileSource? Source = null,
    string? Category = null,
    string? FolderPath = null,
    string? Search = null,
    bool IncludeInactive = false,
    int Page = 1,
    int PageSize = 50);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record BeginManagedFileUploadRequest(
    string Name,
    string FileName,
    string ContentType,
    long SizeBytes,
    FileScope Scope,
    Guid? ClientId,
    Guid? SiteId,
    FileVisibility Visibility,
    string? Description = null,
    string? Category = null,
    string? FolderPath = null,
    string[]? Tags = null,
    DateTime? ExpiresAtUtc = null);

public sealed record BeginManagedFileUploadResponse(
    Guid FileId,
    string ObjectKey,
    string Mode, // "single" | "multipart"
    string? UploadUrl,
    string? UploadId,
    long PartSizeBytes,
    int PartUrlTtlMinutes);

public sealed record CompleteManagedFileUploadRequest(
    Guid FileId,
    string ObjectKey,
    string Name,
    string FileName,
    string ContentType,
    long SizeBytes,
    FileScope Scope,
    Guid? ClientId,
    Guid? SiteId,
    FileVisibility Visibility,
    string? Description = null,
    string? Category = null,
    string? FolderPath = null,
    string[]? Tags = null,
    DateTime? ExpiresAtUtc = null,
    string? UploadId = null,
    IReadOnlyList<MultipartUploadPartStateDto>? Parts = null,
    string? Sha256 = null);

public sealed record UpdateManagedFileRequest(
    string? Name = null,
    string? Description = null,
    string? Category = null,
    string? FolderPath = null,
    string[]? Tags = null,
    FileScope? Scope = null,
    Guid? ClientId = null,
    Guid? SiteId = null,
    FileVisibility? Visibility = null,
    bool? IsActive = null,
    DateTime? ExpiresAtUtc = null,
    bool ClearExpiresAt = false);

public sealed record FileHubStatsDto(
    int TotalFiles,
    long TotalBytes,
    long GlobalBytes,
    long ClientBytes,
    long SiteBytes,
    long TotalDownloads,
    int ActiveShareLinks,
    int ActiveUploadLinks);

// ── Links de Download (Share) ───────────────────────────────────────────────

public sealed record CreateShareLinkRequest(
    string Title,
    string? Message,
    Guid? ClientId,
    Guid? SiteId,
    IReadOnlyList<Guid> FileIds,
    DateTime ExpiresAtUtc,
    bool RequirePasscode,
    string? Passcode,
    int? MaxDownloads);

public sealed record FileShareLinkDto(
    Guid Id,
    string ShortCode,
    string Title,
    string? Message,
    Guid? ClientId,
    string? ClientName,
    Guid? SiteId,
    string? SiteName,
    bool RequirePasscode,
    DateTime ExpiresAtUtc,
    int? MaxDownloads,
    int DownloadCount,
    bool IsRevoked,
    bool IsExpired,
    IReadOnlyList<ManagedFileSummaryDto> Files,
    string? CreatedBy,
    DateTime CreatedAt);

public sealed record ManagedFileSummaryDto(
    Guid Id,
    string Name,
    string FileName,
    string? Description,
    string? Category,
    string ContentType,
    long SizeBytes,
    string? Sha256,
    FileScope Scope,
    string FolderPath = "/");

// ── Pastas Virtuais/Explícitas ───────────────────────────────────────────────

public sealed record ManagedFileFolderDto(
    Guid Id,
    string Name,
    string Path,
    string ParentPath,
    string? Description,
    FileScope Scope,
    Guid? ClientId,
    string? ClientName,
    Guid? SiteId,
    string? SiteName,
    FileVisibility Visibility,
    int FileCount,
    int SubfolderCount,
    bool IsVirtual,
    string? CreatedBy,
    DateTime CreatedAt);

public sealed record CreateManagedFileFolderRequest(
    string Name,
    string? ParentPath,
    string? Description,
    FileScope Scope,
    Guid? ClientId,
    Guid? SiteId,
    FileVisibility Visibility);

public sealed record UpdateManagedFileFolderRequest(
    string? Name = null,
    string? Description = null,
    FileVisibility? Visibility = null);

public sealed record PublicFolderDto(
    string Path,
    string Name,
    string ParentPath,
    string? Description,
    int FileCount);

// ── Links de Upload do Cliente (File Drop) ──────────────────────────────────

public sealed record CreateUploadLinkRequest(
    string Title,
    string? Description,
    Guid? ClientId,
    Guid? SiteId,
    string? TargetFolderPath,
    DateTime ExpiresAtUtc,
    bool RequirePasscode,
    string? Passcode,
    bool AllowMultiple,
    int MaxFiles,
    long MaxFileSizeBytes,
    long MaxTotalSizeBytes,
    string[]? AllowedExtensions,
    string[]? AllowedMimeTypes,
    int? FileRetentionDays,
    bool NotifyOnUpload,
    Guid? TicketId = null);

public sealed record FileUploadLinkDto(
    Guid Id,
    string ShortCode,
    string Title,
    string? Description,
    Guid? ClientId,
    string? ClientName,
    Guid? SiteId,
    string? SiteName,
    string TargetFolderPath,
    bool RequirePasscode,
    DateTime ExpiresAtUtc,
    bool AllowMultiple,
    int MaxFiles,
    int UploadedFilesCount,
    long MaxFileSizeBytes,
    long MaxTotalSizeBytes,
    long UploadedTotalBytes,
    string[] AllowedExtensions,
    string[] AllowedMimeTypes,
    int? FileRetentionDays,
    bool NotifyOnUpload,
    bool IsRevoked,
    bool IsExpired,
    string? CreatedBy,
    DateTime CreatedAt,
    Guid? TicketId = null);

// ── Portal Público (/download) ──────────────────────────────────────────────

public sealed record PublicZeroTouchInfoDto(
    bool Enabled,
    bool Available,
    string? Version,
    string? FileName,
    long? SizeBytes,
    string? Sha256);

public sealed record PublicPortalDto(
    bool Enabled,
    string? Title,
    string? Description,
    string? BrandName,
    string? LogoUrl,
    PublicZeroTouchInfoDto ZeroTouch,
    IReadOnlyList<ManagedFileSummaryDto> Files,
    IReadOnlyList<string> Categories,
    IReadOnlyList<PublicFolderDto> Folders);

public sealed record PublicShareInfoDto(
    string ShortCode,
    string Title,
    string? Message,
    bool RequirePasscode,
    bool Unlocked,
    bool IsExpired,
    bool IsRevoked,
    bool IsLimitReached,
    bool IsLocked,
    DateTime? LockedUntilUtc,
    DateTime ExpiresAtUtc,
    int? MaxDownloads,
    int DownloadCount,
    IReadOnlyList<ManagedFileSummaryDto> Files);

public sealed record PublicUploadInfoDto(
    string ShortCode,
    string Title,
    string? Description,
    bool RequirePasscode,
    bool Unlocked,
    bool IsExpired,
    bool IsRevoked,
    bool IsQuotaReached,
    bool IsLocked,
    DateTime? LockedUntilUtc,
    DateTime ExpiresAtUtc,
    bool AllowMultiple,
    int MaxFiles,
    int UploadedFilesCount,
    int RemainingFiles,
    long MaxFileSizeBytes,
    long MaxTotalSizeBytes,
    long UploadedTotalBytes,
    long RemainingBytes,
    string[] AllowedExtensions,
    string[] AllowedMimeTypes);

public sealed record VerifyPasscodeRequest(string Passcode);

public sealed record VerifyPasscodeResponse(bool Valid, string? SessionToken, DateTime? ExpiresAtUtc, string? Error);

public sealed record InitiatePublicUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes);

public sealed record InitiatePublicUploadResponse(
    Guid FileId,
    string ObjectKey,
    string Mode, // "single" | "multipart"
    string? UploadUrl,
    string? UploadId,
    long PartSizeBytes,
    int PartUrlTtlMinutes);

public sealed record CompletePublicUploadRequest(
    Guid FileId,
    string ObjectKey,
    string FileName,
    string ContentType,
    long SizeBytes,
    string? UploadId = null,
    IReadOnlyList<MultipartUploadPartStateDto>? Parts = null,
    string? Sha256 = null);

public sealed record PresignedDownloadResponse(
    string DownloadUrl,
    string FileName,
    long SizeBytes,
    string? Sha256,
    int ExpiresInSeconds);

public sealed record FileAccessLogDto(
    Guid Id,
    string EventType,
    Guid? ManagedFileId,
    Guid? ShareLinkId,
    Guid? UploadLinkId,
    Guid? AgentId,
    string? ClientIp,
    string? UserAgent,
    string? DetailsJson,
    DateTime CreatedAt);
