using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Configuration;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentConfigurationHandler : IRequestHandler<GetAgentConfigurationQuery, Result<object>>
{ public Task<Result<object>> Handle(GetAgentConfigurationQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class ReportAgentTlsMismatchHandler : IRequestHandler<ReportAgentTlsMismatchCommand, Result<object>>
{ public Task<Result<object>> Handle(ReportAgentTlsMismatchCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetAgentSyncManifestHandler : IRequestHandler<GetAgentSyncManifestQuery, Result<object>>
{ public Task<Result<object>> Handle(GetAgentSyncManifestQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }