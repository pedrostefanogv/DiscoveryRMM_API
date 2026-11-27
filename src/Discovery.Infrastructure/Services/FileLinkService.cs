using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public sealed class FileLinkService(
    DiscoveryDbContext db,
    IObjectStorageProviderFactory storageFactory,
    IConfiguration configuration,
    ILogger<FileLinkService> logger,
    INotificationService? notificationService = null) : IFileLinkService
{
    private const int MaxFailedPasscodeAttempts = 10;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PasscodeSessionTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DownloadUrlTtl = TimeSpan.FromMinutes(15);
    private const long MultipartThresholdBytes = 10L * 1024 * 1024;
    private const long PartSizeBytes = 10L * 1024 * 1024;
    private const int UploadUrlTtlMinutes = 60;

    // ── Admin: Share Links ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<FileShareLinkDto>> ListShareLinksAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        CancellationToken ct = default)
    {
        var query = db.FileShareLinks
            .AsNoTracking()
            .Include(l => l.Items)
            .ThenInclude(i => i.ManagedFile)
            .AsQueryable();

        if (clientId.HasValue)
            query = query.Where(l => l.ClientId == clientId.Value);
        if (siteId.HasValue)
            query = query.Where(l => l.SiteId == siteId.Value);

        var rows = await query.OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync(ct);
        var now = DateTime.UtcNow;

        return rows.Select(l => new FileShareLinkDto(
            Id: l.Id,
            ShortCode: l.ShortCode,
            Title: l.Title,
            Message: l.Message,
            ClientId: l.ClientId,
            ClientName: null,
            SiteId: l.SiteId,
            SiteName: null,
            RequirePasscode: l.RequirePasscode,
            ExpiresAtUtc: l.ExpiresAtUtc,
            MaxDownloads: l.MaxDownloads,
            DownloadCount: l.DownloadCount,
            IsRevoked: l.IsRevoked,
            IsExpired: l.ExpiresAtUtc <= now,
            Files: l.Items
                .Where(i => i.ManagedFile is not null && i.ManagedFile.DeletedAt == null)
                .Select(i => ManagedFileService.ToSummary(i.ManagedFile!))
                .ToArray(),
            CreatedBy: l.CreatedBy,
            CreatedAt: l.CreatedAt)).ToArray();
    }

    public async Task<FileShareLinkDto> CreateShareLinkAsync(
        CreateShareLinkRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new InvalidOperationException("Título do link é obrigatório.");
        if (request.FileIds is null || request.FileIds.Count == 0)
            throw new InvalidOperationException("Selecione ao menos um arquivo.");
        if (request.ExpiresAtUtc.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("A data de validade deve ser futura.");
        if (request.RequirePasscode && string.IsNullOrWhiteSpace(request.Passcode))
            throw new InvalidOperationException("Informe a contra-senha quando a proteção por senha estiver habilitada.");

        var fileIds = request.FileIds.Distinct().ToArray();
        var files = await db.ManagedFiles
            .Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null && f.IsActive)
            .ToListAsync(ct);

        if (files.Count != fileIds.Length)
            throw new InvalidOperationException("Um ou mais arquivos selecionados não existem ou estão inativos.");

        var link = new FileShareLink
        {
            Id = IdGenerator.NewId(),
            ShortCode = GenerateShortCode(),
            Title = request.Title.Trim(),
            Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            RequirePasscode = request.RequirePasscode,
            PasscodeHash = request.RequirePasscode ? HashPasscode(request.Passcode!.Trim()) : null,
            ExpiresAtUtc = request.ExpiresAtUtc.ToUniversalTime(),
            MaxDownloads = request.MaxDownloads is > 0 ? request.MaxDownloads : null,
            CreatedBy = actor,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var file in files)
        {
            link.Items.Add(new FileShareLinkItem
            {
                ShareLinkId = link.Id,
                ManagedFileId = file.Id
            });
        }

        db.FileShareLinks.Add(link);
        await db.SaveChangesAsync(ct);

        return new FileShareLinkDto(
            link.Id, link.ShortCode, link.Title, link.Message, link.ClientId, null, link.SiteId, null,
            link.RequirePasscode, link.ExpiresAtUtc, link.MaxDownloads, link.DownloadCount,
            link.IsRevoked, IsExpired: false,
            files.Select(ManagedFileService.ToSummary).ToArray(),
            link.CreatedBy, link.CreatedAt);
    }

    public async Task RevokeShareLinkAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var link = await db.FileShareLinks.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new FileNotFoundException("Link de download não encontrado.");

        link.IsRevoked = true;
        link.RevokedAtUtc = DateTime.UtcNow;
        link.RevokedBy = actor;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(Guid? ClientId, Guid? SiteId)?> GetShareLinkScopeAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.FileShareLinks.AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => new { l.ClientId, l.SiteId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.ClientId, row.SiteId);
    }

    // ── Admin: Upload Links (File Drop) ──────────────────────────────────────

    public async Task<IReadOnlyList<FileUploadLinkDto>> ListUploadLinksAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        Guid? ticketId = null,
        CancellationToken ct = default)
    {
        var query = db.FileUploadLinks.AsNoTracking().AsQueryable();
        if (clientId.HasValue)
            query = query.Where(l => l.ClientId == clientId.Value);
        if (siteId.HasValue)
            query = query.Where(l => l.SiteId == siteId.Value);
        if (ticketId.HasValue)
            query = query.Where(l => l.TicketId == ticketId.Value);

        var rows = await query.OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync(ct);
        var now = DateTime.UtcNow;

        return rows.Select(l => new FileUploadLinkDto(
            l.Id, l.ShortCode, l.Title, l.Description, l.ClientId, null, l.SiteId, null,
            l.TargetFolderPath, l.RequirePasscode, l.ExpiresAtUtc, l.AllowMultiple,
            l.MaxFiles, l.UploadedFilesCount, l.MaxFileSizeBytes, l.MaxTotalSizeBytes,
            l.UploadedTotalBytes,
            ParseArray(l.AllowedExtensionsJson),
            ParseArray(l.AllowedMimeTypesJson),
            l.FileRetentionDays, l.NotifyOnUpload,
            l.IsRevoked, IsExpired: l.ExpiresAtUtc <= now,
            l.CreatedBy, l.CreatedAt, l.TicketId)).ToArray();
    }

    public async Task<FileUploadLinkDto> CreateUploadLinkAsync(
        CreateUploadLinkRequest request,
        string? actor,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new InvalidOperationException("Título do link de upload é obrigatório.");
        if (request.ExpiresAtUtc.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("A data de validade deve ser futura.");
        if (request.RequirePasscode && string.IsNullOrWhiteSpace(request.Passcode))
            throw new InvalidOperationException("Informe a contra-senha quando a proteção por senha estiver habilitada.");
        if (request.MaxFileSizeBytes <= 0 || request.MaxTotalSizeBytes <= 0)
            throw new InvalidOperationException("Os limites de tamanho devem ser maiores que zero.");

        var maxFiles = request.AllowMultiple ? Math.Clamp(request.MaxFiles, 1, 100) : 1;
        var maxTotal = Math.Max(request.MaxTotalSizeBytes, request.MaxFileSizeBytes);
        var extensions = FileValidationHelper.NormalizeExtensions(request.AllowedExtensions);
        var mimes = FileValidationHelper.NormalizeMimeTypes(request.AllowedMimeTypes);
        var targetFolderPath = FileValidationHelper.NormalizeFolderPath(
            string.IsNullOrWhiteSpace(request.TargetFolderPath) ? "/recebidos" : request.TargetFolderPath);

        if (targetFolderPath != "/")
        {
            var publicFolders = await db.ManagedFileFolders.AsNoTracking()
                .Where(f => f.Visibility == FileVisibility.Public
                            && (f.Scope == FileScope.Global
                                || (request.ClientId.HasValue && f.ClientId == request.ClientId)
                                || (request.SiteId.HasValue && f.SiteId == request.SiteId)))
                .Select(f => f.Path)
                .ToListAsync(ct);

            var isPublicTarget = publicFolders.Any(pub =>
            {
                var normPub = FileValidationHelper.NormalizeFolderPath(pub);
                return string.Equals(targetFolderPath, normPub, StringComparison.OrdinalIgnoreCase)
                       || targetFolderPath.StartsWith(normPub + "/", StringComparison.OrdinalIgnoreCase);
            });

            if (isPublicTarget)
                throw new InvalidOperationException("Não é permitido direcionar links de upload (File Drop) para pastas públicas. Escolha uma pasta privada (ex.: /recebidos).");
        }

        var link = new FileUploadLink
        {
            Id = IdGenerator.NewId(),
            ShortCode = GenerateShortCode(),
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            TicketId = request.TicketId,
            TargetFolderPath = targetFolderPath,
            RequirePasscode = request.RequirePasscode,
            PasscodeHash = request.RequirePasscode ? HashPasscode(request.Passcode!.Trim()) : null,
            ExpiresAtUtc = request.ExpiresAtUtc.ToUniversalTime(),
            AllowMultiple = request.AllowMultiple,
            MaxFiles = maxFiles,
            MaxFileSizeBytes = request.MaxFileSizeBytes,
            MaxTotalSizeBytes = maxTotal,
            AllowedExtensionsJson = JsonSerializer.Serialize(extensions),
            AllowedMimeTypesJson = JsonSerializer.Serialize(mimes),
            FileRetentionDays = request.FileRetentionDays is > 0 ? request.FileRetentionDays : null,
            NotifyOnUpload = request.NotifyOnUpload,
            CreatedBy = actor,
            CreatedAt = DateTime.UtcNow
        };

        db.FileUploadLinks.Add(link);
        await db.SaveChangesAsync(ct);

        return new FileUploadLinkDto(
            link.Id, link.ShortCode, link.Title, link.Description, link.ClientId, null, link.SiteId, null,
            link.TargetFolderPath, link.RequirePasscode, link.ExpiresAtUtc, link.AllowMultiple,
            link.MaxFiles, link.UploadedFilesCount, link.MaxFileSizeBytes, link.MaxTotalSizeBytes,
            link.UploadedTotalBytes, extensions, mimes, link.FileRetentionDays, link.NotifyOnUpload,
            link.IsRevoked, IsExpired: false, link.CreatedBy, link.CreatedAt, link.TicketId);
    }

    public async Task RevokeUploadLinkAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var link = await db.FileUploadLinks.FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new FileNotFoundException("Link de upload não encontrado.");

        link.IsRevoked = true;
        link.RevokedAtUtc = DateTime.UtcNow;
        link.RevokedBy = actor;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(Guid? ClientId, Guid? SiteId)?> GetUploadLinkScopeAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.FileUploadLinks.AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => new { l.ClientId, l.SiteId })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : (row.ClientId, row.SiteId);
    }

    // ── Public: Share Links ──────────────────────────────────────────────────

    public async Task<PublicShareInfoDto> GetPublicShareInfoAsync(
        string shortCode,
        string? sessionToken,
        CancellationToken ct = default)
    {
        var link = await db.FileShareLinks
            .AsNoTracking()
            .Include(l => l.Items)
            .ThenInclude(i => i.ManagedFile)
            .FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de compartilhamento não encontrado.");

        var now = DateTime.UtcNow;
        var isExpired = link.ExpiresAtUtc <= now;
        var isLimitReached = link.MaxDownloads.HasValue && link.DownloadCount >= link.MaxDownloads.Value;
        var isLocked = link.LockedUntilUtc.HasValue && link.LockedUntilUtc.Value > now;
        var unlocked = !link.RequirePasscode || ValidateSessionToken(sessionToken, "share", link.ShortCode);

        var files = unlocked && !link.IsRevoked && !isExpired && !isLimitReached
            ? link.Items
                .Where(i => i.ManagedFile is not null && i.ManagedFile.DeletedAt == null && i.ManagedFile.IsActive)
                .Select(i => ManagedFileService.ToSummary(i.ManagedFile!))
                .ToArray()
            : Array.Empty<ManagedFileSummaryDto>();

        return new PublicShareInfoDto(
            link.ShortCode, link.Title, link.Message, link.RequirePasscode, unlocked,
            isExpired, link.IsRevoked, isLimitReached, isLocked, link.LockedUntilUtc,
            link.ExpiresAtUtc, link.MaxDownloads, link.DownloadCount, files);
    }

    public async Task<VerifyPasscodeResponse> VerifySharePasscodeAsync(
        string shortCode,
        string passcode,
        string? ip,
        string? userAgent,
        CancellationToken ct = default)
    {
        var link = await db.FileShareLinks.FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de compartilhamento não encontrado.");

        var now = DateTime.UtcNow;
        if (link.IsRevoked || link.ExpiresAtUtc <= now)
            return new VerifyPasscodeResponse(false, null, null, "Este link expirou ou foi revogado.");

        if (link.LockedUntilUtc.HasValue && link.LockedUntilUtc.Value > now)
            return new VerifyPasscodeResponse(false, null, null, "Muitas tentativas inválidas. Tente novamente mais tarde.");

        if (!link.RequirePasscode)
        {
            var exp = now.Add(PasscodeSessionTtl);
            return new VerifyPasscodeResponse(true, CreateSessionToken("share", link.ShortCode, exp), exp, null);
        }

        if (!VerifyPasscodeHash(passcode, link.PasscodeHash))
        {
            link.FailedPasscodeAttempts++;
            if (link.FailedPasscodeAttempts >= MaxFailedPasscodeAttempts)
            {
                link.LockedUntilUtc = now.Add(LockoutDuration);
                link.FailedPasscodeAttempts = 0;
            }
            db.FileAccessLogs.Add(new FileAccessLog
            {
                Id = IdGenerator.NewId(),
                EventType = "share_passcode_fail",
                ShareLinkId = link.Id,
                ClientIp = ip,
                UserAgent = userAgent,
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            return new VerifyPasscodeResponse(false, null, null, "Contra-senha inválida.");
        }

        link.FailedPasscodeAttempts = 0;
        link.LockedUntilUtc = null;
        await db.SaveChangesAsync(ct);

        var expiresAt = now.Add(PasscodeSessionTtl);
        return new VerifyPasscodeResponse(true, CreateSessionToken("share", link.ShortCode, expiresAt), expiresAt, null);
    }

    public async Task<PresignedDownloadResponse> DownloadShareFileAsync(
        string shortCode,
        Guid fileId,
        string? sessionToken,
        string? ip,
        string? userAgent,
        CancellationToken ct = default)
    {
        var link = await db.FileShareLinks
            .Include(l => l.Items)
            .ThenInclude(i => i.ManagedFile)
            .FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de compartilhamento não encontrado.");

        var now = DateTime.UtcNow;
        if (link.IsRevoked)
            throw new InvalidOperationException("Este link foi revogado.");
        if (link.ExpiresAtUtc <= now)
            throw new InvalidOperationException("Este link expirou.");
        if (link.MaxDownloads.HasValue && link.DownloadCount >= link.MaxDownloads.Value)
            throw new InvalidOperationException("O limite máximo de downloads deste link foi atingido.");
        if (link.RequirePasscode && !ValidateSessionToken(sessionToken, "share", link.ShortCode))
            throw new UnauthorizedAccessException("Contra-senha necessária ou sessão expirada.");

        var item = link.Items.FirstOrDefault(i => i.ManagedFileId == fileId)
            ?? throw new FileNotFoundException("Arquivo não pertence a este link.");
        var file = item.ManagedFile;
        if (file is null || file.DeletedAt != null || !file.IsActive)
            throw new FileNotFoundException("Arquivo indisponível.");

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        var url = await storage.GetPresignedDownloadUrlAsync(
            file.StorageObjectKey, DownloadUrlTtl, file.FileName, null, ct);

        link.DownloadCount++;
        file.DownloadCount++;
        db.FileAccessLogs.Add(new FileAccessLog
        {
            Id = IdGenerator.NewId(),
            EventType = "share_download",
            ManagedFileId = file.Id,
            ShareLinkId = link.Id,
            ClientIp = ip,
            UserAgent = userAgent,
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        return new PresignedDownloadResponse(url, file.FileName, file.SizeBytes, file.Sha256, (int)DownloadUrlTtl.TotalSeconds);
    }

    // ── Public: Upload Links (File Drop) ─────────────────────────────────────

    public async Task<PublicUploadInfoDto> GetPublicUploadInfoAsync(
        string shortCode,
        string? sessionToken,
        CancellationToken ct = default)
    {
        var link = await db.FileUploadLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de upload não encontrado.");

        var now = DateTime.UtcNow;
        var isExpired = link.ExpiresAtUtc <= now;
        var remainingFiles = Math.Max(0, link.MaxFiles - link.UploadedFilesCount);
        var remainingBytes = Math.Max(0L, link.MaxTotalSizeBytes - link.UploadedTotalBytes);
        var isQuotaReached = remainingFiles <= 0 || remainingBytes <= 0;
        var isLocked = link.LockedUntilUtc.HasValue && link.LockedUntilUtc.Value > now;
        var unlocked = !link.RequirePasscode || ValidateSessionToken(sessionToken, "upload", link.ShortCode);

        return new PublicUploadInfoDto(
            link.ShortCode, link.Title, link.Description, link.RequirePasscode, unlocked,
            isExpired, link.IsRevoked, isQuotaReached, isLocked, link.LockedUntilUtc,
            link.ExpiresAtUtc, link.AllowMultiple, link.MaxFiles, link.UploadedFilesCount,
            remainingFiles, link.MaxFileSizeBytes, link.MaxTotalSizeBytes,
            link.UploadedTotalBytes, remainingBytes,
            ParseArray(link.AllowedExtensionsJson),
            ParseArray(link.AllowedMimeTypesJson));
    }

    public async Task<VerifyPasscodeResponse> VerifyUploadPasscodeAsync(
        string shortCode,
        string passcode,
        string? ip,
        string? userAgent,
        CancellationToken ct = default)
    {
        var link = await db.FileUploadLinks.FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de upload não encontrado.");

        var now = DateTime.UtcNow;
        if (link.IsRevoked || link.ExpiresAtUtc <= now)
            return new VerifyPasscodeResponse(false, null, null, "Este link expirou ou foi revogado.");

        if (link.LockedUntilUtc.HasValue && link.LockedUntilUtc.Value > now)
            return new VerifyPasscodeResponse(false, null, null, "Muitas tentativas inválidas. Tente novamente mais tarde.");

        if (!link.RequirePasscode)
        {
            var exp = now.Add(PasscodeSessionTtl);
            return new VerifyPasscodeResponse(true, CreateSessionToken("upload", link.ShortCode, exp), exp, null);
        }

        if (!VerifyPasscodeHash(passcode, link.PasscodeHash))
        {
            link.FailedPasscodeAttempts++;
            if (link.FailedPasscodeAttempts >= MaxFailedPasscodeAttempts)
            {
                link.LockedUntilUtc = now.Add(LockoutDuration);
                link.FailedPasscodeAttempts = 0;
            }
            db.FileAccessLogs.Add(new FileAccessLog
            {
                Id = IdGenerator.NewId(),
                EventType = "upload_passcode_fail",
                UploadLinkId = link.Id,
                ClientIp = ip,
                UserAgent = userAgent,
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            return new VerifyPasscodeResponse(false, null, null, "Contra-senha inválida.");
        }

        link.FailedPasscodeAttempts = 0;
        link.LockedUntilUtc = null;
        await db.SaveChangesAsync(ct);

        var expiresAt = now.Add(PasscodeSessionTtl);
        return new VerifyPasscodeResponse(true, CreateSessionToken("upload", link.ShortCode, expiresAt), expiresAt, null);
    }

    public async Task<InitiatePublicUploadResponse> InitiatePublicUploadAsync(
        string shortCode,
        InitiatePublicUploadRequest request,
        string? sessionToken,
        CancellationToken ct = default)
    {
        var link = await db.FileUploadLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de upload não encontrado.");

        ValidateUploadLinkAccess(link, sessionToken);

        if (request.SizeBytes <= 0)
            throw new InvalidOperationException("O tamanho do arquivo deve ser maior que zero.");
        if (link.UploadedFilesCount >= link.MaxFiles)
            throw new InvalidOperationException("A quantidade máxima de arquivos para este link foi atingida.");
        if (request.SizeBytes > link.MaxFileSizeBytes)
            throw new InvalidOperationException($"Arquivo excede o limite máximo permitido por arquivo ({link.MaxFileSizeBytes} bytes).");
        if (link.UploadedTotalBytes + request.SizeBytes > link.MaxTotalSizeBytes)
            throw new InvalidOperationException("O envio excede a cota total restante deste link.");

        var safeName = FileValidationHelper.SanitizeFileName(request.FileName);
        var contentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? "application/octet-stream"
            : request.ContentType.Trim();

        var allowedExts = ParseArray(link.AllowedExtensionsJson);
        var allowedMimes = ParseArray(link.AllowedMimeTypesJson);
        var (allowed, reason) = FileValidationHelper.ValidateFileType(safeName, contentType, allowedExts, allowedMimes);
        if (!allowed)
            throw new InvalidOperationException(reason!);

        var fileId = IdGenerator.NewId();
        var objectKey = $"client-uploads/{link.Id:N}/{fileId:N}/{safeName}";
        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);

        if (request.SizeBytes <= MultipartThresholdBytes)
        {
            var uploadUrl = await storage.GetPresignedUploadUrlAsync(
                objectKey, UploadUrlTtlMinutes, contentType, request.SizeBytes, ct);

            return new InitiatePublicUploadResponse(
                fileId, objectKey, "single", uploadUrl, null, PartSizeBytes, UploadUrlTtlMinutes);
        }

        var uploadId = await storage.CreateMultipartUploadAsync(objectKey, contentType, ct);
        return new InitiatePublicUploadResponse(
            fileId, objectKey, "multipart", null, uploadId, PartSizeBytes, UploadUrlTtlMinutes);
    }

    public async Task<MultipartUploadPartUrlDto> GetPublicUploadPartUrlAsync(
        string shortCode,
        string objectKey,
        string uploadId,
        int partNumber,
        string? sessionToken,
        CancellationToken ct = default)
    {
        var link = await db.FileUploadLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de upload não encontrado.");

        ValidateUploadLinkAccess(link, sessionToken);
        var expectedRoot = $"client-uploads/{link.Id:N}/";
        if (string.IsNullOrWhiteSpace(objectKey) || !objectKey.StartsWith(expectedRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("ObjectKey inválido para este link.");
        if (partNumber is < 1 or > 10_000)
            throw new InvalidOperationException($"Número de parte inválido: {partNumber}.");

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        var url = await storage.GetPresignedUploadPartUrlAsync(
            objectKey, uploadId, partNumber, UploadUrlTtlMinutes, ct);

        return new MultipartUploadPartUrlDto(partNumber, url, UploadUrlTtlMinutes * 60);
    }

    public async Task<ManagedFileSummaryDto> CompletePublicUploadAsync(
        string shortCode,
        CompletePublicUploadRequest request,
        string? sessionToken,
        string? ip,
        string? userAgent,
        CancellationToken ct = default)
    {
        var link = await db.FileUploadLinks.FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de upload não encontrado.");

        ValidateUploadLinkAccess(link, sessionToken);

        var safeName = FileValidationHelper.SanitizeFileName(request.FileName);
        var expectedPrefix = $"client-uploads/{link.Id:N}/{request.FileId:N}/";
        if (string.IsNullOrWhiteSpace(request.ObjectKey)
            || !request.ObjectKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ObjectKey não pertence a este link de upload.");
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
                logger.LogWarning(ex, "Complete multipart público reapresentado para {ObjectKey}.", request.ObjectKey);
            }
        }

        var metadata = await storage.GetMetadataAsync(request.ObjectKey, ct)
            ?? throw new InvalidOperationException("Arquivo ainda não encontrado no object storage.");

        // Segurança: valida o tamanho REAL no S3 após o upload. Se violar os limites,
        // apaga imediatamente do bucket e registra tentativa rejeitada.
        var actualSize = metadata.SizeBytes;
        if (actualSize <= 0
            || actualSize > link.MaxFileSizeBytes
            || link.UploadedFilesCount >= link.MaxFiles
            || link.UploadedTotalBytes + actualSize > link.MaxTotalSizeBytes)
        {
            try { await storage.DeleteAsync(request.ObjectKey, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Falha ao remover upload excedente {ObjectKey}.", request.ObjectKey); }

            db.FileAccessLogs.Add(new FileAccessLog
            {
                Id = IdGenerator.NewId(),
                EventType = "upload_rejected",
                UploadLinkId = link.Id,
                ClientIp = ip,
                UserAgent = userAgent,
                DetailsJson = JsonSerializer.Serialize(new { fileName = safeName, actualSize, maxFileSize = link.MaxFileSizeBytes }),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);

            throw new InvalidOperationException("Tamanho real do arquivo ou cota do link excedida. O arquivo foi descartado.");
        }

        var now = DateTime.UtcNow;
        var scope = link.SiteId.HasValue ? FileScope.Site
            : link.ClientId.HasValue ? FileScope.Client
            : FileScope.Global;

        var entity = new ManagedFile
        {
            Id = request.FileId,
            Name = safeName,
            FileName = safeName,
            Description = $"Enviado via link: {link.Title}",
            Category = "Recebidos",
            FolderPath = link.TargetFolderPath,
            TagsJson = "[\"client-upload\"]",
            Scope = scope,
            ClientId = link.ClientId,
            SiteId = link.SiteId,
            Visibility = FileVisibility.Private,
            Source = FileSource.ClientUploadLink,
            SourceUploadLinkId = link.Id,
            StorageObjectKey = request.ObjectKey,
            StorageBucket = metadata.Bucket,
            ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? metadata.ContentType : request.ContentType.Trim(),
            SizeBytes = actualSize,
            Sha256 = NormalizeSha256(request.Sha256),
            IsActive = true,
            ExpiresAtUtc = link.FileRetentionDays.HasValue ? now.AddDays(link.FileRetentionDays.Value) : null,
            CreatedBy = $"upload-link:{link.ShortCode}",
            CreatedAt = now,
            UpdatedAt = now
        };

        link.UploadedFilesCount++;
        link.UploadedTotalBytes += actualSize;

        db.ManagedFiles.Add(entity);
        if (link.TicketId.HasValue)
        {
            db.Attachments.Add(new Attachment
            {
                Id = IdGenerator.NewId(),
                EntityType = "Ticket",
                EntityId = link.TicketId.Value,
                ClientId = link.ClientId,
                FileName = safeName,
                Description = $"Enviado via link externo: {link.Title}",
                StorageObjectKey = request.ObjectKey,
                StorageBucket = metadata.Bucket,
                ContentType = entity.ContentType,
                SizeBytes = actualSize,
                StorageChecksum = entity.Sha256 ?? metadata.Checksum,
                StorageProviderType = (int)metadata.StorageProvider,
                UploadedBy = $"upload-link:{link.ShortCode}",
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        db.FileAccessLogs.Add(new FileAccessLog
        {
            Id = IdGenerator.NewId(),
            EventType = "upload_completed",
            ManagedFileId = entity.Id,
            UploadLinkId = link.Id,
            ClientIp = ip,
            UserAgent = userAgent,
            DetailsJson = JsonSerializer.Serialize(new { fileName = safeName, sizeBytes = actualSize }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(ct);

        if (link.NotifyOnUpload && notificationService is not null)
            await NotifyUploadCompletedAsync(link, entity, ip, ct);

        return ManagedFileService.ToSummary(entity);
    }

    public async Task AbortPublicUploadAsync(
        string shortCode,
        string objectKey,
        string uploadId,
        string? sessionToken,
        CancellationToken ct = default)
    {
        var link = await db.FileUploadLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ShortCode == shortCode, ct)
            ?? throw new FileNotFoundException("Link de upload não encontrado.");

        ValidateUploadLinkAccess(link, sessionToken);
        var expectedRoot = $"client-uploads/{link.Id:N}/";
        if (string.IsNullOrWhiteSpace(objectKey) || !objectKey.StartsWith(expectedRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("ObjectKey inválido para este link.");

        var storage = await storageFactory.CreateObjectStorageServiceAsync(ct);
        await storage.AbortMultipartUploadAsync(objectKey, uploadId, ct);
    }

    // ── Helpers internos ─────────────────────────────────────────────────────

    private void ValidateUploadLinkAccess(FileUploadLink link, string? sessionToken)
    {
        var now = DateTime.UtcNow;
        if (link.IsRevoked)
            throw new InvalidOperationException("Este link de upload foi revogado.");
        if (link.ExpiresAtUtc <= now)
            throw new InvalidOperationException("Este link de upload expirou.");
        if (link.RequirePasscode && !ValidateSessionToken(sessionToken, "upload", link.ShortCode))
            throw new UnauthorizedAccessException("Contra-senha necessária ou sessão expirada.");
    }

    private static string GenerateShortCode()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string HashPasscode(string passcode)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passcode),
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPasscodeHash(string passcode, string? stored)
    {
        if (string.IsNullOrWhiteSpace(passcode) || string.IsNullOrWhiteSpace(stored))
            return false;

        var parts = stored.Split(':');
        if (parts.Length != 3 || parts[0] != "v1")
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(passcode.Trim()),
                salt,
                100_000,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private string CreateSessionToken(string kind, string shortCode, DateTime expiresAtUtc)
    {
        var expUnix = new DateTimeOffset(expiresAtUtc).ToUnixTimeSeconds();
        var payload = $"{kind}|{shortCode}|{expUnix}";
        var sig = SignPayload(payload);
        var raw = $"{payload}|{sig}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private bool ValidateSessionToken(string? token, string expectedKind, string expectedShortCode)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = decoded.Split('|');
            if (parts.Length != 4)
                return false;

            if (!string.Equals(parts[0], expectedKind, StringComparison.Ordinal)
                || !string.Equals(parts[1], expectedShortCode, StringComparison.Ordinal))
            {
                return false;
            }

            if (!long.TryParse(parts[2], out var expUnix))
                return false;
            if (DateTimeOffset.FromUnixTimeSeconds(expUnix) <= DateTimeOffset.UtcNow)
                return false;

            var payload = $"{parts[0]}|{parts[1]}|{parts[2]}";
            var expectedSig = SignPayload(payload);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(parts[3]),
                Encoding.UTF8.GetBytes(expectedSig));
        }
        catch
        {
            return false;
        }
    }

    private string SignPayload(string payload)
    {
        var secret = configuration["Jwt:Secret"]
            ?? configuration["Jwt:Key"]
            ?? configuration["Security:ShareSessionSecret"]
            ?? "discovery-file-share-session-fallback-key";
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes("file-share-session:" + secret));
        var mac = HMACSHA256.HashData(keyBytes, Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(mac);
    }

    private static string[] ParseArray(string? json)
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

    private async Task NotifyUploadCompletedAsync(
        FileUploadLink link,
        ManagedFile file,
        string? ip,
        CancellationToken ct)
    {
        try
        {
            var recipientUserIds = new HashSet<Guid>();

            if (!string.IsNullOrWhiteSpace(link.CreatedBy))
            {
                var actorLower = link.CreatedBy.Trim().ToLowerInvariant();
                var creatorId = await db.Users
                    .AsNoTracking()
                    .Where(u => u.IsActive && (u.Login.ToLower() == actorLower || u.Email.ToLower() == actorLower))
                    .Select(u => (Guid?)u.Id)
                    .FirstOrDefaultAsync(ct);
                if (creatorId.HasValue)
                    recipientUserIds.Add(creatorId.Value);
            }

            string? ticketTitle = null;
            if (link.TicketId.HasValue)
            {
                var ticket = await db.Tickets
                    .AsNoTracking()
                    .Where(t => t.Id == link.TicketId.Value)
                    .Select(t => new { t.AssignedToUserId, t.Title })
                    .FirstOrDefaultAsync(ct);
                if (ticket is not null)
                {
                    ticketTitle = ticket.Title;
                    if (ticket.AssignedToUserId.HasValue)
                        recipientUserIds.Add(ticket.AssignedToUserId.Value);
                }
            }

            const string eventType = "file.upload.completed";
            var topic = link.TicketId.HasValue ? "tickets" : "files";
            var title = link.TicketId.HasValue
                ? $"Arquivo recebido no chamado: {file.FileName}"
                : $"Arquivo recebido via link: {link.Title}";

            var sizeLabel = FormatBytes(file.SizeBytes);
            var messageParts = new List<string>
            {
                $"O arquivo \"{file.FileName}\" ({sizeLabel}) foi enviado através do link \"{link.Title}\"."
            };
            if (!string.IsNullOrWhiteSpace(ticketTitle))
                messageParts.Add($"Chamado: {ticketTitle}.");
            if (!string.IsNullOrWhiteSpace(ip))
                messageParts.Add($"IP de origem: {ip}.");
            var message = string.Join(" ", messageParts);

            var payload = new
            {
                fileId = file.Id,
                fileName = file.FileName,
                sizeBytes = file.SizeBytes,
                uploadLinkId = link.Id,
                shortCode = link.ShortCode,
                linkTitle = link.Title,
                clientId = link.ClientId,
                siteId = link.SiteId,
                ticketId = link.TicketId,
                tab = link.TicketId.HasValue ? "attachments" : (string?)null
            };

            if (recipientUserIds.Count > 0)
            {
                foreach (var userId in recipientUserIds)
                {
                    await notificationService!.PublishAsync(
                        new NotificationPublishRequest(
                            EventType: eventType,
                            Topic: topic,
                            Title: title,
                            Message: message,
                            Severity: NotificationSeverity.Informational,
                            Payload: payload,
                            RecipientUserId: userId,
                            CreatedBy: $"upload-link:{link.ShortCode}"),
                        ct);
                }
            }
            else
            {
                await notificationService!.PublishAsync(
                    new NotificationPublishRequest(
                        EventType: eventType,
                        Topic: topic,
                        Title: title,
                        Message: message,
                        Severity: NotificationSeverity.Informational,
                        Payload: payload,
                        RecipientUserId: null,
                        RecipientKey: link.CreatedBy,
                        CreatedBy: $"upload-link:{link.ShortCode}"),
                    ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao enviar notificação de upload concluído para o link {UploadLinkId}.", link.Id);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F1} MB";
        return $"{mb / 1024.0:F2} GB";
    }
}
