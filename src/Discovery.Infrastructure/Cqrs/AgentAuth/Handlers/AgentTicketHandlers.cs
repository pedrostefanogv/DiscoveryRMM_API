using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Tickets;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetMyTicketsHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<GetMyTicketsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetMyTicketsQuery q, CancellationToken ct)
    {
        var tickets = await ticketRepo.GetByAgentIdAsync(q.AgentId, q.WorkflowStateId);
        return Result<object>.Success(tickets);
    }
}

public sealed class GetMyTicketHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<GetMyTicketQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetMyTicketQuery q, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(q.TicketId);
        if (ticket is null)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        return Result<object>.Success(ticket);
    }
}

public sealed class CreateMyTicketHandler(
    ITicketRepository ticketRepo,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo
) : IRequestHandler<CreateMyTicketCommand, Result<object>>
{
    public async Task<Result<object>> Handle(CreateMyTicketCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<object>.Failure(Error.NotFound("Site not found for agent."));

        var ticket = new Ticket
        {
            Title = cmd.Title,
            Description = cmd.Description ?? string.Empty,
            ClientId = site.ClientId,
            AgentId = cmd.AgentId,
            SiteId = agent.SiteId,
            DepartmentId = cmd.DepartmentId,
            WorkflowProfileId = cmd.WorkflowProfileId,
            Category = cmd.Category,
            Priority = Enum.TryParse<Core.Enums.TicketPriority>(cmd.Priority, ignoreCase: true, out var prio) ? prio : Core.Enums.TicketPriority.Medium
        };

        var created = await ticketRepo.CreateAsync(ticket);
        return Result<object>.Success(created);
    }
}

public sealed class AddMyTicketCommentHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<AddMyTicketCommentCommand, Result<object>>
{
    public async Task<Result<object>> Handle(AddMyTicketCommentCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (ticket is null)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        var comment = new TicketComment
        {
            TicketId = cmd.TicketId,
            Content = cmd.Content,
            IsInternal = cmd.IsInternal ?? false,
            Author = "Agent"
        };

        var added = await ticketRepo.AddCommentAsync(comment);
        return Result<object>.Success(added);
    }
}

public sealed class GetMyTicketCommentsHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<GetMyTicketCommentsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetMyTicketCommentsQuery q, CancellationToken ct)
    {
        var comments = await ticketRepo.GetCommentsAsync(q.TicketId);
        return Result<object>.Success(comments);
    }
}

public sealed class UpdateMyTicketWorkflowStateHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<UpdateMyTicketWorkflowStateCommand, Result<object>>
{
    public async Task<Result<object>> Handle(UpdateMyTicketWorkflowStateCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (ticket is null)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        await ticketRepo.UpdateWorkflowStateAsync(cmd.TicketId, cmd.WorkflowStateId);
        return Result<object>.Success(new { ticketId = cmd.TicketId, workflowStateId = cmd.WorkflowStateId });
    }
}

public sealed class CloseAndRateMyTicketHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<CloseAndRateMyTicketCommand, Result<object>>
{
    public async Task<Result<object>> Handle(CloseAndRateMyTicketCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (ticket is null)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        ticket.Rating = cmd.Rating;
        ticket.ClosedAt = DateTime.UtcNow;
        await ticketRepo.UpdateAsync(ticket);

        return Result<object>.Success(new { ticketId = cmd.TicketId, closed = true });
    }
}