using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolvedor ÚNICO da equipe de um departamento (D7).
///
/// Equipe efetiva = membros ATIVOS do próprio departamento + membros ATIVOS
/// herdados do departamento GLOBAL pai (ClientId == null), deduplicados por
/// UserId. A herança é de um salto: departamento global nunca herda e um pai
/// não global/inativo é ignorado defensivamente (dados legados), com log.
///
/// A ordem é estável: próprios primeiro (CreatedAt, UserId) e depois os
/// herdados (CreatedAt, UserId), o que preserva o cursor do round-robin para os
/// membros do próprio departamento.
/// </summary>
public class DepartmentTeamResolver(
    DiscoveryDbContext db,
    ILogger<DepartmentTeamResolver>? logger = null) : IDepartmentTeamResolver
{
    public async Task<IReadOnlyList<DepartmentTeamMember>> ResolveMembersAsync(
        Guid departmentId, CancellationToken ct = default)
    {
        var department = await db.Departments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == departmentId, ct);

        // D4 — departamento inativo não tem equipe elegível.
        if (department is null || !department.IsActive)
            return [];

        var result = new List<DepartmentTeamMember>();
        var seen = new HashSet<Guid>();

        var own = await LoadMembersAsync(department.Id, ct);
        AddRange(result, seen, own, "own");

        var parent = await ResolveGlobalParentAsync(department, ct);
        if (parent is not null)
        {
            var inherited = await LoadMembersAsync(parent.Id, ct);
            AddRange(result, seen, inherited, $"inherited:{parent.Id}");
        }

        return result;
    }

    /// <summary>
    /// Só aceita o pai quando é GLOBAL e ATIVO e o departamento é de cliente.
    /// Ciclo/auto-herança legada é ignorada na leitura (um salto, sem cadeia).
    /// </summary>
    private async Task<Department?> ResolveGlobalParentAsync(
        Department department, CancellationToken ct)
    {
        if (department.ClientId is null || department.InheritFromGlobalId is not { } parentId)
            return null;

        if (parentId == department.Id)
        {
            logger?.LogWarning(
                "Departamento {DepartmentId} aponta para si mesmo em InheritFromGlobalId; herança ignorada.",
                department.Id);
            return null;
        }

        var parent = await db.Departments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == parentId, ct);

        if (parent is null)
        {
            logger?.LogWarning(
                "Departamento {DepartmentId} aponta para o global {ParentId}, que não existe; herança ignorada.",
                department.Id, parentId);
            return null;
        }

        if (parent.ClientId is not null)
        {
            // Dados legados: contrato atual só permite herdar de global. Não
            // seguimos a cadeia (um salto) e descartamos o vínculo inválido.
            logger?.LogWarning(
                "Departamento {DepartmentId} herda de {ParentId}, que não é global (ClientId={ClientId}); herança ignorada.",
                department.Id, parent.Id, parent.ClientId);
            return null;
        }

        if (!parent.IsActive)
        {
            logger?.LogWarning(
                "Departamento {DepartmentId} herda do global {ParentId}, que está inativo; herança ignorada.",
                department.Id, parent.Id);
            return null;
        }

        return parent;
    }

    private async Task<List<MemberRow>> LoadMembersAsync(Guid departmentId, CancellationToken ct)
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
            .ThenBy(x => x.UserId)
            .ToListAsync(ct);

        return rows
            .Select(row => new MemberRow(
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

    private static void AddRange(
        List<DepartmentTeamMember> target, HashSet<Guid> seen,
        IEnumerable<MemberRow> rows, string origin)
    {
        foreach (var row in rows)
        {
            if (!seen.Add(row.UserId))
                continue;

            target.Add(new DepartmentTeamMember(
                row.UserId,
                row.DisplayName,
                row.SkillTagsJson,
                row.SkillLevel,
                row.MaxOpenTickets,
                row.Weight,
                row.AcceptsAiAssignment,
                row.MemberSince,
                origin));
        }
    }

    private sealed record MemberRow(
        Guid UserId,
        string? DisplayName,
        string? SkillTagsJson,
        int SkillLevel,
        int? MaxOpenTickets,
        decimal Weight,
        bool AcceptsAiAssignment,
        DateTime MemberSince);
}
