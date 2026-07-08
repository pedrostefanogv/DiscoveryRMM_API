using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.MeshCentral;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class CreateMeshCentralEmbedUrlHandler : IRequestHandler<CreateMeshCentralEmbedUrlCommand, Result<object>>
{ public Task<Result<object>> Handle(CreateMeshCentralEmbedUrlCommand cmd, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }

public sealed class GetMeshCentralInstallHandler : IRequestHandler<GetMeshCentralInstallQuery, Result<object>>
{ public Task<Result<object>> Handle(GetMeshCentralInstallQuery q, CancellationToken ct) => Task.FromResult(Result<object>.Success(null!)); }