namespace Discovery.Core.Interfaces;

/// <summary>
/// Membro candidato da equipe de um departamento (perfil usado pela triagem).
/// <paramref name="Origin"/> é "own" para membros do próprio departamento ou
/// "inherited:&lt;departmentId&gt;" para membros herdados do global pai.
/// </summary>
public sealed record DepartmentTeamMember(
    Guid UserId,
    string? DisplayName,
    string? SkillTagsJson,
    int SkillLevel,
    int? MaxOpenTickets,
    decimal Weight,
    bool AcceptsAiAssignment,
    DateTime MemberSince,
    string Origin = "own");

/// <summary>
/// Ponto único de resolução da equipe candidata de um departamento, usado pela
/// auto-atribuição determinística e pela triagem por IA.
///
/// A herança de departamento (D7) é implementada SOMENTE aqui:
/// equipe efetiva = membros ativos do próprio + membros ativos herdados do
/// departamento GLOBAL pai (um salto), deduplicados por usuário.
/// </summary>
public interface IDepartmentTeamResolver
{
    /// <summary>
    /// Membros elegíveis como candidatos: departamento ativo, vínculo ativo,
    /// usuário ativo. Próprios primeiro e depois herdados do global, com ordem
    /// estável por CreatedAt/UserId.
    /// </summary>
    Task<IReadOnlyList<DepartmentTeamMember>> ResolveMembersAsync(
        Guid departmentId, CancellationToken ct = default);
}
