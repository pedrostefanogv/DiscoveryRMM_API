using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.MeshCentral.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.MeshCentral;

public sealed class GetMeshCentralStatusQueryHandler : IRequestHandler<GetMeshCentralStatusQuery, Result<MeshCentralStatusDto>>
{
    public Task<Result<MeshCentralStatusDto>> Handle(GetMeshCentralStatusQuery q, CancellationToken ct)
        => Task.FromResult(Result<MeshCentralStatusDto>.Success(new MeshCentralStatusDto(false, null, null)));
}