using Discovery.Core.Cqrs;
using Discovery.Core.Enums.Identity;

namespace Discovery.Core.Cqrs.Users.Commands;

/// <summary>
/// Cria um usuário e, opcionalmente, já o vincula a um grupo num escopo.
///
/// <para>
/// <b>Por que escopo aqui:</b> antes, criar um usuário e dar-lhe acesso eram dois
/// passos não relacionados — o usuário nascia com ZERO permissões e o vínculo
/// dependia de uma segunda chamada que podia falhar em silêncio (o console fazia
/// um loop sequencial e reportava "groupsFailed"). Resultado: usuários criados
/// "com acesso" que não conseguiam fazer nada, ou vínculos parciais.
/// </para>
///
/// <para>
/// A criação agora é <b>atômica</b> e <b>escopada</b>: valida a política de senha
/// ANTES de persistir e exige que o ator tenha escopo para conceder o vínculo
/// inicial (mesma regra de <c>AssignGroupRoleCommand</c>).
/// </para>
/// </summary>
/// <param name="Login">Login único.</param>
/// <param name="Email">E-mail único.</param>
/// <param name="FullName">Nome completo.</param>
/// <param name="Password">Senha inicial (validada pelo IPasswordService antes de gravar).</param>
/// <param name="MfaRequired">Exigir MFA (default true, como antes).</param>
/// <param name="GroupId">Grupo ao qual vincular já na criação (opcional).</param>
/// <param name="RequestedByUserId">
/// Ator. Necessário para validar que ele pode conceder o vínculo inicial.
/// </param>
public sealed record CreateUserCommand(
    string Login, string Email, string FullName, string Password,
    bool MfaRequired = true,
    Guid? GroupId = null,
    Guid RequestedByUserId = default
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
