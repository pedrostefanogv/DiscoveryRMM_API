using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Automation;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class SyncAutomationPolicyHandler() : IRequestHandler<SyncAutomationPolicyCommand, Result<object>>
{ public Task<Result<object>> Handle(SyncAutomationPolicyCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetAgentCommandsHandler() : IRequestHandler<GetAgentCommandsQuery, Result<object>>
{ public Task<Result<object>> Handle(GetAgentCommandsQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class AckAutomationExecutionHandler() : IRequestHandler<AckAutomationExecutionCommand, Result<VoidResult>>
{ public Task<Result<VoidResult>> Handle(AckAutomationExecutionCommand cmd, CancellationToken ct) => Task.FromResult(Result<VoidResult>.Success(VoidResult.Value)); }

public sealed class CompleteAutomationExecutionHandler() : IRequestHandler<CompleteAutomationExecutionCommand, Result<VoidResult>>
{ public Task<Result<VoidResult>> Handle(CompleteAutomationExecutionCommand cmd, CancellationToken ct) => Task.FromResult(Result<VoidResult>.Success(VoidResult.Value)); }