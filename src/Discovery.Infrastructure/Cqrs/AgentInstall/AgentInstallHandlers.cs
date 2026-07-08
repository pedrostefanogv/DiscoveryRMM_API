using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentInstall.Queries;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentInstall;

public sealed class GetAgentInstallUrlQueryHandler : IRequestHandler<GetAgentInstallUrlQuery, Result<AgentInstallDto>>
{
    public Task<Result<AgentInstallDto>> Handle(GetAgentInstallUrlQuery q, CancellationToken ct)
    {
        var url = $"/api/agent/download?clientId={q.ClientId}&siteId={q.SiteId}";
        return Task.FromResult(Result<AgentInstallDto>.Success(new AgentInstallDto(url, null, null)));
    }
}
