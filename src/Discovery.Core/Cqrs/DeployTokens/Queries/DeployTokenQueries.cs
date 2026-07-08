using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.DeployTokens.Commands;

namespace Discovery.Core.Cqrs.DeployTokens.Queries;

public sealed record ListDeployTokensQuery(Guid ClientId, Guid SiteId) : IQuery<Result<IReadOnlyList<DeployTokenDto>>>;