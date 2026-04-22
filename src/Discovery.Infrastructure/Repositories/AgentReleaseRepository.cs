using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AgentReleaseRepository(DiscoveryDbContext db) : IAgentReleaseRepository
{
    public async Task<IReadOnlyList<AgentRelease>> ListAsync(bool includeInactive = false, string? channel = null, CancellationToken cancellationToken = default)
    {
        var query = db.AgentReleases
            .AsNoTracking()
            .Include(release => release.Artifacts)
            .AsQueryable();

        if (!includeInactive)
            query = query.Where(release => release.IsActive);

        if (!string.IsNullOrWhiteSpace(channel))
            query = query.Where(release => release.Channel == channel.Trim().ToLowerInvariant());

        return await query
            .OrderByDescending(release => release.PublishedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentRelease?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await db.AgentReleases
            .AsNoTracking()
            .Include(release => release.Artifacts)
            .SingleOrDefaultAsync(release => release.Id == id, cancellationToken);
    }

    public async Task<AgentRelease?> GetByVersionAsync(string version, string channel, CancellationToken cancellationToken = default)
    {
        return await db.AgentReleases
            .AsNoTracking()
            .Include(release => release.Artifacts)
            .SingleOrDefaultAsync(
                release => release.Version == version && release.Channel == channel,
                cancellationToken);
    }

    public async Task<AgentRelease> CreateAsync(AgentRelease release, CancellationToken cancellationToken = default)
    {
        release.Id = IdGenerator.NewId();
        release.CreatedAt = DateTime.UtcNow;
        release.UpdatedAt = release.CreatedAt;

        db.AgentReleases.Add(release);
        await db.SaveChangesAsync(cancellationToken);
        return release;
    }

    public async Task UpdateAsync(AgentRelease release, CancellationToken cancellationToken = default)
    {
        var existing = await db.AgentReleases.SingleOrDefaultAsync(item => item.Id == release.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Version = release.Version;
        existing.Channel = release.Channel;
        existing.IsActive = release.IsActive;
        existing.Mandatory = release.Mandatory;
        existing.MinimumSupportedVersion = release.MinimumSupportedVersion;
        existing.ReleaseNotes = release.ReleaseNotes;
        existing.PublishedAtUtc = release.PublishedAtUtc;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.CreatedBy = release.CreatedBy;
        existing.UpdatedBy = release.UpdatedBy;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await db.AgentReleases
            .Where(release => release.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<AgentReleaseArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        return await db.AgentReleaseArtifacts
            .AsNoTracking()
            .Include(artifact => artifact.Release)
            .SingleOrDefaultAsync(artifact => artifact.Id == artifactId, cancellationToken);
    }

    public async Task<AgentReleaseArtifact?> GetArtifactAsync(
        Guid releaseId,
        string platform,
        string architecture,
        Core.Enums.AgentReleaseArtifactType artifactType,
        CancellationToken cancellationToken = default)
    {
        return await db.AgentReleaseArtifacts
            .AsNoTracking()
            .Include(artifact => artifact.Release)
            .SingleOrDefaultAsync(
                artifact => artifact.AgentReleaseId == releaseId
                    && artifact.Platform == platform
                    && artifact.Architecture == architecture
                    && artifact.ArtifactType == artifactType,
                cancellationToken);
    }

    public async Task<AgentReleaseArtifact> CreateArtifactAsync(AgentReleaseArtifact artifact, CancellationToken cancellationToken = default)
    {
        artifact.Id = IdGenerator.NewId();
        artifact.CreatedAt = DateTime.UtcNow;
        artifact.UpdatedAt = artifact.CreatedAt;

        db.AgentReleaseArtifacts.Add(artifact);
        await db.SaveChangesAsync(cancellationToken);
        return artifact;
    }

    public async Task UpdateArtifactAsync(AgentReleaseArtifact artifact, CancellationToken cancellationToken = default)
    {
        var existing = await db.AgentReleaseArtifacts.SingleOrDefaultAsync(item => item.Id == artifact.Id, cancellationToken);
        if (existing is null)
            return;

        existing.Platform = artifact.Platform;
        existing.Architecture = artifact.Architecture;
        existing.ArtifactType = artifact.ArtifactType;
        existing.FileName = artifact.FileName;
        existing.ContentType = artifact.ContentType;
        existing.StorageObjectKey = artifact.StorageObjectKey;
        existing.StorageBucket = artifact.StorageBucket;
        existing.StorageProviderType = artifact.StorageProviderType;
        existing.Sha256 = artifact.Sha256;
        existing.SizeBytes = artifact.SizeBytes;
        existing.SignatureThumbprint = artifact.SignatureThumbprint;
        existing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        await db.AgentReleaseArtifacts
            .Where(artifact => artifact.Id == artifactId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
