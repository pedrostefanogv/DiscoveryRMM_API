using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Knowledge;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetKnowledgeArticlesHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IKnowledgeArticleRepository knowledgeRepo
) : IRequestHandler<GetKnowledgeArticlesQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetKnowledgeArticlesQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var articles = await knowledgeRepo.ListByScopeAsync(
            clientId: site?.ClientId,
            siteId: agent.SiteId,
            status: "Published",
            departmentId: null,
            category: q.Category,
            ct: ct);

        return Result<object>.Success(articles);
    }
}

public sealed class GetKnowledgeArticleHandler(
    IKnowledgeArticleRepository knowledgeRepo
) : IRequestHandler<GetKnowledgeArticleQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetKnowledgeArticleQuery q, CancellationToken ct)
    {
        var article = await knowledgeRepo.GetByIdAsync(q.ArticleId, ct);
        if (article is null)
            return Result<object>.Failure(Error.NotFound("Knowledge article not found."));

        return Result<object>.Success(article);
    }
}