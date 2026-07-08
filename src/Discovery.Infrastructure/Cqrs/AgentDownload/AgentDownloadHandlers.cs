using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentDownload.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentDownload;

public sealed class GetAgentDownloadQueryHandler(IAgentUpdateService svc) : IRequestHandler<GetAgentDownloadQuery, Result<AgentDownloadDto>>
{
    public async Task<Result<AgentDownloadDto>> Handle(GetAgentDownloadQuery q, CancellationToken ct)
    {
        var build = await svc.GetCurrentBuildAsync(q.Platform, q.Architecture, null, ct);
        if (build is null) return Result<AgentDownloadDto>.Failure(Error.NotFound("No current build found"));
        return Result<AgentDownloadDto>.Success(new AgentDownloadDto($"/api/agent-updates/{build.Id}/download", build.Version, build.FileName, build.Sha256));
    }
}
