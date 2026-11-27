using System.Security.Cryptography;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public sealed class ManagedFileService(
    DiscoveryDbContext db,
    IObjectStorageProviderFactory storageFactory,
    ILogger<ManagedFileService> logger) : IManagedFileService
{
    private const long MultipartThresholdBytes = 10L * 1024 * 1024; // 10 MiB
    private const long PartSizeBytes = 10L * 1024 * 1024;
    private const int UploadUrlTtlMinutes = 60;
    private static readonly TimeSpan DownloadUrlTtl = TimeSpan.FromMinutes(15);

    public async Task<PagedResult<ManagedFileDto>> ListAsync(
        ManagedFileFilter filter,
        IReadOnlyCollection<Guid>? allowedClientIds = null,
        IReadOnlyCollection<Guid>? allowedSiteIds = null,
        CancellationToken ct = default)
    {
        var query = db.ManagedFiles.AsNoTracking().Where(f => f.DeletedAt == null);

        if (!filter.IncludeInactive)
            query = query.Where(f => f.IsActive);

        if (filter.Scope.HasValue)
            query = query.Where(f => f.Scope == filter.Scope.Value);

        if (filter.ClientId.HasValue)
            query = query.Where(f => f.ClientId == filter.ClientId.Value);

        if (filter.SiteId.HasValue)
            query = query.Where(f => f.SiteId == filter.SiteId.Value);

        if (filter.Visibility.HasValue)
            query = query.Where(f => f.Visibility == filter.Visibility.Value);

        if (filter.Source.HasValue)
            query = query.Where(f => f.Source == filter.Source.Value);

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            var cat = filter.Category.Trim();
            query = query.Where(f => f.Category == cat);
        }

        if (!string.IsNullOrWhiteSpace(filter.FolderPath))
        {
            var folder = FileValidationHelper.NormalizeFolderPath(filter.FolderPath);
            query = query.Where(f => f.FolderPath == folder);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(f =>
                f.Name.ToLower().Contains(term)
                || f.FileName.ToLower().Contains(term)
                || (f.Description != null && f.Description.ToLower().Contains(term)));
        }

        // Recorte multi-tenant quando o operador não possui acesso global.
        if (allowedClientIds is not null || allowedSiteIds is not null)
        {
            var cIds = allowedClientIds?.ToHashSet() ?? [];
            var sIds = allowedSiteIds?.ToHashSet() ?? [];
            query = query.Where(f =>
                f.Scope == FileScope.Global
                || (f.ClientId.HasValue && cIds.Contains(f.ClientId.Value))
                || (f.SiteId.HasValue && sIds.Contains(f.SiteId.Value)));
        }

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(f => f.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = await MapDtosAsync(rows, ct);
        return new PagedResult<ManagedFileDto>(items, total, page, pageSize);
    }

    public async Task<ManagedFileDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.ManagedFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.DeletedAt == null, ct);

        if (entity is null)
            return null;

        var dtos = await MapDtosAsync([entity], ct);
        return dtos[0];
    }

    public async Task<FileHubStatsDto> GetStatsAsync(
        IReadOnlyCollection<Guid>? allowedClientIds = null,
        IReadOnlyCollection<Guid>? allowedSiteIds = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var filesQuery = db.ManagedFiles.AsNoTracking().Where(f => f.DeletedAt == null && f.IsActive);

        if (allowedClientIds is not null || allowedSiteIds is not null)
        {
            var cIds = allowedClientIds?.ToHashSet() ?? [];
            var sIds = allowedSiteIds?.ToHashSet() ?? [];
            filesQuery = filesQuery.Where(f =>
                f.Scope == FileScope.Global
                || (f.ClientId.HasValue && cIds.Contains(f.ClientId.Value))
                || (f.SiteId.HasValue && sIds.Contains(f.SiteId.Value)));
        }

        var files = await filesQuery
            .Select(f => new { f.Scope, f.SizeBytes, f.DownloadCount })
            .ToListAsync(ct);

        var activeShares = await db.FileShareLinks.AsNoTracking()
            .CountAsync(l => !l.IsRevoked && l.ExpiresAtUtc > now, ct);
        var activeUploads = await db.FileUploadLinks.AsNoTracking()
            .CountAsync(l => !l.IsRevoked && l.ExpiresAtUtc > now, ct);

        return new FileHubStatsDto(
            TotalFiles: files.Count,
            TotalBytes: files.Sum(x => x.SizeBytes),
            GlobalBytes: files.Where(x => x.Scope == FileScope.Global).Sum(x => x.SizeBytes),
            ClientBytes: files.Where(x => x.Scope == FileScope.Client).Sum(x => x.SizeBytes),
            SiteBytes: files.Where(x => x.Scope == FileScope.Site).Sum(x => x.SizeBytes),
            TotalDownloads: files.Sum(x => x.DownloadCount),
            ActiveShareLinks: activeShares,
            ActiveUploadLinks: activeUploads);
    }

    public async Task<BeginManagedFileUploadResponse> BeginUploadAsync(
        BeginManagedFileUploadRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        ValidateScope(request.Scope, request.ClientId, request.SiteId);
        if (request.SizeBytes <= 0)
            throw new InvalidOperationException("O tamanho do arquivo deve ser maior que zero.");

        var safeName = FileValidationHelper.SanitizeFileName(request.FileName);
        var contentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? "application/octet-stream"
            : request.ContentType.Trim();

        var fileId = IdGenerator.NewId();
        var objectKey = ComposeObjectKey(request.Scope, request.ClientId, request.SiteId, fileId, safeName);
        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);

        if (request.SizeBytes <= MultipartThresholdBytes)
        {
            var uploadUrl = await storage.GetPresignedUploadUrlAsync(
                objectKey, UploadUrlTtlMinutes, contentType, request.SizeBytes, ct);

            return new BeginManagedFileUploadResponse(
                FileId: fileId,
                ObjectKey: objectKey,
                Mode: "single",
                UploadUrl: uploadUrl,
                UploadId: null,
                PartSizeBytes: PartSizeBytes,
                PartUrlTtlMinutes: UploadUrlTtlMinutes);
        }

        var uploadId = await storage.CreateMultipartUploadAsync(objectKey, contentType, ct);
        return new BeginManagedFileUploadResponse(
            FileId: fileId,
            ObjectKey: objectKey,
            Mode: "multipart",
            UploadUrl: null,
            UploadId: uploadId,
            PartSizeBytes: PartSizeBytes,
            PartUrlTtlMinutes: UploadUrlTtlMinutes);
    }

    public async Task<MultipartUploadPartUrlDto> GetUploadPartUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || !objectKey.StartsWith("file-hub/", StringComparison.Ordinal))
            throw new InvalidOperationException("ObjectKey inválido para o File Hub.");
        if (partNumber is < 1 or > 10_000)
            throw new InvalidOperationException($"Número de parte inválido: {partNumber}.");

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        var url = await storage.GetPresignedUploadPartUrlAsync(
            objectKey, uploadId, partNumber, UploadUrlTtlMinutes, ct);

        return new MultipartUploadPartUrlDto(partNumber, url, UploadUrlTtlMinutes * 60);
    }

    public async Task<ManagedFileDto> CompleteUploadAsync(
        CompleteManagedFileUploadRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        ValidateScope(request.Scope, request.ClientId, request.SiteId);
        var safeName = FileValidationHelper.SanitizeFileName(request.FileName);
        var expectedPrefix = ComposeObjectKeyPrefix(request.Scope, request.ClientId, request.SiteId, request.FileId);
        if (string.IsNullOrWhiteSpace(request.ObjectKey)
            || !request.ObjectKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ObjectKey não corresponde ao arquivo/escopo informado.");
        }

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        if (!string.IsNullOrWhiteSpace(request.UploadId))
        {
            if (request.Parts is null || request.Parts.Count == 0)
                throw new InvalidOperationException("Partes do upload multipart são obrigatórias.");

            var orderedParts = request.Parts
                .OrderBy(p => p.PartNumber)
                .Select(p => new MultipartUploadPart(p.PartNumber, p.ETag, p.SizeBytes))
                .ToArray();

            try
            {
                await storage.CompleteMultipartUploadAsync(request.ObjectKey, request.UploadId, orderedParts, ct);
            }
            catch (Exception ex)
            {
                if (!await storage.ExistsAsync(request.ObjectKey, ct))
                    throw;
                logger.LogWarning(ex, "Complete multipart reapresentado para {ObjectKey}; objeto já existe.", request.ObjectKey);
            }
        }

        var metadata = await storage.GetMetadataAsync(request.ObjectKey, ct)
            ?? throw new InvalidOperationException("Arquivo ainda não encontrado no object storage.");

        var sha256 = NormalizeSha256(request.Sha256);
        if (string.IsNullOrWhiteSpace(sha256) && metadata.SizeBytes <= 250L * 1024 * 1024)
            sha256 = await TryComputeSha256Async(storage, request.ObjectKey, ct);

        var now = DateTime.UtcNow;
        var entity = new ManagedFile
        {
            Id = request.FileId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? safeName : request.Name.Trim(),
            FileName = safeName,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            FolderPath = FileValidationHelper.NormalizeFolderPath(request.FolderPath),
            TagsJson = JsonSerializer.Serialize(request.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToArray() ?? []),
            Scope = request.Scope,
            ClientId = request.Scope == FileScope.Global ? null : request.ClientId,
            SiteId = request.Scope == FileScope.Site ? request.SiteId : null,
            Visibility = request.Visibility,
            Source = FileSource.AdminUpload,
            StorageObjectKey = request.ObjectKey,
            StorageBucket = metadata.Bucket,
            ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? metadata.ContentType : request.ContentType.Trim(),
            SizeBytes = metadata.SizeBytes,
            Sha256 = sha256,
            IsActive = true,
            ExpiresAtUtc = request.ExpiresAtUtc?.ToUniversalTime(),
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.ManagedFiles.Add(entity);
        await db.SaveChangesAsync(ct);

        return (await MapDtosAsync([entity], ct))[0];
    }

    public async Task AbortUploadAsync(string objectKey, string uploadId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || !objectKey.StartsWith("file-hub/", StringComparison.Ordinal))
            throw new InvalidOperationException("ObjectKey inválido para o File Hub.");

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        await storage.AbortMultipartUploadAsync(objectKey, uploadId, ct);
    }

    public async Task<ManagedFileDto> UpdateAsync(Guid id, UpdateManagedFileRequest request, CancellationToken ct = default)
    {
        var entity = await db.ManagedFiles.FirstOrDefaultAsync(f => f.Id == id && f.DeletedAt == null, ct)
            ?? throw new FileNotFoundException("Arquivo não encontrado.");

        if (!string.IsNullOrWhiteSpace(request.Name))
            entity.Name = request.Name.Trim();
        if (request.Description is not null)
            entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (request.Category is not null)
            entity.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        if (request.FolderPath is not null)
            entity.FolderPath = FileValidationHelper.NormalizeFolderPath(request.FolderPath);
        if (request.Tags is not null)
            entity.TagsJson = JsonSerializer.Serialize(request.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToArray());
        if (request.Scope.HasValue)
        {
            var nextClient = request.ClientId ?? entity.ClientId;
            var nextSite = request.SiteId ?? entity.SiteId;
            ValidateScope(request.Scope.Value, nextClient, nextSite);
            entity.Scope = request.Scope.Value;
            entity.ClientId = request.Scope.Value == FileScope.Global ? null : nextClient;
            entity.SiteId = request.Scope.Value == FileScope.Site ? nextSite : null;
        }
        if (request.Visibility.HasValue)
            entity.Visibility = request.Visibility.Value;
        if (request.IsActive.HasValue)
            entity.IsActive = request.IsActive.Value;
        if (request.ClearExpiresAt)
            entity.ExpiresAtUtc = null;
        else if (request.ExpiresAtUtc.HasValue)
            entity.ExpiresAtUtc = request.ExpiresAtUtc.Value.ToUniversalTime();

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return (await MapDtosAsync([entity], ct))[0];
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.ManagedFiles.FirstOrDefaultAsync(f => f.Id == id && f.DeletedAt == null, ct)
            ?? throw new FileNotFoundException("Arquivo não encontrado.");

        try
        {
            var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
            await storage.DeleteAsync(entity.StorageObjectKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao remover objeto {ObjectKey} do S3 durante exclusão de ManagedFile {Id}.", entity.StorageObjectKey, id);
        }

        entity.DeletedAt = DateTime.UtcNow;
        entity.IsActive = false;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<PresignedDownloadResponse> GetAdminDownloadUrlAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.ManagedFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.DeletedAt == null, ct)
            ?? throw new FileNotFoundException("Arquivo não encontrado.");

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        var url = await storage.GetPresignedDownloadUrlAsync(
            entity.StorageObjectKey, DownloadUrlTtl, entity.FileName, null, ct);

        return new PresignedDownloadResponse(url, entity.FileName, entity.SizeBytes, entity.Sha256, (int)DownloadUrlTtl.TotalSeconds);
    }

    public async Task<IReadOnlyList<ManagedFileSummaryDto>> ListForAgentAsync(
        Guid clientId,
        Guid siteId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await db.ManagedFiles.AsNoTracking()
            .Where(f => f.DeletedAt == null
                        && f.IsActive
                        && (f.ExpiresAtUtc == null || f.ExpiresAtUtc > now)
                        && (f.Visibility == FileVisibility.Public || f.Visibility == FileVisibility.AgentOnly)
                        && (f.Scope == FileScope.Global
                            || (f.Scope == FileScope.Client && f.ClientId == clientId)
                            || (f.Scope == FileScope.Site && f.SiteId == siteId)))
            .OrderByDescending(f => f.Scope)
            .ThenBy(f => f.Name)
            .ToListAsync(ct);

        return rows.Select(ToSummary).ToArray();
    }

    public async Task<PresignedDownloadResponse> GetAgentDownloadUrlAsync(
        Guid id,
        Guid agentId,
        Guid clientId,
        Guid siteId,
        string? ip,
        string? userAgent,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = await db.ManagedFiles.FirstOrDefaultAsync(
            f => f.Id == id
                 && f.DeletedAt == null
                 && f.IsActive
                 && (f.ExpiresAtUtc == null || f.ExpiresAtUtc > now)
                 && (f.Visibility == FileVisibility.Public || f.Visibility == FileVisibility.AgentOnly)
                 && (f.Scope == FileScope.Global
                     || (f.Scope == FileScope.Client && f.ClientId == clientId)
                     || (f.Scope == FileScope.Site && f.SiteId == siteId)),
            ct)
            ?? throw new FileNotFoundException("Arquivo não encontrado ou fora do escopo do agent.");

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        var url = await storage.GetPresignedDownloadUrlAsync(
            entity.StorageObjectKey, DownloadUrlTtl, entity.FileName, null, ct);

        entity.DownloadCount++;
        db.FileAccessLogs.Add(new FileAccessLog
        {
            Id = IdGenerator.NewId(),
            EventType = "agent_download",
            ManagedFileId = entity.Id,
            AgentId = agentId,
            ClientIp = ip,
            UserAgent = userAgent,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        return new PresignedDownloadResponse(url, entity.FileName, entity.SizeBytes, entity.Sha256, (int)DownloadUrlTtl.TotalSeconds);
    }

    public async Task<(IReadOnlyList<ManagedFileSummaryDto> Files, IReadOnlyList<string> Categories)> ListPublicFilesAsync(
        string? search,
        string? category,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var baseQuery = db.ManagedFiles.AsNoTracking()
            .Where(f => f.DeletedAt == null
                        && f.IsActive
                        && f.Visibility == FileVisibility.Public
                        && (f.ExpiresAtUtc == null || f.ExpiresAtUtc > now));

        var categories = await baseQuery
            .Where(f => f.Category != null && f.Category != "")
            .Select(f => f.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = category.Trim();
            baseQuery = baseQuery.Where(f => f.Category == cat);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            baseQuery = baseQuery.Where(f =>
                f.Name.ToLower().Contains(term)
                || f.FileName.ToLower().Contains(term)
                || (f.Description != null && f.Description.ToLower().Contains(term)));
        }

        var rows = await baseQuery
            .OrderBy(f => f.Name)
            .Take(250)
            .ToListAsync(ct);

        return (rows.Select(ToSummary).ToArray(), categories);
    }

    public async Task<PresignedDownloadResponse> GetPublicFileDownloadUrlAsync(
        Guid id,
        string? ip,
        string? userAgent,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entity = await db.ManagedFiles.FirstOrDefaultAsync(
            f => f.Id == id
                 && f.DeletedAt == null
                 && f.IsActive
                 && f.Visibility == FileVisibility.Public
                 && (f.ExpiresAtUtc == null || f.ExpiresAtUtc > now),
            ct)
            ?? throw new FileNotFoundException("Arquivo público não encontrado ou expirado.");

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        var url = await storage.GetPresignedDownloadUrlAsync(
            entity.StorageObjectKey, DownloadUrlTtl, entity.FileName, null, ct);

        entity.DownloadCount++;
        db.FileAccessLogs.Add(new FileAccessLog
        {
            Id = IdGenerator.NewId(),
            EventType = "public_download",
            ManagedFileId = entity.Id,
            ClientIp = ip,
            UserAgent = userAgent,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        return new PresignedDownloadResponse(url, entity.FileName, entity.SizeBytes, entity.Sha256, (int)DownloadUrlTtl.TotalSeconds);
    }

    public async Task<IReadOnlyList<FileAccessLogDto>> ListAccessLogsAsync(
        string? eventType = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var query = db.FileAccessLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(l => l.EventType == eventType.Trim());

        var rows = await query
            .OrderByDescending(l => l.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

        return rows.Select(l => new FileAccessLogDto(
            l.Id, l.EventType, l.ManagedFileId, l.ShareLinkId, l.UploadLinkId,
            l.AgentId, l.ClientIp, l.UserAgent, l.DetailsJson, l.CreatedAt)).ToArray();
    }

    private async Task<IReadOnlyList<ManagedFileDto>> MapDtosAsync(IReadOnlyList<ManagedFile> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var clientIds = rows.Where(r => r.ClientId.HasValue).Select(r => r.ClientId!.Value).Distinct().ToArray();
        var siteIds = rows.Where(r => r.SiteId.HasValue).Select(r => r.SiteId!.Value).Distinct().ToArray();

        var clients = clientIds.Length > 0
            ? await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : new Dictionary<Guid, string>();
        var sites = siteIds.Length > 0
            ? await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct)
            : new Dictionary<Guid, string>();

        return rows.Select(r => new ManagedFileDto(
            Id: r.Id,
            Name: r.Name,
            FileName: r.FileName,
            Description: r.Description,
            Category: r.Category,
            FolderPath: r.FolderPath,
            Tags: ParseStringArray(r.TagsJson),
            Scope: r.Scope,
            ClientId: r.ClientId,
            ClientName: r.ClientId.HasValue && clients.TryGetValue(r.ClientId.Value, out var cn) ? cn : null,
            SiteId: r.SiteId,
            SiteName: r.SiteId.HasValue && sites.TryGetValue(r.SiteId.Value, out var sn) ? sn : null,
            Visibility: r.Visibility,
            Source: r.Source,
            SourceUploadLinkId: r.SourceUploadLinkId,
            ContentType: r.ContentType,
            SizeBytes: r.SizeBytes,
            Sha256: r.Sha256,
            DownloadCount: r.DownloadCount,
            IsActive: r.IsActive,
            ExpiresAtUtc: r.ExpiresAtUtc,
            CreatedBy: r.CreatedBy,
            CreatedAt: r.CreatedAt,
            UpdatedAt: r.UpdatedAt)).ToArray();
    }

    internal static ManagedFileSummaryDto ToSummary(ManagedFile f) => new(
        f.Id, f.Name, f.FileName, f.Description, f.Category, f.ContentType, f.SizeBytes, f.Sha256, f.Scope);

    private static void ValidateScope(FileScope scope, Guid? clientId, Guid? siteId)
    {
        if (scope == FileScope.Client && (!clientId.HasValue || clientId.Value == Guid.Empty))
            throw new InvalidOperationException("ClientId é obrigatório para arquivos de escopo Cliente.");
        if (scope == FileScope.Site && (!siteId.HasValue || siteId.Value == Guid.Empty || !clientId.HasValue || clientId.Value == Guid.Empty))
            throw new InvalidOperationException("ClientId e SiteId são obrigatórios para arquivos de escopo Site.");
    }

    private static string ComposeObjectKey(FileScope scope, Guid? clientId, Guid? siteId, Guid fileId, string safeFileName)
        => $"{ComposeObjectKeyPrefix(scope, clientId, siteId, fileId)}{safeFileName}";

    private static string ComposeObjectKeyPrefix(FileScope scope, Guid? clientId, Guid? siteId, Guid fileId)
    {
        var scopeSegment = scope switch
        {
            FileScope.Global => "global",
            FileScope.Client => $"client/{clientId!.Value:N}",
            FileScope.Site => $"site/{siteId!.Value:N}",
            _ => "global"
        };
        return $"file-hub/{scopeSegment}/{fileId:N}/";
    }

    private static string[] ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var clean = value.Trim().ToLowerInvariant();
        return clean.Length == 64 && clean.All(Uri.IsHexDigit) ? clean : null;
    }

    private async Task<string?> TryComputeSha256Async(IObjectStorageService storage, string objectKey, CancellationToken ct)
    {
        try
        {
            await using var stream = await ObjectStorageStream.OpenReadAsync(storage, objectKey, ct);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Não foi possível calcular SHA-256 automático para {ObjectKey}.", objectKey);
            return null;
        }
    }
}
