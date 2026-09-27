using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public class TicketAssignmentService(
    DiscoveryDbContext db,
    IDepartmentTeamResolver teamResolver) : ITicketAssignmentService
{
    public async Task<int?> GetStrategyAsync(Guid departmentId, CancellationToken ct = default)
    {
        var dept = await db.Departments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == departmentId, ct);
        return dept?.AssignmentStrategy;
    }

    public async Task<Guid?> ResolveAssigneeAsync(Guid departmentId, CancellationToken ct = default)
    {
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct);
        if (dept is null) return null;

        // None: sem auto-atribuição. AiTriage: a triagem por IA roda de forma
        // assíncrona (fila) e decide por conta própria — não é round-robin.
        if (dept.AssignmentStrategy is (int)TicketAssignmentStrategy.None
            or (int)TicketAssignmentStrategy.AiTriage)
        {
            return null;
        }

        return await ResolveFallbackAsync(departmentId, dept.AssignmentStrategy, ct);
    }

    /// <summary>
    /// Estratégia determinística explícita (usada também como fallback da
    /// triagem por IA e pela rede de segurança de chamados sem responsável).
    /// </summary>
    public async Task<Guid?> ResolveFallbackAsync(Guid departmentId, int fallbackStrategy, CancellationToken ct = default)
    {
        // Mesmo resolvedor da triagem: garante que estratégia determinística e IA
        // enxerguem exatamente o mesmo conjunto de candidatos.
        var members = (await teamResolver.ResolveMembersAsync(departmentId, ct))
            .Select(member => member.UserId)
            .ToList();

        if (members.Count == 0)
            return null;

        if (fallbackStrategy == (int)TicketAssignmentStrategy.RoundRobin)
        {
            var lastId = await db.Departments.AsNoTracking()
                .Where(d => d.Id == departmentId)
                .Select(d => d.RoundRobinLastUserId)
                .FirstOrDefaultAsync(ct);

            var index = lastId.HasValue ? members.IndexOf(lastId.Value) : -1;
            var next = members[(index + 1) % members.Count];

            var tracked = await db.Departments.FirstAsync(d => d.Id == departmentId, ct);
            tracked.RoundRobinLastUserId = next;
            await db.SaveChangesAsync(ct);
            return next;
        }

        // LeastOpenTickets: menos chamados abertos; empate pelo membro mais antigo.
        var openCounts = await db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.ClosedAt == null && t.AssignedToUserId != null
                        && members.Contains(t.AssignedToUserId.Value))
            .GroupBy(t => t.AssignedToUserId!.Value)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var counts = openCounts.ToDictionary(x => x.UserId, x => x.Count);
        return members.OrderBy(u => counts.TryGetValue(u, out var c) ? c : 0).First();
    }
}
