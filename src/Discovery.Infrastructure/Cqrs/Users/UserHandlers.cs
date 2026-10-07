using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Cqrs.Users.Queries;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Enums.Security;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using MediatR;

// Alias explícito: Discovery.Core.DTOs.Users também define um tipo chamado UserDto,
// o que tornaria a importação direta ambígua com o UserDto do CQRS.
using AdminUserMfaKeyDto = Discovery.Core.DTOs.Mfa.AdminUserMfaKeyDto;
using MyMfaKeySummaryDto = Discovery.Core.DTOs.Users.MyMfaKeySummaryDto;
using MySecurityProfileDto = Discovery.Core.DTOs.Users.MySecurityProfileDto;
using UserSecurityStateDto = Discovery.Core.DTOs.Users.UserSecurityStateDto;

namespace Discovery.Infrastructure.Cqrs.Users;

public sealed class ListUsersQueryHandler(
    IUserRepository repo
) : IRequestHandler<ListUsersQuery, Result<UsersPageDto>>
{
    public async Task<Result<UsersPageDto>> Handle(ListUsersQuery q, CancellationToken ct)
    {
        var users = await repo.GetAllPageAsync(q.Cursor, q.Limit);
        var count = await repo.CountAsync();
        var items = users.Select(Map).ToList().AsReadOnly();
        var hasMore = items.Count >= q.Limit;
        // Cursor DEVE ser codificado via helper — o decoder (UserRepository →
        // TryDecodeCreatedAtCursor) espera Base64 "ticks|guidN". Emitir só o Id
        // cru quebrava a paginação da lista de usuários (decode falhava →
        // repetia a 1ª página).
        var nextCursor = hasMore && items.Count > 0
            ? CursorPaginationHelper.EncodeCreatedAtCursor(items[^1].CreatedAt, items[^1].Id)
            : null;

        return Result<UsersPageDto>.Success(new UsersPageDto(items, nextCursor, hasMore, count));
    }

    private static UserDto Map(User u) => UserMapping.ToDto(u);
}

public sealed class GetUserByIdQueryHandler(
    IUserRepository repo
) : IRequestHandler<GetUserByIdQuery, Result<UserDto>>
{
    public async Task<Result<UserDto>> Handle(GetUserByIdQuery q, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(q.Id);
        return user is null
            ? Result<UserDto>.Failure(Error.NotFound($"User {q.Id} not found"))
            : Result<UserDto>.Success(UserMapping.ToDto(user));
    }
}

public sealed class CreateUserCommandHandler(
    IUserRepository repo, IUserPasswordManagementService passwordManagement
) : IRequestHandler<CreateUserCommand, Result<UserDto>>
{
    public async Task<Result<UserDto>> Handle(CreateUserCommand cmd, CancellationToken ct)
    {
        if (await repo.ExistsByLoginAsync(cmd.Login))
            return Result<UserDto>.Failure(Error.Conflict($"Login '{cmd.Login}' already exists"));
        if (await repo.ExistsByEmailAsync(cmd.Email))
            return Result<UserDto>.Failure(Error.Conflict($"Email '{cmd.Email}' already exists"));

        var user = new User
        {
            Id = Guid.NewGuid(),
            Login = cmd.Login,
            Email = cmd.Email,
            FullName = cmd.FullName,
            MustChangePassword = false,
            IsActive = true,
            // Antes o valor era fixo em true e o campo enviado pelo console web era
            // silenciosamente ignorado (UI exibia um valor diferente do gravado).
            MfaRequired = cmd.MfaRequired,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await repo.CreateAsync(user);

        // Apply password via password management service
        await passwordManagement.ResetPasswordAsync(created.Id, cmd.Password, "system", ct);

        return Result<UserDto>.Success(UserMapping.ToDto(created));
    }
}

public sealed class UpdateUserCommandHandler(
    IUserRepository repo
) : IRequestHandler<UpdateUserCommand, Result<UserDto>>
{
    public async Task<Result<UserDto>> Handle(UpdateUserCommand cmd, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(cmd.Id);
        if (user is null)
            return Result<UserDto>.Failure(Error.NotFound($"User {cmd.Id} not found"));

        // Unicidade: sem estas checagens um login/e-mail duplicado estourava a unique
        // index do banco e o cliente recebia 500 em vez de 409.
        if (cmd.Login is not null)
        {
            var login = cmd.Login.Trim();
            if (!string.Equals(user.Login, login, StringComparison.OrdinalIgnoreCase)
                && await repo.ExistsByLoginAsync(login))
                return Result<UserDto>.Failure(Error.Conflict($"Login '{login}' already exists"));

            user.Login = login;
        }

        if (cmd.Email is not null)
        {
            var email = cmd.Email.Trim();
            if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
                && await repo.ExistsByEmailAsync(email))
                return Result<UserDto>.Failure(Error.Conflict($"Email '{email}' already exists"));

            user.Email = email;
        }

        if (cmd.FullName is not null) user.FullName = cmd.FullName.Trim();
        if (cmd.IsActive.HasValue) user.IsActive = cmd.IsActive.Value;
        if (cmd.MfaRequired.HasValue) user.MfaRequired = cmd.MfaRequired.Value;
        user.UpdatedAt = DateTime.UtcNow;

        var updated = await repo.UpdateAsync(user);
        return Result<UserDto>.Success(UserMapping.ToDto(updated));
    }
}

