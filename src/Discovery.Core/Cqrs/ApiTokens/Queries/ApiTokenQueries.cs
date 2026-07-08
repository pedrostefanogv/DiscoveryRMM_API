using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.ApiTokens.Commands;

namespace Discovery.Core.Cqrs.ApiTokens.Queries;

public sealed record ListApiTokensQuery(Guid UserId) : IQuery<Result<IReadOnlyList<ApiTokenDto>>>;