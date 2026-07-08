using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Auth.Queries;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Auth.QueryHandlers;

public sealed class LoginQueryHandler(
    IUserAuthService userAuthService
) : IRequestHandler<LoginQuery, Result<LoginResultDto>>
{
    public async Task<Result<LoginResultDto>> Handle(LoginQuery q, CancellationToken ct)
    {
        try
        {
            var response = await userAuthService.LoginAsync(q.Email, q.Password, q.IpAddress, q.UserAgent);
            return Result<LoginResultDto>.Success(new LoginResultDto(
                Guid.Empty, response.AccessToken ?? string.Empty, response.RefreshToken ?? string.Empty,
                response.MfaToken, response.MfaRequired, DateTime.UtcNow.AddSeconds(response.ExpiresInSeconds ?? 0)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<LoginResultDto>.Failure(Error.Unauthorized(ex.Message));
        }
    }
}

public sealed class RefreshTokenQueryHandler(
    IUserAuthService userAuthService
) : IRequestHandler<RefreshTokenQuery, Result<RefreshResultDto>>
{
    public async Task<Result<RefreshResultDto>> Handle(RefreshTokenQuery q, CancellationToken ct)
    {
        try
        {
            var pair = await userAuthService.RefreshAsync(q.RefreshToken);
            return Result<RefreshResultDto>.Success(new RefreshResultDto(pair.AccessToken, pair.RefreshToken, DateTime.UtcNow.AddSeconds(pair.ExpiresInSeconds)));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<RefreshResultDto>.Failure(Error.Unauthorized(ex.Message));
        }
    }
}

public sealed class ListUsersQueryHandler(
    IUserRepository userRepo
) : IRequestHandler<ListUsersQuery, Result<ListUsersResult>>
{
    public async Task<Result<ListUsersResult>> Handle(ListUsersQuery q, CancellationToken ct)
    {
        // Usamos GetAllPageAsync sem cursor para obter todos os usuários.
        // O repositório será migrado para suporte nativo a busca por termo no futuro.
#pragma warning disable CS0618 // Obsoleto — caminho de migração conhecido
        var users = await userRepo.GetAllAsync();
#pragma warning restore CS0618
        var filtered = users.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(q.SearchTerm))
        {
            var s = q.SearchTerm.ToLower();
            filtered = filtered.Where(u =>
                (u.Email?.ToLower().Contains(s) ?? false) ||
                (u.FullName?.ToLower().Contains(s) ?? false) ||
                (u.Login?.ToLower().Contains(s) ?? false));
        }

        var dtos = filtered.Take(q.Limit).Select(u => new UserDto(
            u.Id, u.Email, u.FullName, string.Empty, u.IsActive, u.CreatedAt
        )).ToList() as IReadOnlyList<UserDto>;

        return Result<ListUsersResult>.Success(new ListUsersResult(dtos, null, false));
    }
}