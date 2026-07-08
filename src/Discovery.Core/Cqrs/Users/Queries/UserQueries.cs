using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Users.Commands;

namespace Discovery.Core.Cqrs.Users.Queries;

public sealed record ListUsersQuery(string? Cursor = null, int Limit = 50)
    : IQuery<Result<UsersPageDto>>;

public sealed record GetUserByIdQuery(Guid Id) : IQuery<Result<UserDto>>;

public sealed record UsersPageDto(
    IReadOnlyList<UserDto> Items, string? NextCursor, bool HasMore, int Total
);
