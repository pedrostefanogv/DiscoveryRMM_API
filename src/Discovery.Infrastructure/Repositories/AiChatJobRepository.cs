using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Repositories;

public class AiChatJobRepository : IAiChatJobRepository
{
    private readonly DiscoveryDbContext _db;
    private readonly ILogger<AiChatJobRepository> _logger;

    public AiChatJobRepository(DiscoveryDbContext db, ILogger<AiChatJobRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

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

    public async Task<IReadOnlyList<AiChatJob>> GetRecoverableAsync(int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);

        return await _db.AiChatJobs
            .AsNoTracking()
            .Where(j => j.Status == "Pending" || j.Status == "Processing")
            .OrderBy(j => j.CreatedAt)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task UpdateAsync(AiChatJob job, CancellationToken ct = default)
    {
        // Usa ExecuteUpdateAsync para evitar race condition (busca-then-update sem lock)
        // Só atualiza se o status atual permitir (ex: não sobrescrever Completed com Processing)
        var rows = await _db.AiChatJobs
            .Where(j => j.Id == job.Id && (j.Status == "Pending" || j.Status == "Processing"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(j => j.Status, job.Status)
                .SetProperty(j => j.AssistantMessage, job.AssistantMessage)
                .SetProperty(j => j.TokensUsed, job.TokensUsed)
                .SetProperty(j => j.ErrorMessage, job.ErrorMessage)
                .SetProperty(j => j.StartedAt, job.StartedAt)
                .SetProperty(j => j.CompletedAt, job.CompletedAt),
            ct);

        if (rows == 0)
        {
            // Pode já ter sido processado por outra instância
            _logger.LogWarning("AiChatJob {JobId} não atualizado: não encontrado ou já finalizado", job.Id);
        }
    }
}
