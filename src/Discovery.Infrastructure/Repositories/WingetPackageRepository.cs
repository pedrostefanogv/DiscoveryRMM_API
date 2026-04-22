using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class WingetPackageRepository : IWingetPackageRepository
{
    private readonly DiscoveryDbContext _db;

    public WingetPackageRepository(DiscoveryDbContext db) => _db = db;

    public async Task<WingetPackage?> GetByPackageIdAsync(string packageId)
    {
        var normalized = NormalizeId(packageId);
        return await _db.WingetPackages
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.PackageId == normalized);
    }

    public async Task<(IReadOnlyList<WingetPackage> Items, int TotalCount)> SearchAsync(
        string? search,
        string? architecture,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var query = _db.WingetPackages.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x =>
                x.PackageId.ToLower().Contains(term) ||
                x.Name.ToLower().Contains(term) ||
                x.Publisher.ToLower().Contains(term) ||
                x.Description.ToLower().Contains(term) ||
                x.Tags.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(architecture))
        {
            var arch = architecture.Trim().ToLower();
            query = query.Where(x => x.InstallerUrlsJson.ToLower().Contains($"\"{arch}\""));
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

    public async Task BulkUpsertAsync(
        IReadOnlyList<WingetPackage> packages,
        CancellationToken cancellationToken = default)
    {
        if (packages.Count == 0)
            return;

        var now = DateTime.UtcNow;

        foreach (var batch in packages.Chunk(200))
        {
            var packageIds = batch.Select(p => NormalizeId(p.PackageId)).ToList();

            var existing = await _db.WingetPackages
                .Where(x => packageIds.Contains(x.PackageId))
                .ToDictionaryAsync(x => x.PackageId, StringComparer.OrdinalIgnoreCase, cancellationToken);

            foreach (var incoming in batch)
            {
                var normalizedId = NormalizeId(incoming.PackageId);
                if (string.IsNullOrWhiteSpace(normalizedId))
                    continue;

                if (existing.TryGetValue(normalizedId, out var current))
                {
                    current.Name = incoming.Name;
                    current.Publisher = incoming.Publisher;
                    current.Version = incoming.Version;
                    current.Description = incoming.Description;
                    current.Homepage = incoming.Homepage;
                    current.License = incoming.License;
                    current.Category = incoming.Category;
                    current.Icon = incoming.Icon;
                    current.InstallCommand = incoming.InstallCommand;
                    current.Tags = incoming.Tags;
                    current.InstallerUrlsJson = incoming.InstallerUrlsJson;
                    current.LastUpdated = incoming.LastUpdated;
                    current.SourceGeneratedAt = incoming.SourceGeneratedAt;
                    current.SyncedAt = now;
                }
                else
                {
                    _db.WingetPackages.Add(new WingetPackage
                    {
                        Id = IdGenerator.NewId(),
                        PackageId = normalizedId,
                        Name = incoming.Name,
                        Publisher = incoming.Publisher,
                        Version = incoming.Version,
                        Description = incoming.Description,
                        Homepage = incoming.Homepage,
                        License = incoming.License,
                        Category = incoming.Category,
                        Icon = incoming.Icon,
                        InstallCommand = incoming.InstallCommand,
                        Tags = incoming.Tags,
                        InstallerUrlsJson = incoming.InstallerUrlsJson,
                        LastUpdated = incoming.LastUpdated,
                        SourceGeneratedAt = incoming.SourceGeneratedAt,
                        SyncedAt = now,
                        CreatedAt = now
                    });
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        return await _db.WingetPackages.CountAsync(cancellationToken);
    }

    private static string NormalizeId(string packageId) =>
        packageId.Trim().ToLowerInvariant();
}
