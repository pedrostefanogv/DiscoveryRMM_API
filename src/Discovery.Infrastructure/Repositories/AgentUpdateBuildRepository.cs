using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AgentUpdateBuildRepository(DiscoveryDbContext db) : IAgentUpdateBuildRepository
{
    public async Task<AgentUpdateBuild?> GetCurrentAsync(
        string platform,
        string architecture,
        AgentReleaseArtifactType artifactType,
        CancellationToken cancellationToken = default)
    {
        return await db.AgentUpdateBuilds
            .AsNoTracking()
            .Where(build => build.IsActive)
            .Where(build => build.Platform == platform)
            .Where(build => build.Architecture == architecture)
            .Where(build => build.ArtifactType == artifactType)
            .OrderByDescending(build => build.UpdatedAt)
            .ThenByDescending(build => build.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AgentUpdateBuild> CreateAsync(AgentUpdateBuild build, CancellationToken cancellationToken = default)
    {
        build.Id = IdGenerator.NewId();
        build.CreatedAt = DateTime.UtcNow;
        build.UpdatedAt = build.CreatedAt;

        db.AgentUpdateBuilds.Add(build);
        await db.SaveChangesAsync(cancellationToken);
        return build;
    }

    public async Task DeactivateCurrentAsync(
        string platform,
        string architecture,
        AgentReleaseArtifactType artifactType,
        Guid keepActiveBuildId,
        CancellationToken cancellationToken = default)
    {
        var targets = await db.AgentUpdateBuilds
            .Where(build => build.IsActive)
            .Where(build => build.Platform == platform)
            .Where(build => build.Architecture == architecture)
            .Where(build => build.ArtifactType == artifactType)
            .Where(build => build.Id != keepActiveBuildId)
            .ToListAsync(cancellationToken);

        if (targets.Count == 0)
            return;

        foreach (var build in targets)
        {
            build.IsActive = false;
            build.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
