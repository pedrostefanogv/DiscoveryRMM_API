using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

public interface IFileLinkService
{
    // ── Admin: Share Links (Download) ──
    Task<IReadOnlyList<FileShareLinkDto>> ListShareLinksAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        CancellationToken ct = default);

    Task<FileShareLinkDto> CreateShareLinkAsync(
        CreateShareLinkRequest request,
        string? actor,
        CancellationToken ct = default);

    Task RevokeShareLinkAsync(Guid id, string? actor, CancellationToken ct = default);

    Task<(Guid? ClientId, Guid? SiteId)?> GetShareLinkScopeAsync(Guid id, CancellationToken ct = default);

    // ── Admin: Upload Links (File Drop) ──
    Task<IReadOnlyList<FileUploadLinkDto>> ListUploadLinksAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        Guid? ticketId = null,
        CancellationToken ct = default);

    Task<FileUploadLinkDto> CreateUploadLinkAsync(
        CreateUploadLinkRequest request,
        string? actor,
        CancellationToken ct = default);

    Task RevokeUploadLinkAsync(Guid id, string? actor, CancellationToken ct = default);

    Task<(Guid? ClientId, Guid? SiteId)?> GetUploadLinkScopeAsync(Guid id, CancellationToken ct = default);

    // ── Public: Share Links ──
    Task<PublicShareInfoDto> GetPublicShareInfoAsync(
        string shortCode,
        string? sessionToken,
        CancellationToken ct = default);

    Task<VerifyPasscodeResponse> VerifySharePasscodeAsync(
        string shortCode,
        string passcode,
        string? ip,
        string? userAgent,
        CancellationToken ct = default);

    Task<PresignedDownloadResponse> DownloadShareFileAsync(
        string shortCode,
        Guid fileId,
        string? sessionToken,
        string? ip,
        string? userAgent,
        CancellationToken ct = default);

    // ── Public: Upload Links (File Drop) ──
    Task<PublicUploadInfoDto> GetPublicUploadInfoAsync(
        string shortCode,
        string? sessionToken,
        CancellationToken ct = default);

    Task<VerifyPasscodeResponse> VerifyUploadPasscodeAsync(
        string shortCode,
        string passcode,
        string? ip,
        string? userAgent,
        CancellationToken ct = default);

    Task<InitiatePublicUploadResponse> InitiatePublicUploadAsync(
        string shortCode,
        InitiatePublicUploadRequest request,
        string? sessionToken,
        CancellationToken ct = default);

    Task<MultipartUploadPartUrlDto> GetPublicUploadPartUrlAsync(
        string shortCode,
        string objectKey,
        string uploadId,
        int partNumber,
        string? sessionToken,
        CancellationToken ct = default);

    Task<ManagedFileSummaryDto> CompletePublicUploadAsync(
        string shortCode,
        CompletePublicUploadRequest request,
        string? sessionToken,
        string? ip,
        string? userAgent,
        CancellationToken ct = default);

    Task AbortPublicUploadAsync(
        string shortCode,
        string objectKey,
        string uploadId,
        string? sessionToken,
        CancellationToken ct = default);
}
