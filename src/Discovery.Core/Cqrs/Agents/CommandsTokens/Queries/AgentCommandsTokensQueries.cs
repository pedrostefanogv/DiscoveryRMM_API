using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Commands;

namespace Discovery.Core.Cqrs.Agents.CommandsTokens.Queries;

public sealed record GetAgentCommandsQuery(Guid AgentId, int Limit = 50) : IQuery<Result<IReadOnlyList<AgentCommandDto>>>;
public sealed record GetAgentTokensQuery(Guid AgentId) : IQuery<Result<IReadOnlyList<AgentTokenDto>>>;