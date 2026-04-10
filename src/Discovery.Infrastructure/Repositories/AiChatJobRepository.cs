using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AiChatJobRepository : IAiChatJobRepository
{
    private readonly DiscoveryDbContext _db;

    public AiChatJobRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AiChatJob> CreateAsync(AiChatJob job, CancellationToken ct = default)
    {
        job.Id = IdGenerator.NewId();
        job.CreatedAt = DateTime.UtcNow;

        _db.AiChatJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<AiChatJob?> GetByIdAsync(Guid jobId, Guid agentId, CancellationToken ct = default)
    {
        return await _db.AiChatJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId && j.AgentId == agentId)
            .SingleOrDefaultAsync(ct);
    }

    public async Task UpdateAsync(AiChatJob job, CancellationToken ct = default)
    {
        var existingJob = await _db.AiChatJobs.SingleOrDefaultAsync(j => j.Id == job.Id, ct);
        if (existingJob is null)
            return;

        existingJob.Status = job.Status;
        existingJob.AssistantMessage = job.AssistantMessage;
        existingJob.TokensUsed = job.TokensUsed;
        existingJob.ErrorMessage = job.ErrorMessage;
        existingJob.StartedAt = job.StartedAt;
        existingJob.CompletedAt = job.CompletedAt;

        await _db.SaveChangesAsync(ct);
    }
}
