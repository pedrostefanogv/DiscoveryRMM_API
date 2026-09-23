using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public class TicketAssignmentService(DiscoveryDbContext db) : ITicketAssignmentService
{
    public async Task<Guid?> ResolveAssigneeAsync(Guid departmentId, CancellationToken ct = default)
    {
        var dept = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct);
        if (dept is null || dept.AssignmentStrategy == (int)TicketAssignmentStrategy.None)
            return null;

        var members = await db.DepartmentMembers.AsNoTracking()
            .Where(m => m.DepartmentId == departmentId && m.IsActive)
            .Join(db.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, _) => new { m.UserId, m.CreatedAt })
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.UserId)
            .ToListAsync(ct);

        if (members.Count == 0)
            return null;

        if (dept.AssignmentStrategy == (int)TicketAssignmentStrategy.RoundRobin)
        {
            var index = dept.RoundRobinLastUserId.HasValue ? members.IndexOf(dept.RoundRobinLastUserId.Value) : -1;
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
