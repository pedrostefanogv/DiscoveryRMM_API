using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface IManagedFileService
{
    Task<PagedResult<ManagedFileDto>> ListAsync(
        ManagedFileFilter filter,
        IReadOnlyCollection<Guid>? allowedClientIds = null,
        IReadOnlyCollection<Guid>? allowedSiteIds = null,
        CancellationToken ct = default);

    Task<ManagedFileDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<FileHubStatsDto> GetStatsAsync(
        IReadOnlyCollection<Guid>? allowedClientIds = null,
        IReadOnlyCollection<Guid>? allowedSiteIds = null,
        CancellationToken ct = default);

    Task<BeginManagedFileUploadResponse> BeginUploadAsync(
        BeginManagedFileUploadRequest request,
        string? actor,
        CancellationToken ct = default);

    Task<MultipartUploadPartUrlDto> GetUploadPartUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        CancellationToken ct = default);

    Task<ManagedFileDto> CompleteUploadAsync(
        CompleteManagedFileUploadRequest request,
        string? actor,
        CancellationToken ct = default);

    Task AbortUploadAsync(string objectKey, string uploadId, CancellationToken ct = default);

    Task<ManagedFileDto> UpdateAsync(Guid id, UpdateManagedFileRequest request, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    Task<PresignedDownloadResponse> GetAdminDownloadUrlAsync(Guid id, CancellationToken ct = default);

    /// <summary>Lista arquivos visíveis ao Agent considerando herança Global → Client → Site.</summary>
    Task<IReadOnlyList<ManagedFileSummaryDto>> ListForAgentAsync(
        Guid clientId,
        Guid siteId,
        CancellationToken ct = default);

    Task<PresignedDownloadResponse> GetAgentDownloadUrlAsync(
        Guid id,
        Guid agentId,
        Guid clientId,
        Guid siteId,
        string? ip,
        string? userAgent,
        CancellationToken ct = default);

    /// <summary>Lista arquivos marcados como Public para o portal /download.</summary>
    Task<(IReadOnlyList<ManagedFileSummaryDto> Files, IReadOnlyList<string> Categories, IReadOnlyList<PublicFolderDto> Folders)> ListPublicFilesAsync(
        string? search,
        string? category,
        string? folder = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ManagedFileFolderDto>> ListFoldersAsync(
        string? parentPath = null,
        bool includeAll = false,
        FileScope? scope = null,
        Guid? clientId = null,
        Guid? siteId = null,
        FileVisibility? visibility = null,
        IReadOnlyCollection<Guid>? allowedClientIds = null,
        IReadOnlyCollection<Guid>? allowedSiteIds = null,
        CancellationToken ct = default);

    Task<ManagedFileFolderDto?> GetFolderByIdAsync(Guid id, CancellationToken ct = default);

    Task<ManagedFileFolderDto> CreateFolderAsync(CreateManagedFileFolderRequest request, string? actor, CancellationToken ct = default);

    Task<ManagedFileFolderDto> UpdateFolderAsync(Guid id, UpdateManagedFileFolderRequest request, CancellationToken ct = default);

    Task DeleteFolderAsync(Guid id, bool moveFilesToParent = true, CancellationToken ct = default);

    Task<PresignedDownloadResponse> GetPublicFileDownloadUrlAsync(
        Guid id,
        string? ip,
        string? userAgent,
        CancellationToken ct = default);

    Task<IReadOnlyList<FileAccessLogDto>> ListAccessLogsAsync(
        string? eventType = null,
        int limit = 100,
        CancellationToken ct = default);
}
