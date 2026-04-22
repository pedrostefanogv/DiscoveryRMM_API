using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AppPackageRepository : IAppPackageRepository
{
    private readonly DiscoveryDbContext _db;

    public AppPackageRepository(DiscoveryDbContext db)
    {
        _db = db;
    }

    public async Task<AppPackage?> GetByInstallationTypeAndPackageIdAsync(
        AppInstallationType installationType,
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePackageId(packageId, installationType);
        return await _db.AppPackages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.InstallationType == installationType && x.PackageId == normalized,
                cancellationToken);
    }

    public async Task<(IReadOnlyList<AppPackage> Items, int TotalCount)> SearchAsync(
        AppInstallationType installationType,
        string? search,
        string? architecture,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var query = _db.AppPackages
            .AsNoTracking()
            .Where(x => x.InstallationType == installationType);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var pattern = $"%{term}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.PackageId, pattern) ||
                EF.Functions.ILike(x.Name, pattern) ||
                (x.Publisher != null && EF.Functions.ILike(x.Publisher, pattern)) ||
                (x.Description != null && EF.Functions.ILike(x.Description, pattern)));
        }

        if (installationType == AppInstallationType.Winget && !string.IsNullOrWhiteSpace(architecture))
        {
            // metadata_json is mapped as jsonb, so string functions like lower()/contains can fail in PostgreSQL.
            // For now, ignore architecture at SQL level to avoid breaking catalog queries.
            _ = architecture;
        }

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.PackageId)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<AppPackage>> GetAllByInstallationTypeAsync(
        AppInstallationType installationType,
        CancellationToken cancellationToken = default)
    {
        return await _db.AppPackages
            .AsNoTracking()
            .Where(x => x.InstallationType == installationType)
            .OrderBy(x => x.PackageId)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> BulkUpsertAsync(
        IReadOnlyList<AppPackage> packages,
        AppInstallationType installationType,
        CancellationToken cancellationToken = default)
    {
        if (packages.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var upserted = 0;

        foreach (var batch in packages.Chunk(200))
        {
            var normalizedIds = batch
                .Select(x => NormalizePackageId(x.PackageId, installationType))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existing = await _db.AppPackages
                .Where(x => x.InstallationType == installationType && normalizedIds.Contains(x.PackageId))
                .ToDictionaryAsync(x => x.PackageId, StringComparer.OrdinalIgnoreCase, cancellationToken);

            foreach (var incoming in batch)
            {
                var normalizedId = NormalizePackageId(incoming.PackageId, installationType);
                if (string.IsNullOrWhiteSpace(normalizedId))
                    continue;

                upserted++;
                if (existing.TryGetValue(normalizedId, out var current))
                {
                    current.Name = incoming.Name;
                    current.Publisher = incoming.Publisher;
                    current.Version = incoming.Version;
                    current.Description = incoming.Description;
                    current.IconUrl = incoming.IconUrl;
                    current.SiteUrl = incoming.SiteUrl;
                    current.InstallCommand = incoming.InstallCommand;
                    current.MetadataJson = incoming.MetadataJson;
                    current.FileObjectKey = incoming.FileObjectKey;
                    current.FileBucket = incoming.FileBucket;
                    current.FilePublicUrl = incoming.FilePublicUrl;
                    current.FileContentType = incoming.FileContentType;
                    current.FileSizeBytes = incoming.FileSizeBytes;
                    current.FileChecksum = incoming.FileChecksum;
                    current.SourceGeneratedAt = incoming.SourceGeneratedAt;
                    current.LastUpdated = incoming.LastUpdated;
                    current.SyncedAt = now;
                    current.UpdatedAt = now;
                }
                else
                {
                    _db.AppPackages.Add(new AppPackage
                    {
                        Id = incoming.Id == Guid.Empty ? IdGenerator.NewId() : incoming.Id,
                        InstallationType = installationType,
                        PackageId = normalizedId,
                        Name = incoming.Name,
                        Publisher = incoming.Publisher,
                        Version = incoming.Version,
                        Description = incoming.Description,
                        IconUrl = incoming.IconUrl,
                        SiteUrl = incoming.SiteUrl,
                        InstallCommand = incoming.InstallCommand,
                        MetadataJson = incoming.MetadataJson,
                        FileObjectKey = incoming.FileObjectKey,
                        FileBucket = incoming.FileBucket,
                        FilePublicUrl = incoming.FilePublicUrl,
                        FileContentType = incoming.FileContentType,
                        FileSizeBytes = incoming.FileSizeBytes,
                        FileChecksum = incoming.FileChecksum,
                        SourceGeneratedAt = incoming.SourceGeneratedAt,
                        LastUpdated = incoming.LastUpdated,
                        SyncedAt = now,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        return upserted;
    }

    public async Task<AppPackage> UpsertCustomAsync(AppPackage package, CancellationToken cancellationToken = default)
    {
        var normalizedPackageId = NormalizePackageId(package.PackageId, AppInstallationType.Custom);
        var now = DateTime.UtcNow;

        var current = await _db.AppPackages
            .SingleOrDefaultAsync(
                x => x.InstallationType == AppInstallationType.Custom && x.PackageId == normalizedPackageId,
                cancellationToken);

        if (current is null)
        {
            package.Id = package.Id == Guid.Empty ? IdGenerator.NewId() : package.Id;
            package.InstallationType = AppInstallationType.Custom;
            package.PackageId = normalizedPackageId;
            package.SyncedAt = now;
            package.CreatedAt = now;
            package.UpdatedAt = now;
            _db.AppPackages.Add(package);
            await _db.SaveChangesAsync(cancellationToken);
            return package;
        }

        current.Name = package.Name;
        current.Publisher = package.Publisher;
        current.Version = package.Version;
        current.Description = package.Description;
        current.IconUrl = package.IconUrl;
        current.SiteUrl = package.SiteUrl;
        current.InstallCommand = package.InstallCommand;
        current.MetadataJson = package.MetadataJson;
        current.FileObjectKey = package.FileObjectKey;
        current.FileBucket = package.FileBucket;
        current.FilePublicUrl = package.FilePublicUrl;
        current.FileContentType = package.FileContentType;
        current.FileSizeBytes = package.FileSizeBytes;
        current.FileChecksum = package.FileChecksum;
        current.LastUpdated = package.LastUpdated;
        current.UpdatedAt = now;
        current.SyncedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        return current;
    }

    private static string NormalizePackageId(string packageId, AppInstallationType installationType)
    {
        var normalized = packageId.Trim();
        return installationType == AppInstallationType.Winget || installationType == AppInstallationType.Chocolatey
            ? normalized.ToLowerInvariant()
            : normalized;
    }
}
