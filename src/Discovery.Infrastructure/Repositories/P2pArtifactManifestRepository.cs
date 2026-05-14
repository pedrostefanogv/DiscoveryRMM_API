using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class P2pArtifactManifestRepository : IP2pArtifactManifestRepository
{
    private readonly DiscoveryDbContext _db;

    public P2pArtifactManifestRepository(DiscoveryDbContext db)
    {
        _db = db;
    }

    public async Task<P2pArtifactManifest?> GetByArtifactIdAsync(Guid artifactId, CancellationToken ct = default)
    {
        return await _db.P2pArtifactManifests
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ArtifactId == artifactId, ct);
    }

    public async Task UpsertAsync(P2pArtifactManifest manifest, CancellationToken ct = default)
    {
        var existing = await _db.P2pArtifactManifests
            .FirstOrDefaultAsync(m => m.ArtifactId == manifest.ArtifactId, ct);

        if (existing is not null)
        {
            // Só sobrescreve se sha256 diferente OU generatedAt mais novo
            if (existing.Sha256 != manifest.Sha256 || manifest.GeneratedAt > existing.GeneratedAt)
            {
                existing.ManifestJson = manifest.ManifestJson;
                existing.Sha256 = manifest.Sha256;
                existing.TotalSize = manifest.TotalSize;
                existing.ChunkSize = manifest.ChunkSize;
                existing.TotalChunks = manifest.TotalChunks;
                existing.GeneratedBy = manifest.GeneratedBy;
                existing.GeneratedAt = manifest.GeneratedAt;
                existing.UpdatedAt = DateTime.UtcNow;
            }
        }
        else
        {
            manifest.UpdatedAt = DateTime.UtcNow;
            _db.P2pArtifactManifests.Add(manifest);
        }

        await _db.SaveChangesAsync(ct);
    }
}
