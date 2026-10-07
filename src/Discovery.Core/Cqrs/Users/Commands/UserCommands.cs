using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Users.Commands;

public sealed record CreateUserCommand(
    string Login, string Email, string FullName, string Password,
    // Default true preserva o comportamento anterior e mantém compatibilidade
    // com chamadas existentes; o console web envia o valor explicitamente.
    bool MfaRequired = true
) : ICommand<Result<UserDto>>;

public sealed record UpdateUserCommand(
    Guid Id, string? Login, string? Email, string? FullName,
    bool? IsActive, bool? MfaRequired
) : ICommand<Result<UserDto>>;

public sealed record DeleteUserCommand(Guid Id) : ICommand<Result<VoidResult>>;

/// <summary>Atualiza o próprio perfil (e-mail e nome). Não permite trocar login.</summary>
public sealed record UpdateMyProfileCommand(
    Guid Id, string Email, string FullName
) : ICommand<Result<UserDto>>;

/// <summary>Marca o usuário para trocar a senha no próximo login (reset administrativo "forçado").</summary>
public sealed record ForcePasswordChangeCommand(Guid Id) : ICommand<Result<VoidResult>>;

/// <summary>Revoga (desativa) todas as chaves MFA ativas de um usuário.</summary>
public sealed record RevokeUserMfaCommand(Guid Id) : ICommand<Result<VoidResult>>;

/// <summary>Revoga (desativa) uma chave MFA específica de um usuário.</summary>
public sealed record RevokeUserMfaKeyCommand(Guid Id, Guid KeyId) : ICommand<Result<VoidResult>>;

/// <summary>
/// Usuário exposto nas respostas administrativas. Os contadores de lockout NÃO fazem
/// parte deste contrato (vazavam para qualquer usuário com Users.View); eles ficam em
/// GET /api/v1/users/{id}/security-state, restrito a Users.Edit.
/// </summary>
public sealed record UserDto(
    Guid Id, string Login, string Email, string FullName,
    bool IsActive, bool MfaRequired, bool MfaConfigured,
    bool MustChangePassword, bool MustChangeProfile,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastLoginAt
);
