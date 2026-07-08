using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Software;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentSoftwareHandler() : IRequestHandler<GetAgentSoftwareQuery, Result<object>>
{ public Task<Result<object>> Handle(GetAgentSoftwareQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class ReportAgentSoftwareHandler() : IRequestHandler<ReportAgentSoftwareCommand, Result<VoidResult>>
{ public Task<Result<VoidResult>> Handle(ReportAgentSoftwareCommand cmd, CancellationToken ct) => Task.FromResult(Result<VoidResult>.Success(VoidResult.Value)); }