public sealed class DeleteUserCommandHandler(
    IUserRepository repo
) : IRequestHandler<DeleteUserCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteUserCommand cmd, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(cmd.Id);
        if (user is null)
            return Result<VoidResult>.Failure(Error.NotFound($"User {cmd.Id} not found"));

        // Soft delete: deactivate
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;
        await repo.UpdateAsync(user);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

/// <summary>Mapeamento único de User para o DTO exposto pela API.</summary>
internal static class UserMapping
{
    internal static UserDto ToDto(User u) => new(
        u.Id, u.Login, u.Email, u.FullName,
        u.IsActive, u.MfaRequired, u.MfaConfigured,
        u.MustChangePassword, u.MustChangeProfile,
        u.CreatedAt, u.UpdatedAt, u.LastLoginAt);
}

public sealed class GetUserSecurityStateQueryHandler(
    IUserRepository repo
) : IRequestHandler<GetUserSecurityStateQuery, Result<UserSecurityStateDto>>
{
    public async Task<Result<UserSecurityStateDto>> Handle(GetUserSecurityStateQuery q, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(q.Id);
        if (user is null)
            return Result<UserSecurityStateDto>.Failure(Error.NotFound($"User {q.Id} not found"));

        return Result<UserSecurityStateDto>.Success(new UserSecurityStateDto
        {
            Id = user.Id,
            IsActive = user.IsActive,
            MfaRequired = user.MfaRequired,
            MfaConfigured = user.MfaConfigured,
            MustChangePassword = user.MustChangePassword,
            MustChangeProfile = user.MustChangeProfile,
            FailedLoginAttempts = user.FailedLoginAttempts,
            LockoutUntil = user.LockoutUntil,
            MfaFailedAttempts = user.MfaFailedAttempts,
            MfaLockoutUntil = user.MfaLockoutUntil,
            LastLoginAt = user.LastLoginAt
        });
    }
}

public sealed class UpdateMyProfileCommandHandler(
    IUserRepository repo
) : IRequestHandler<UpdateMyProfileCommand, Result<UserDto>>
{
    public async Task<Result<UserDto>> Handle(UpdateMyProfileCommand cmd, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(cmd.Id);
        if (user is null)
            return Result<UserDto>.Failure(Error.NotFound($"User {cmd.Id} not found"));

        var email = cmd.Email?.Trim() ?? string.Empty;
        var fullName = cmd.FullName?.Trim() ?? string.Empty;

        if (email.Length == 0 || fullName.Length == 0)
            return Result<UserDto>.Failure(Error.Validation("Email", "E-mail e nome completo são obrigatórios."));

        // ExistsByEmailAsync considera qualquer usuário (inclusive ele mesmo), então a
        // checagem só vale quando o e-mail realmente mudou.
        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
            && await repo.ExistsByEmailAsync(email))
            return Result<UserDto>.Failure(Error.Conflict($"Email '{email}' already exists"));

        user.Email = email;
        user.FullName = fullName;

        var updated = await repo.UpdateAsync(user);
        return Result<UserDto>.Success(UserMapping.ToDto(updated));
    }
}

