using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Interfaces;
using MediatR;
using TicketCommandService = Discovery.Infrastructure.Services.TicketCommandService;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

public sealed class CreateTicketCommandHandler(
    ITicketCommandService ticketCommandService
) : IRequestHandler<CreateTicketCommand, Result<TicketDetailDto>>
{
    public async Task<Result<TicketDetailDto>> Handle(CreateTicketCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketCommandService.CreateTicketAsync(
            cmd.Title, cmd.Description, cmd.Priority,
            cmd.ClientId, cmd.SiteId, cmd.AgentId, cmd.DepartmentId,
            cmd.WorkflowProfileId, cmd.AssignedToUserId, cmd.Category, ct);
        return Result<TicketDetailDto>.Success(TicketCommandService.ToDto(ticket));
    }
}

public sealed class UpdateTicketCommandHandler(
    ITicketCommandService ticketCommandService
) : IRequestHandler<UpdateTicketCommand, Result<TicketDetailDto>>
{
    public async Task<Result<TicketDetailDto>> Handle(UpdateTicketCommand cmd, CancellationToken ct)
    {
        try
        {
            var ticket = await ticketCommandService.UpdateTicketAsync(
                cmd.Id, cmd.Title, cmd.Description, cmd.Priority,
                cmd.DepartmentId, cmd.WorkflowProfileId, cmd.AssignedToUserId,
                cmd.Category, ct);
            return Result<TicketDetailDto>.Success(TicketCommandService.ToDto(ticket));
        }
        catch (KeyNotFoundException)
        {
            return Result<TicketDetailDto>.Failure(Error.NotFound($"Ticket {cmd.Id} not found"));
        }
    }
}

public sealed class TransitionTicketStateCommandHandler(
    ITicketWorkflowService workflow
) : IRequestHandler<TransitionTicketStateCommand, Result<TransitionTicketStateResult>>
{
    public async Task<Result<TransitionTicketStateResult>> Handle(TransitionTicketStateCommand cmd, CancellationToken ct)
    {
        var updated = await workflow.TransitionAsync(cmd.TicketId, cmd.TargetStateId, cmd.ChangedByUserId, ct);
        return Result<TransitionTicketStateResult>.Success(new TransitionTicketStateResult(updated.Id, updated.WorkflowStateId, updated.WorkflowStateId, updated.ClosedAt));
    }
}

public sealed class AddTicketCommentCommandHandler(
    ITicketCommandService ticketCommandService
) : IRequestHandler<AddTicketCommentCommand, Result<AddTicketCommentResult>>
{
    public async Task<Result<AddTicketCommentResult>> Handle(AddTicketCommentCommand cmd, CancellationToken ct)
    {
        try
        {
            var comment = await ticketCommandService.AddCommentAsync(
                cmd.TicketId, cmd.Content, cmd.IsInternal, cmd.UserId, cmd.UserName, ct);
            return Result<AddTicketCommentResult>.Success(new AddTicketCommentResult(comment.Id, comment.CreatedAt));
        }
        catch (KeyNotFoundException)
        {
            return Result<AddTicketCommentResult>.Failure(Error.NotFound($"Ticket {cmd.TicketId} not found"));
        }
    }
}

public sealed class AssignTicketCommandHandler(
    ITicketCommandService ticketCommandService
) : IRequestHandler<AssignTicketCommand, Result<AssignTicketResult>>
{
    public async Task<Result<AssignTicketResult>> Handle(AssignTicketCommand cmd, CancellationToken ct)
    {
        try
        {
            var ticket = await ticketCommandService.AssignTicketAsync(
                cmd.TicketId, cmd.AssignedToUserId, cmd.ChangedByUserId, ct);
            return Result<AssignTicketResult>.Success(new AssignTicketResult(cmd.TicketId, cmd.AssignedToUserId));
        }
        catch (KeyNotFoundException)
        {
            return Result<AssignTicketResult>.Failure(Error.NotFound($"Ticket {cmd.TicketId} not found"));
        }
    }
}
