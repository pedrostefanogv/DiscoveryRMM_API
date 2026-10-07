using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.DTOs.Mfa;

// Alias: Discovery.Core.DTOs.Users define um UserDto com o mesmo nome do record do CQRS.
using MySecurityProfileDto = Discovery.Core.DTOs.Users.MySecurityProfileDto;
using UserSecurityStateDto = Discovery.Core.DTOs.Users.UserSecurityStateDto;

namespace Discovery.Core.Cqrs.Users.Queries;

public sealed record ListUsersQuery(string? Cursor = null, int Limit = 50)
    : IQuery<Result<UsersPageDto>>;

public sealed record GetUserByIdQuery(Guid Id) : IQuery<Result<UserDto>>;

/// <summary>
/// Perfil de segurança do próprio usuário: exigência/estado de MFA por role e as chaves ativas.
/// Contrato consumido por GET /api/v1/users/me/security (ProfilePage).
/// </summary>
public sealed record GetMySecurityProfileQuery(Guid UserId) : IQuery<Result<MySecurityProfileDto>>;

/// <summary>Lista as chaves MFA ativas de outro usuário (visão administrativa).</summary>
public sealed record ListUserMfaKeysQuery(Guid UserId) : IQuery<Result<IReadOnlyList<AdminUserMfaKeyDto>>>;

/// <summary>
/// Estado de segurança do usuário (lockout de senha/MFA, obrigações de troca).
/// Restrito a quem tem Users.Edit — não vai no UserDto geral.
/// </summary>
public sealed record GetUserSecurityStateQuery(Guid Id) : IQuery<Result<UserSecurityStateDto>>;

public sealed record UsersPageDto(
    IReadOnlyList<UserDto> Items, string? NextCursor, bool HasMore, int Total
);
