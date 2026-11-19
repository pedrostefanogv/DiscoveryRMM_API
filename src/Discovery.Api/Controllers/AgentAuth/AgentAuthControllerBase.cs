using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-auth")]
[AllowAnonymous]
public abstract class AgentAuthControllerBase : ControllerBase
{
    protected readonly IMediator _mediator;
    protected readonly IAgentRepository _agentRepo;

    protected AgentAuthControllerBase(IMediator mediator, IAgentRepository agentRepo)
    {
        _mediator = mediator;
        _agentRepo = agentRepo;
    }

    protected bool TryGetAgentId(out Guid agentId)
    {
        if (HttpContext.Items["AgentId"] is Guid id) { agentId = id; return true; }
        agentId = Guid.Empty; return false;
    }

    protected async Task<(Agent? agent, IActionResult? blocked)> GetAgentOrBlockAsync(Guid agentId, bool allowPending)
    {
        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return (null, NotFound(new { error = "Agent not found." }));
        if (!allowPending && agent.ZeroTouchPending) return (null, StatusCode(403, new { error = "Agent registration is pending (zero-touch)." }));
        return (agent, null);
    }

    protected IActionResult MapResult<T>(Result<T> result, Func<T, IActionResult> onSuccess)
        where T : notnull
        => result.Match(onSuccess, errors => errors[0].Code switch
        {
            "NotFound" => NotFound(new { error = errors[0].Message }),
            "Forbidden" => StatusCode(403, new { error = errors[0].Message }),
            "Validation" => BadRequest(new { error = errors[0].Message, field = errors[0].Field }),
            "Internal" => StatusCode(500, new { error = "Erro interno do servidor" }),
            _ => BadRequest(new { error = errors[0].Message })
        });
}