public sealed class ForcePasswordChangeCommandHandler(
    IUserRepository repo,
    IUserSessionRepository sessions
) : IRequestHandler<ForcePasswordChangeCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ForcePasswordChangeCommand cmd, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(cmd.Id);
        if (user is null)
            return Result<VoidResult>.Failure(Error.NotFound($"User {cmd.Id} not found"));

        user.MustChangePassword = true;
        await repo.UpdateAsync(user);

        // Encerra as sessões ativas: sem isso o usuário continuaria navegando com a
        // senha antiga até o refresh token expirar.
        await sessions.RevokeAllByUserIdAsync(cmd.Id);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class GetMySecurityProfileQueryHandler(
    IUserRepository repo,
    IUserMfaKeyRepository mfaKeys,
    IUserAuthService auth
) : IRequestHandler<GetMySecurityProfileQuery, Result<MySecurityProfileDto>>
{
    public async Task<Result<MySecurityProfileDto>> Handle(GetMySecurityProfileQuery q, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(q.UserId);
        if (user is null)
            return Result<MySecurityProfileDto>.Failure(Error.NotFound("Usuário não encontrado."));

        var requirement = await auth.GetEffectiveMfaRequirementAsync(q.UserId);

        var keys = (await mfaKeys.GetActiveByUserIdAsync(q.UserId))
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new MyMfaKeySummaryDto
            {
                Id = k.Id,
                Name = k.Name,
                KeyType = k.KeyType,
                IsActive = k.IsActive,
                CreatedAt = k.CreatedAt,
                LastUsedAt = k.LastUsedAt
            })
            .ToList();

        var configuredForRequiredMethod = requirement switch
        {
            RoleMfaRequirement.Totp => keys.Any(k => k.KeyType == MfaKeyType.Totp),
            RoleMfaRequirement.Fido2 => keys.Any(k => k.KeyType == MfaKeyType.Fido2),
            _ => user.MfaConfigured || keys.Count > 0
        };

        return Result<MySecurityProfileDto>.Success(new MySecurityProfileDto
        {
            MfaRequired = user.MfaRequired || requirement != RoleMfaRequirement.None,
            MfaConfigured = configuredForRequiredMethod,
            RoleMfaRequirement = requirement,
            Keys = keys
        });
    }
}

public sealed class ListUserMfaKeysQueryHandler(
    IUserMfaKeyRepository mfaKeys,
    IUserRepository users
) : IRequestHandler<ListUserMfaKeysQuery, Result<IReadOnlyList<AdminUserMfaKeyDto>>>
{
    public async Task<Result<IReadOnlyList<AdminUserMfaKeyDto>>> Handle(ListUserMfaKeysQuery q, CancellationToken ct)
    {
        if (await users.GetByIdAsync(q.UserId) is null)
            return Result<IReadOnlyList<AdminUserMfaKeyDto>>.Failure(Error.NotFound($"User {q.UserId} not found"));

        // AdminUserMfaKeyDto é usado (e não o MfaKeyDto do fluxo próprio) porque o
        // console web espera keyType como string ("Fido2"/"Totp").
        var items = (await mfaKeys.GetActiveByUserIdAsync(q.UserId))
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new AdminUserMfaKeyDto
            {
                Id = k.Id,
                Name = k.Name,
                KeyType = k.KeyType,
                CreatedAt = k.CreatedAt,
                LastUsedAt = k.LastUsedAt
            })
            .ToList()
            .AsReadOnly();

        return Result<IReadOnlyList<AdminUserMfaKeyDto>>.Success(items);
    }
}

public sealed class RevokeUserMfaCommandHandler(
    IUserRepository repo,
    IUserMfaKeyRepository mfaKeys
) : IRequestHandler<RevokeUserMfaCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RevokeUserMfaCommand cmd, CancellationToken ct)
    {
        var user = await repo.GetByIdAsync(cmd.Id);
        if (user is null)
            return Result<VoidResult>.Failure(Error.NotFound($"User {cmd.Id} not found"));

        await mfaKeys.DeactivateAllByUserIdAsync(cmd.Id);
        // Sem MfaConfigured=false o próximo login não emitiria mfa_setup token e o
        // usuário ficaria preso sem conseguir autenticar.
        await repo.SetMfaConfiguredAsync(cmd.Id, false);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class RevokeUserMfaKeyCommandHandler(
    IUserRepository repo,
    IUserMfaKeyRepository mfaKeys
) : IRequestHandler<RevokeUserMfaKeyCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RevokeUserMfaKeyCommand cmd, CancellationToken ct)
    {
        var deactivated = await mfaKeys.DeactivateAsync(cmd.KeyId, cmd.Id);
        if (!deactivated)
            return Result<VoidResult>.Failure(Error.NotFound("Chave MFA não encontrada para este usuário."));

        if (await mfaKeys.CountActiveByUserIdAsync(cmd.Id) == 0)
            await repo.SetMfaConfiguredAsync(cmd.Id, false);

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
