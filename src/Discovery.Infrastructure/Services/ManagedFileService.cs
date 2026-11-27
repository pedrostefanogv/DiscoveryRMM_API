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
            var folderLower = FileValidationHelper.NormalizeFolderPath(filter.FolderPath).ToLowerInvariant();
            query = query.Where(f => f.FolderPath.ToLower() == folderLower);
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
        var normalizedFolderPath = FileValidationHelper.NormalizeFolderPath(request.FolderPath);
        var effectiveClientId = request.Scope == FileScope.Global ? null : request.ClientId;
        var effectiveSiteId = request.Scope == FileScope.Site ? request.SiteId : null;
        var effectiveVisibility = request.Visibility;
        if (normalizedFolderPath != "/")
        {
            var inherited = await ResolveAncestorOrFolderVisibilityAsync(
                request.Scope, effectiveClientId, effectiveSiteId, normalizedFolderPath, ct);
            if (inherited.HasValue)
                effectiveVisibility = inherited.Value;
        }

        var entity = new ManagedFile
        {
            Id = request.FileId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? safeName : request.Name.Trim(),
            FileName = safeName,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            FolderPath = normalizedFolderPath,
            TagsJson = JsonSerializer.Serialize(request.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToArray() ?? []),
            Scope = request.Scope,
            ClientId = effectiveClientId,
            SiteId = effectiveSiteId,
            Visibility = effectiveVisibility,
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
        if (entity.FolderPath != "/")
        {
            var inherited = await ResolveAncestorOrFolderVisibilityAsync(
                entity.Scope, entity.ClientId, entity.SiteId, entity.FolderPath, ct);
            if (inherited.HasValue)
                entity.Visibility = inherited.Value;
        }
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

    public async Task<(IReadOnlyList<ManagedFileSummaryDto> Files, IReadOnlyList<string> Categories, IReadOnlyList<PublicFolderDto> Folders)> ListPublicFilesAsync(
        string? search,
        string? category,
        string? folder = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var baseQuery = db.ManagedFiles.AsNoTracking()
            .Where(f => f.DeletedAt == null
                        && f.IsActive
                        && f.Visibility == FileVisibility.Public
                        && (f.ExpiresAtUtc == null || f.ExpiresAtUtc > now));

        var allPublicFiles = await baseQuery
            .Select(f => new { f.FolderPath, f.Category })
            .ToListAsync(ct);

        var categories = allPublicFiles
            .Where(f => !string.IsNullOrWhiteSpace(f.Category))
            .Select(f => f.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToList();

        var explicitFolders = await db.ManagedFileFolders.AsNoTracking()
            .Where(f => f.Visibility == FileVisibility.Public)
            .ToListAsync(ct);

        var folderMap = new Dictionary<string, (string Name, string ParentPath, string? Description)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ef in explicitFolders)
        {
            var norm = FileValidationHelper.NormalizeFolderPath(ef.Path);
            if (norm == "/") continue;
            folderMap[norm] = (ef.Name, FileValidationHelper.GetParentFolderPath(norm), ef.Description);
            var anc = FileValidationHelper.GetParentFolderPath(norm);
            while (anc != "/")
            {
                if (!folderMap.ContainsKey(anc))
                {
                    var ancName = anc[(anc.LastIndexOf('/') + 1)..];
                    folderMap[anc] = (ancName, FileValidationHelper.GetParentFolderPath(anc), null);
                }
                anc = FileValidationHelper.GetParentFolderPath(anc);
            }
        }

        foreach (var pf in allPublicFiles)
        {
            var norm = FileValidationHelper.NormalizeFolderPath(pf.FolderPath);
            while (norm != "/")
            {
                if (!folderMap.ContainsKey(norm))
                {
                    var name = norm[(norm.LastIndexOf('/') + 1)..];
                    folderMap[norm] = (name, FileValidationHelper.GetParentFolderPath(norm), null);
                }
                norm = FileValidationHelper.GetParentFolderPath(norm);
            }
        }

        var publicFolders = folderMap
            .Select(kvp =>
            {
                var path = kvp.Key;
                var prefix = path + "/";
                var count = allPublicFiles.Count(f =>
                {
                    var fp = FileValidationHelper.NormalizeFolderPath(f.FolderPath);
                    return string.Equals(fp, path, StringComparison.OrdinalIgnoreCase)
                           || fp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                });
                return new PublicFolderDto(path, kvp.Value.Name, kvp.Value.ParentPath, kvp.Value.Description, count);
            })
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

        if (!string.IsNullOrWhiteSpace(folder) && string.IsNullOrWhiteSpace(search) && string.IsNullOrWhiteSpace(category))
        {
            var folderLower = FileValidationHelper.NormalizeFolderPath(folder).ToLowerInvariant();
            baseQuery = baseQuery.Where(f => f.FolderPath.ToLower() == folderLower);
        }

        var rows = await baseQuery
            .OrderBy(f => f.Name)
            .Take(250)
            .ToListAsync(ct);

        return (rows.Select(ToSummary).ToArray(), categories, publicFolders);
    }

    public async Task<IReadOnlyList<ManagedFileFolderDto>> ListFoldersAsync(
        string? parentPath = null,
        bool includeAll = false,
        FileScope? scope = null,
        Guid? clientId = null,
        Guid? siteId = null,
        FileVisibility? visibility = null,
        IReadOnlyCollection<Guid>? allowedClientIds = null,
        IReadOnlyCollection<Guid>? allowedSiteIds = null,
        CancellationToken ct = default)
    {
        var foldersQuery = db.ManagedFileFolders.AsNoTracking();
        var filesQuery = db.ManagedFiles.AsNoTracking().Where(f => f.DeletedAt == null && f.IsActive);

        if (scope.HasValue)
        {
            foldersQuery = foldersQuery.Where(f => f.Scope == scope.Value);
            filesQuery = filesQuery.Where(f => f.Scope == scope.Value);
        }

        if (clientId.HasValue)
        {
            foldersQuery = foldersQuery.Where(f => f.ClientId == clientId.Value);
            filesQuery = filesQuery.Where(f => f.ClientId == clientId.Value);
        }

        if (siteId.HasValue)
        {
            foldersQuery = foldersQuery.Where(f => f.SiteId == siteId.Value);
            filesQuery = filesQuery.Where(f => f.SiteId == siteId.Value);
        }

        if (visibility.HasValue)
        {
            foldersQuery = foldersQuery.Where(f => f.Visibility == visibility.Value);
            filesQuery = filesQuery.Where(f => f.Visibility == visibility.Value);
        }

        if (allowedClientIds is not null || allowedSiteIds is not null)
        {
            var cIds = allowedClientIds?.ToHashSet() ?? [];
            var sIds = allowedSiteIds?.ToHashSet() ?? [];
            foldersQuery = foldersQuery.Where(f =>
                f.Scope == FileScope.Global
                || (f.ClientId.HasValue && cIds.Contains(f.ClientId.Value))
                || (f.SiteId.HasValue && sIds.Contains(f.SiteId.Value)));
            filesQuery = filesQuery.Where(f =>
                f.Scope == FileScope.Global
                || (f.ClientId.HasValue && cIds.Contains(f.ClientId.Value))
                || (f.SiteId.HasValue && sIds.Contains(f.SiteId.Value)));
        }

        var explicitFolders = await foldersQuery.ToListAsync(ct);
        var fileRows = await filesQuery
            .Select(f => new { f.FolderPath, f.Scope, f.ClientId, f.SiteId, f.Visibility, f.CreatedAt })
            .ToListAsync(ct);

        var combined = new Dictionary<(FileScope Scope, Guid? ClientId, Guid? SiteId, string PathLower), ManagedFileFolder>();
        foreach (var ef in explicitFolders)
        {
            var norm = FileValidationHelper.NormalizeFolderPath(ef.Path);
            if (norm == "/") continue;
            combined[(ef.Scope, ef.ClientId, ef.SiteId, norm.ToLowerInvariant())] = ef;
        }

        foreach (var fr in fileRows)
        {
            var norm = FileValidationHelper.NormalizeFolderPath(fr.FolderPath);
            while (norm != "/")
            {
                var key = (fr.Scope, fr.ClientId, fr.SiteId, norm.ToLowerInvariant());
                if (!combined.ContainsKey(key))
                {
                    var name = norm[(norm.LastIndexOf('/') + 1)..];
                    combined[key] = new ManagedFileFolder
                    {
                        Id = Guid.Empty,
                        Name = name,
                        Path = norm,
                        ParentPath = FileValidationHelper.GetParentFolderPath(norm),
                        Scope = fr.Scope,
                        ClientId = fr.ClientId,
                        SiteId = fr.SiteId,
                        Visibility = fr.Visibility,
                        CreatedAt = fr.CreatedAt,
                        UpdatedAt = fr.CreatedAt
                    };
                }
                norm = FileValidationHelper.GetParentFolderPath(norm);
            }
        }

        var allEntries = combined.Values.ToList();
        var clientIds = allEntries.Where(x => x.ClientId.HasValue).Select(x => x.ClientId!.Value).Distinct().ToArray();
        var siteIds = allEntries.Where(x => x.SiteId.HasValue).Select(x => x.SiteId!.Value).Distinct().ToArray();

        var clients = clientIds.Length > 0
            ? await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : new Dictionary<Guid, string>();
        var sites = siteIds.Length > 0
            ? await db.Sites.AsNoTracking().Where(s => siteIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct)
            : new Dictionary<Guid, string>();

        var targetParent = FileValidationHelper.NormalizeFolderPath(parentPath);
        IEnumerable<ManagedFileFolder> filtered = allEntries;
        if (!includeAll)
        {
            filtered = filtered.Where(f => string.Equals(
                FileValidationHelper.NormalizeFolderPath(f.ParentPath),
                targetParent,
                StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                var normPath = FileValidationHelper.NormalizeFolderPath(f.Path);
                var prefix = normPath + "/";
                var fileCount = fileRows.Count(fr =>
                {
                    if (fr.Scope != f.Scope || fr.ClientId != f.ClientId || fr.SiteId != f.SiteId)
                        return false;
                    var fp = FileValidationHelper.NormalizeFolderPath(fr.FolderPath);
                    return string.Equals(fp, normPath, StringComparison.OrdinalIgnoreCase)
                           || fp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                });
                var subfolderCount = allEntries.Count(sf =>
                    sf.Scope == f.Scope
                    && sf.ClientId == f.ClientId
                    && sf.SiteId == f.SiteId
                    && string.Equals(FileValidationHelper.NormalizeFolderPath(sf.ParentPath), normPath, StringComparison.OrdinalIgnoreCase));

                return new ManagedFileFolderDto(
                    Id: f.Id,
                    Name: f.Name,
                    Path: normPath,
                    ParentPath: FileValidationHelper.NormalizeFolderPath(f.ParentPath),
                    Description: f.Description,
                    Scope: f.Scope,
                    ClientId: f.ClientId,
                    ClientName: f.ClientId.HasValue && clients.TryGetValue(f.ClientId.Value, out var cn) ? cn : null,
                    SiteId: f.SiteId,
                    SiteName: f.SiteId.HasValue && sites.TryGetValue(f.SiteId.Value, out var sn) ? sn : null,
                    Visibility: f.Visibility,
                    FileCount: fileCount,
                    SubfolderCount: subfolderCount,
                    IsVirtual: f.Id == Guid.Empty,
                    CreatedBy: f.CreatedBy,
                    CreatedAt: f.CreatedAt);
            })
            .ToArray();
    }

    public async Task<ManagedFileFolderDto?> GetFolderByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.ManagedFileFolders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id, ct);

        if (entity is null)
            return null;

        return await MapFolderDtoAsync(entity, ct);
    }

    public async Task<ManagedFileFolderDto> CreateFolderAsync(
        CreateManagedFileFolderRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        ValidateScope(request.Scope, request.ClientId, request.SiteId);
        var safeName = FileValidationHelper.SanitizeFolderName(request.Name);
        var parentPath = FileValidationHelper.NormalizeFolderPath(request.ParentPath);
        var fullPath = FileValidationHelper.CombineFolderPath(parentPath, safeName);
        var effectiveClientId = request.Scope == FileScope.Global ? null : request.ClientId;
        var effectiveSiteId = request.Scope == FileScope.Site ? request.SiteId : null;

        var existingInScope = await db.ManagedFileFolders
            .Where(f => f.Scope == request.Scope && f.ClientId == effectiveClientId && f.SiteId == effectiveSiteId)
            .ToListAsync(ct);

        if (existingInScope.Any(f => string.Equals(f.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Já existe uma pasta com este nome neste local.");

        var effectiveVisibility = request.Visibility;
        if (parentPath != "/")
        {
            var inherited = await ResolveAncestorOrFolderVisibilityAsync(
                request.Scope, effectiveClientId, effectiveSiteId, parentPath, ct);
            if (inherited.HasValue)
                effectiveVisibility = inherited.Value;
        }

        var now = DateTime.UtcNow;

        // Garante que todos os ancestrais intermediários existam no mesmo escopo/tenant.
        if (parentPath != "/")
        {
            var segments = parentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var running = "";
            foreach (var seg in segments)
            {
                var prev = running == "" ? "/" : running;
                running += "/" + seg;
                if (!existingInScope.Any(f => string.Equals(f.Path, running, StringComparison.OrdinalIgnoreCase)))
                {
                    var ancestor = new ManagedFileFolder
                    {
                        Id = IdGenerator.NewId(),
                        Name = seg,
                        Path = running,
                        ParentPath = prev,
                        Scope = request.Scope,
                        ClientId = effectiveClientId,
                        SiteId = effectiveSiteId,
                        Visibility = effectiveVisibility,
                        CreatedBy = actor,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    db.ManagedFileFolders.Add(ancestor);
                    existingInScope.Add(ancestor);
                }
            }
        }

        var entity = new ManagedFileFolder
        {
            Id = IdGenerator.NewId(),
            Name = safeName,
            Path = fullPath,
            ParentPath = parentPath,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Scope = request.Scope,
            ClientId = effectiveClientId,
            SiteId = effectiveSiteId,
            Visibility = effectiveVisibility,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.ManagedFileFolders.Add(entity);
        await db.SaveChangesAsync(ct);

        return await MapFolderDtoAsync(entity, ct);
    }

    public async Task<ManagedFileFolderDto> UpdateFolderAsync(
        Guid id,
        UpdateManagedFileFolderRequest request,
        CancellationToken ct = default)
    {
        var entity = await db.ManagedFileFolders.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new FileNotFoundException("Pasta não encontrada.");

        var oldPath = FileValidationHelper.NormalizeFolderPath(entity.Path);
        var now = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var safeName = FileValidationHelper.SanitizeFolderName(request.Name);
            var newPath = FileValidationHelper.CombineFolderPath(entity.ParentPath, safeName);

            if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
            {
                var scopeFolders = await db.ManagedFileFolders
                    .Where(f => f.Scope == entity.Scope && f.ClientId == entity.ClientId && f.SiteId == entity.SiteId)
                    .ToListAsync(ct);

                if (scopeFolders.Any(f => f.Id != entity.Id && string.Equals(f.Path, newPath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Já existe uma pasta com este nome neste local.");

                var oldPrefix = oldPath + "/";
                foreach (var sub in scopeFolders.Where(f => f.Id != entity.Id && f.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    sub.Path = newPath + sub.Path[oldPath.Length..];
                    sub.ParentPath = FileValidationHelper.GetParentFolderPath(sub.Path);
                    sub.UpdatedAt = now;
                }

                var scopeFiles = await db.ManagedFiles
                    .Where(f => f.DeletedAt == null
                                && f.Scope == entity.Scope
                                && f.ClientId == entity.ClientId
                                && f.SiteId == entity.SiteId)
                    .ToListAsync(ct);

                foreach (var file in scopeFiles)
                {
                    var fp = FileValidationHelper.NormalizeFolderPath(file.FolderPath);
                    if (string.Equals(fp, oldPath, StringComparison.OrdinalIgnoreCase))
                    {
                        file.FolderPath = newPath;
                        file.UpdatedAt = now;
                    }
                    else if (fp.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        file.FolderPath = newPath + fp[oldPath.Length..];
                        file.UpdatedAt = now;
                    }
                }
            }

            entity.Name = safeName;
            entity.Path = newPath;
        }

        if (request.Description is not null)
            entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        if (request.Visibility.HasValue && request.Visibility.Value != entity.Visibility)
        {
            if (FileValidationHelper.NormalizeFolderPath(entity.ParentPath) != "/")
            {
                throw new InvalidOperationException(
                    "A visibilidade de subpastas é herdada da pasta raiz. Altere a visibilidade na pasta raiz para aplicá-la a toda a árvore.");
            }

            var currentPath = FileValidationHelper.NormalizeFolderPath(entity.Path);
            var currentPrefix = currentPath + "/";
            entity.Visibility = request.Visibility.Value;

            var childFolders = await db.ManagedFileFolders
                .Where(f => f.Id != entity.Id
                            && f.Scope == entity.Scope
                            && f.ClientId == entity.ClientId
                            && f.SiteId == entity.SiteId)
                .ToListAsync(ct);

            foreach (var sub in childFolders.Where(f =>
                         FileValidationHelper.NormalizeFolderPath(f.Path).StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                sub.Visibility = request.Visibility.Value;
                sub.UpdatedAt = now;
            }

            var childFiles = await db.ManagedFiles
                .Where(f => f.DeletedAt == null
                            && f.Scope == entity.Scope
                            && f.ClientId == entity.ClientId
                            && f.SiteId == entity.SiteId)
                .ToListAsync(ct);

            foreach (var file in childFiles)
            {
                var fp = FileValidationHelper.NormalizeFolderPath(file.FolderPath);
                if (string.Equals(fp, currentPath, StringComparison.OrdinalIgnoreCase)
                    || fp.StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    file.Visibility = request.Visibility.Value;
                    file.UpdatedAt = now;
                }
            }
        }

        entity.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return await MapFolderDtoAsync(entity, ct);
    }

    private async Task<FileVisibility?> ResolveAncestorOrFolderVisibilityAsync(
        FileScope scope,
        Guid? clientId,
        Guid? siteId,
        string folderPath,
        CancellationToken ct)
    {
        var norm = FileValidationHelper.NormalizeFolderPath(folderPath);
        if (norm == "/")
            return null;

        var folders = await db.ManagedFileFolders.AsNoTracking()
            .Where(f => f.Scope == scope && f.ClientId == clientId && f.SiteId == siteId)
            .Select(f => new { f.Path, f.Visibility })
            .ToListAsync(ct);

        while (norm != "/")
        {
            var match = folders.FirstOrDefault(f =>
                string.Equals(FileValidationHelper.NormalizeFolderPath(f.Path), norm, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.Visibility;
            norm = FileValidationHelper.GetParentFolderPath(norm);
        }

        return null;
    }

    public async Task DeleteFolderAsync(Guid id, bool moveFilesToParent = true, CancellationToken ct = default)
    {
        var entity = await db.ManagedFileFolders.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new FileNotFoundException("Pasta não encontrada.");

        var oldPath = FileValidationHelper.NormalizeFolderPath(entity.Path);
        var parentPath = FileValidationHelper.NormalizeFolderPath(entity.ParentPath);
        var oldPrefix = oldPath + "/";
        var now = DateTime.UtcNow;

        var scopeFolders = await db.ManagedFileFolders
            .Where(f => f.Scope == entity.Scope && f.ClientId == entity.ClientId && f.SiteId == entity.SiteId)
            .ToListAsync(ct);

        var subfolders = scopeFolders
            .Where(f => f.Id != entity.Id && f.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var scopeFiles = await db.ManagedFiles
            .Where(f => f.DeletedAt == null
                        && f.IsActive
                        && f.Scope == entity.Scope
                        && f.ClientId == entity.ClientId
                        && f.SiteId == entity.SiteId)
            .ToListAsync(ct);

        var affectedFiles = scopeFiles
            .Where(f =>
            {
                var fp = FileValidationHelper.NormalizeFolderPath(f.FolderPath);
                return string.Equals(fp, oldPath, StringComparison.OrdinalIgnoreCase)
                       || fp.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (!moveFilesToParent && (subfolders.Count > 0 || affectedFiles.Count > 0))
            throw new InvalidOperationException("A pasta não está vazia. Remova ou mova os itens antes de excluir.");

        var existingFoldersByPath = scopeFolders
            .Where(f => f.Id != entity.Id && !subfolders.Contains(f))
            .GroupBy(f => FileValidationHelper.NormalizeFolderPath(f.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Visibility, StringComparer.OrdinalIgnoreCase);

        foreach (var sub in subfolders)
        {
            var suffix = sub.Path[oldPath.Length..];
            var nextPath = FileValidationHelper.NormalizeFolderPath(parentPath == "/" ? suffix : parentPath + suffix);
            if (existingFoldersByPath.ContainsKey(nextPath))
            {
                db.ManagedFileFolders.Remove(sub);
            }
            else
            {
                sub.Path = nextPath;
                sub.ParentPath = FileValidationHelper.GetParentFolderPath(nextPath);
                sub.UpdatedAt = now;
                existingFoldersByPath[nextPath] = sub.Visibility;
            }
        }

        foreach (var file in affectedFiles)
        {
            var fp = FileValidationHelper.NormalizeFolderPath(file.FolderPath);
            if (string.Equals(fp, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                file.FolderPath = parentPath;
            }
            else
            {
                var suffix = fp[oldPath.Length..]; // starts with '/'
                file.FolderPath = FileValidationHelper.NormalizeFolderPath(parentPath == "/" ? suffix : parentPath + suffix);
            }

            var probe = file.FolderPath;
            while (probe != "/")
            {
                if (existingFoldersByPath.TryGetValue(probe, out var inheritedVis))
                {
                    file.Visibility = inheritedVis;
                    break;
                }
                probe = FileValidationHelper.GetParentFolderPath(probe);
            }

            file.UpdatedAt = now;
        }

        db.ManagedFileFolders.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    private async Task<ManagedFileFolderDto> MapFolderDtoAsync(ManagedFileFolder f, CancellationToken ct)
    {
        var clientName = f.ClientId.HasValue
            ? await db.Clients.AsNoTracking().Where(c => c.Id == f.ClientId.Value).Select(c => c.Name).FirstOrDefaultAsync(ct)
            : null;
        var siteName = f.SiteId.HasValue
            ? await db.Sites.AsNoTracking().Where(s => s.Id == f.SiteId.Value).Select(s => s.Name).FirstOrDefaultAsync(ct)
            : null;

        var normPath = FileValidationHelper.NormalizeFolderPath(f.Path);
        var prefix = normPath + "/";

        var filePaths = await db.ManagedFiles.AsNoTracking()
            .Where(x => x.DeletedAt == null && x.IsActive && x.Scope == f.Scope && x.ClientId == f.ClientId && x.SiteId == f.SiteId)
            .Select(x => x.FolderPath)
            .ToListAsync(ct);

        var fileCount = filePaths.Count(fp =>
        {
            var normFp = FileValidationHelper.NormalizeFolderPath(fp);
            return string.Equals(normFp, normPath, StringComparison.OrdinalIgnoreCase)
                   || normFp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        });

        var subCount = await db.ManagedFileFolders.AsNoTracking()
            .CountAsync(sf => sf.Scope == f.Scope && sf.ClientId == f.ClientId && sf.SiteId == f.SiteId && sf.ParentPath == normPath, ct);

        return new ManagedFileFolderDto(
            Id: f.Id,
            Name: f.Name,
            Path: normPath,
            ParentPath: FileValidationHelper.NormalizeFolderPath(f.ParentPath),
            Description: f.Description,
            Scope: f.Scope,
            ClientId: f.ClientId,
            ClientName: clientName,
            SiteId: f.SiteId,
            SiteName: siteName,
            Visibility: f.Visibility,
            FileCount: fileCount,
            SubfolderCount: subCount,
            IsVirtual: f.Id == Guid.Empty,
            CreatedBy: f.CreatedBy,
            CreatedAt: f.CreatedAt);
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
        f.Id, f.Name, f.FileName, f.Description, f.Category, f.ContentType, f.SizeBytes, f.Sha256, f.Scope, f.FolderPath);

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
