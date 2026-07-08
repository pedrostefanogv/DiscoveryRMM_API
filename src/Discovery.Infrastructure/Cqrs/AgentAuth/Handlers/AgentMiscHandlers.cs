using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetAgentIdentityHandler() : IRequestHandler<GetAgentIdentityQuery, Result<object>>
{ public Task<Result<object>> Handle(GetAgentIdentityQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetAppStoreEffectiveHandler() : IRequestHandler<GetAppStoreEffectiveQuery, Result<object>>
{ public Task<Result<object>> Handle(GetAppStoreEffectiveQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetRuntimeCustomFieldsHandler() : IRequestHandler<GetRuntimeCustomFieldsQuery, Result<object>>
{ public Task<Result<object>> Handle(GetRuntimeCustomFieldsQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class UpsertCollectedCustomFieldHandler() : IRequestHandler<UpsertCollectedCustomFieldCommand, Result<object>>
{ public Task<Result<object>> Handle(UpsertCollectedCustomFieldCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class IssueZeroTouchDeployTokenHandler() : IRequestHandler<IssueZeroTouchDeployTokenCommand, Result<object>>
{ public Task<Result<object>> Handle(IssueZeroTouchDeployTokenCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetAgentUpdateManifestHandler() : IRequestHandler<GetAgentUpdateManifestQuery, Result<object>>
{ public Task<Result<object>> Handle(GetAgentUpdateManifestQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class DownloadAgentUpdateHandler() : IRequestHandler<DownloadAgentUpdateQuery, Result<object>>
{ public Task<Result<object>> Handle(DownloadAgentUpdateQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class ReportAgentUpdateHandler() : IRequestHandler<ReportAgentUpdateCommand, Result<object>>
{ public Task<Result<object>> Handle(ReportAgentUpdateCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }