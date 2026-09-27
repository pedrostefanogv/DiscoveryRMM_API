namespace Discovery.Core.Interfaces;

/// <summary>
/// Membro candidato da equipe de um departamento (perfil usado pela triagem).
/// </summary>
public sealed record DepartmentTeamMember(
    Guid UserId,
    string? DisplayName,
    string? SkillTagsJson,
    int SkillLevel,
    int? MaxOpenTickets,
    decimal Weight,
    bool AcceptsAiAssignment,
    DateTime MemberSince);

/// <summary>
/// Ponto único de resolução da equipe candidata de um departamento, usado pela
/// auto-atribuição determinística e pela triagem por IA.
///
/// É também o PONTO DE EXTENSÃO preparado para a herança de departamento:
/// quando/se InheritFromGlobalId for implementado, a regra de herdar membros do
/// departamento global entra SOMENTE aqui (ver
/// docs_planejamento/DEPARTMENT_INHERITANCE_SEAM.md), sem tocar nos serviços.
/// </summary>
public interface IDepartmentTeamResolver
{
    /// <summary>
    /// Membros do departamento elegíveis como candidatos: vínculo ativo, usuário
    /// ativo, ordenados pela antiguidade do vínculo (ordem do round-robin atual).
    /// </summary>
    Task<IReadOnlyList<DepartmentTeamMember>> ResolveMembersAsync(
        Guid departmentId, CancellationToken ct = default);
}
