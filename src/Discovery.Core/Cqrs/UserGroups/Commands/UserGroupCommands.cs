using Discovery.Core.Cqrs;
using Discovery.Core.DTOs.Groups;
using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Cqrs.UserGroups.Commands;

public sealed record CreateUserGroupCommand(
    string Name, string? Description
) : ICommand<Result<UserGroupDto>>;

public sealed record UpdateUserGroupCommand(
    Guid Id, string? Name, string? Description, bool? IsActive
) : ICommand<Result<UserGroupDto>>;

public sealed record DeleteUserGroupCommand(Guid Id) : ICommand<Result<VoidResult>>;

/// <summary>Vincula um usuário ao grupo.</summary>
/// <param name="RequestedByUserId">
/// Ator que executa a operação. Usado para impedir que um administrador conceda
/// acessos (membro ou papel) fora do próprio escopo — antes o handler não sabia
/// quem estava concedendo, então qualquer <c>Users:Edit</c> Global podia vincular
/// qualquer pessoa a qualquer grupo.
/// </param>
public sealed record AddGroupMemberCommand(
    Guid GroupId, Guid UserId, Guid RequestedByUserId = default
) : ICommand<Result<GroupMemberDto>>;

/// <summary>Remove o vínculo do usuário com o grupo.</summary>
/// <param name="RequestedByUserId">Ator da operação (mesma regra de escopo do Add).</param>
public sealed record RemoveGroupMemberCommand(
    Guid GroupId, Guid UserId, Guid RequestedByUserId = default
) : ICommand<Result<VoidResult>>;

/// <summary>Atribui uma role ao grupo, em um nível de escopo (Global/Client/Site).</summary>
/// <param name="RequestedByUserId">
/// Ator da concessão. Um administrador escopado a um cliente não pode conceder
/// escopo Global nem de outro cliente — é o vetor de escalação entre tenants.
/// </param>
public sealed record AssignGroupRoleCommand(
    Guid GroupId, Guid RoleId, ScopeLevel ScopeLevel, Guid? ScopeId, Guid RequestedByUserId = default
) : ICommand<Result<GroupRoleAssignmentDto>>;

/// <summary>Remove uma atribuição de role do grupo.</summary>
/// <param name="RequestedByUserId">Ator da remoção (mesma regra de escopo do assign).</param>
public sealed record RemoveGroupRoleAssignmentCommand(
    Guid GroupId, Guid AssignmentId, Guid RequestedByUserId = default
) : ICommand<Result<VoidResult>>;

public sealed record UserGroupDto(
    Guid Id, string Name, string? Description,
    bool IsActive, DateTime CreatedAt, DateTime UpdatedAt
);
