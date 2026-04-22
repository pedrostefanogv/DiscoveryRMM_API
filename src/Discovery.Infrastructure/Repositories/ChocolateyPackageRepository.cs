using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class ChocolateyPackageRepository : IChocolateyPackageRepository
{
    private readonly DiscoveryDbContext _db;

    public ChocolateyPackageRepository(DiscoveryDbContext db) => _db = db;

    public async Task<ChocolateyPackage?> GetByPackageIdAsync(string packageId)
    {
        var normalized = NormalizeId(packageId);
        return await _db.ChocolateyPackages
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.PackageId == normalized);
    }

    public async Task<(IReadOnlyList<ChocolateyPackage> Items, int TotalCount)> SearchAsync(
        string? search,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var query = _db.ChocolateyPackages.AsNoTracking();

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

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.DownloadCount)
            .ThenBy(x => x.PackageId)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task BulkUpsertAsync(
        IReadOnlyList<ChocolateyPackage> packages,
        CancellationToken cancellationToken = default)
    {
        if (packages.Count == 0)
            return;

        var now = DateTime.UtcNow;

        // Process in batches of 200 to avoid excessive memory/transaction size
        foreach (var batch in packages.Chunk(200))
        {
            var packageIds = batch.Select(p => NormalizeId(p.PackageId)).ToList();

            var existing = await _db.ChocolateyPackages
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
                    current.LicenseUrl = incoming.LicenseUrl;
                    current.Tags = incoming.Tags;
                    current.DownloadCount = incoming.DownloadCount;
                    current.LastUpdated = incoming.LastUpdated;
                    current.SyncedAt = now;
                }
                else
                {
                    _db.ChocolateyPackages.Add(new ChocolateyPackage
                    {
                        Id = IdGenerator.NewId(),
                        PackageId = normalizedId,
                        Name = incoming.Name,
                        Publisher = incoming.Publisher,
                        Version = incoming.Version,
                        Description = incoming.Description,
                        Homepage = incoming.Homepage,
                        LicenseUrl = incoming.LicenseUrl,
                        Tags = incoming.Tags,
                        DownloadCount = incoming.DownloadCount,
                        LastUpdated = incoming.LastUpdated,
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
        return await _db.ChocolateyPackages.CountAsync(cancellationToken);
    }

    private static string NormalizeId(string packageId) =>
        packageId.Trim().ToLowerInvariant();
}
