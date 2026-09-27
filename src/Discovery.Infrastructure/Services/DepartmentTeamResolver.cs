using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação padrão: apenas os membros ativos do próprio departamento com
/// usuário ativo, ordenados pela data de entrada no departamento.
///
/// NÃO implementa herança de departamento (InheritFromGlobalId) — o campo
/// continua apenas informativo. Este é o lugar onde essa herança deve entrar
/// quando for priorizada.
/// </summary>
public class DepartmentTeamResolver(DiscoveryDbContext db) : IDepartmentTeamResolver
{
    public async Task<IReadOnlyList<DepartmentTeamMember>> ResolveMembersAsync(
        Guid departmentId, CancellationToken ct = default)
    {
        var rows = await db.DepartmentMembers.AsNoTracking()
            .Where(m => m.DepartmentId == departmentId && m.IsActive)
            .Join(db.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id,
                (m, u) => new
                {
                    m.UserId,
                    u.FullName,
                    u.Login,
                    m.SkillTagsJson,
                    m.SkillLevel,
                    m.MaxOpenTickets,
                    m.Weight,
                    m.AcceptsAiAssignment,
                    m.CreatedAt
                })
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);

        return rows
            .Select(row => new DepartmentTeamMember(
                row.UserId,
                string.IsNullOrWhiteSpace(row.FullName) ? row.Login : row.FullName,
                row.SkillTagsJson,
                row.SkillLevel,
                row.MaxOpenTickets,
                row.Weight,
                row.AcceptsAiAssignment,
                row.CreatedAt))
            .ToList();
    }
}
