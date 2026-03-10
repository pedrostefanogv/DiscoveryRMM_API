using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AiChatJobRepository : IAiChatJobRepository
{
    private readonly MeduzaDbContext _db;

    public AiChatJobRepository(MeduzaDbContext db) => _db = db;

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
