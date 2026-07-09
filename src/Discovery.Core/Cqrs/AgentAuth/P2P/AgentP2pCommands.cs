using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.AgentAuth.P2P;

public sealed record GetAgentP2pSeedPlanQuery(Guid AgentId) : IQuery<Result<object>>;
