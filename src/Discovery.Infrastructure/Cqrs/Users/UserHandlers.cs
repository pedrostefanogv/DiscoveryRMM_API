using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Cqrs.Users.Queries;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using MediatR;

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
        var nextCursor = hasMore && items.Count > 0
            ? items[^1].Id.ToString()
            : null;

        return Result<UsersPageDto>.Success(new UsersPageDto(items, nextCursor, hasMore, count));
    }

    private static UserDto Map(User u) => new(u.Id, u.Login, u.Email, u.FullName,
        u.IsActive, u.MfaRequired, u.MfaConfigured,
        u.MustChangePassword, u.MustChangeProfile,
        u.FailedLoginAttempts, u.LockoutUntil,
        u.CreatedAt, u.UpdatedAt, u.LastLoginAt);
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
            : Result<UserDto>.Success(new UserDto(user.Id, user.Login, user.Email,
                user.FullName, user.IsActive, user.MfaRequired, user.MfaConfigured,
                user.MustChangePassword, user.MustChangeProfile,
                user.FailedLoginAttempts, user.LockoutUntil,
                user.CreatedAt, user.UpdatedAt, user.LastLoginAt));
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
            MfaRequired = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await repo.CreateAsync(user);

        // Apply password via password management service
        await passwordManagement.ResetPasswordAsync(created.Id, cmd.Password, "system", ct);

        return Result<UserDto>.Success(new UserDto(created.Id, created.Login, created.Email,
            created.FullName, created.IsActive, created.MfaRequired, created.MfaConfigured,
            created.MustChangePassword, created.MustChangeProfile,
            created.FailedLoginAttempts, created.LockoutUntil,
            created.CreatedAt, created.UpdatedAt, created.LastLoginAt));
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

        if (cmd.Login is not null) user.Login = cmd.Login;
        if (cmd.Email is not null) user.Email = cmd.Email;
        if (cmd.FullName is not null) user.FullName = cmd.FullName;
        if (cmd.IsActive.HasValue) user.IsActive = cmd.IsActive.Value;
        if (cmd.MfaRequired.HasValue) user.MfaRequired = cmd.MfaRequired.Value;
        user.UpdatedAt = DateTime.UtcNow;

        var updated = await repo.UpdateAsync(user);
        return Result<UserDto>.Success(new UserDto(updated.Id, updated.Login, updated.Email,
            updated.FullName, updated.IsActive, updated.MfaRequired, updated.MfaConfigured,
            updated.MustChangePassword, updated.MustChangeProfile,
            updated.FailedLoginAttempts, updated.LockoutUntil,
            updated.CreatedAt, updated.UpdatedAt, updated.LastLoginAt));
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
