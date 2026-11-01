using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.AgentAuth.P2P;

public sealed record GetAgentP2pSeedPlanQuery(Guid AgentId) : IQuery<Result<object>>;

public sealed record IngestP2pTelemetryCommand(Guid AgentId, P2pTelemetryRequest Request) : ICommand<Result<List<P2pErrorDetail>>>;

public sealed record GetAgentP2pCloudBootstrapQuery(Guid AgentId, P2pCloudBootstrapRequest? Request) : IQuery<Result<P2pCloudBootstrapResponseDto>>;
