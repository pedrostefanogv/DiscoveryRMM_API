using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using TicketCommandService = Discovery.Infrastructure.Services.TicketCommandService;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

public sealed class CreateTicketCommandHandler(
    ITicketCommandService ticketCommandService,
    ITicketSubmissionService ticketSubmissionService,
    IDepartmentCustomFieldService departmentCustomFieldService
) : IRequestHandler<CreateTicketCommand, Result<TicketDetailDto>>
{
    public async Task<Result<TicketDetailDto>> Handle(CreateTicketCommand cmd, CancellationToken ct)
    {
        // Template (opcional) + validação dos campos personalizados + snapshot.
        var submission = await ticketSubmissionService.PrepareAsync(
            new TicketSubmissionRequest(
                cmd.ClientId, cmd.DepartmentId, cmd.TemplateId,
                cmd.Title, cmd.Description, cmd.Category, cmd.Priority.ToString(),
                cmd.CustomFieldValues),
            ct);

        if (!submission.IsValid)
        {
            return Result<TicketDetailDto>.Failure(
                submission.Errors
                    .Select(e => Error.Validation(e.FieldName, e.ErrorMessage))
                    .ToList());
        }

        var priority = Enum.TryParse<TicketPriority>(submission.Priority, ignoreCase: true, out var parsed)
            ? parsed
            : cmd.Priority;

        try
        {
            var ticket = await ticketCommandService.CreateTicketAsync(
                submission.Title, submission.Description, priority,
                cmd.ClientId, cmd.SiteId, cmd.AgentId, submission.DepartmentId,
                cmd.WorkflowProfileId, cmd.AssignedToUserId, submission.Category, ct,
                submission.SnapshotMarkdown);

            if (submission.CustomFieldValues.Count > 0 && submission.DepartmentId.HasValue)
            {
                await departmentCustomFieldService.SaveTicketFieldValuesAsync(
                    ticket.Id, submission.DepartmentId.Value,
                    submission.CustomFieldValues, updatedBy: null, ct);
            }

            return Result<TicketDetailDto>.Success(TicketCommandService.ToDto(ticket));
        }
        catch (InvalidOperationException ex)
        {
            // B7: workflow sem estado inicial vira 400 com mensagem, não 500 nem ticket órfão.
            return Result<TicketDetailDto>.Failure(Error.Validation("WorkflowState", ex.Message));
        }
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
                cmd.Category, cmd.ClearDepartment, cmd.ClearWorkflowProfile, ct);
            return Result<TicketDetailDto>.Success(TicketCommandService.ToDto(ticket));
        }
        catch (KeyNotFoundException)
        {
            return Result<TicketDetailDto>.Failure(Error.NotFound($"Ticket {cmd.Id} not found"));
        }
    }
}

public sealed class TransitionTicketStateCommandHandler(
    ITicketWorkflowService workflow,
    ITicketRepository ticketRepo
) : IRequestHandler<TransitionTicketStateCommand, Result<TransitionTicketStateResult>>
{
    public async Task<Result<TransitionTicketStateResult>> Handle(TransitionTicketStateCommand cmd, CancellationToken ct)
    {
        // Captura o estado anterior ANTES da transição: o handler devolvia o
        // novo estado nas duas posições (Previous == New).
        var before = await ticketRepo.GetByIdAsync(cmd.TicketId);
        var previousStateId = before?.WorkflowStateId ?? Guid.Empty;

        var updated = await workflow.TransitionAsync(cmd.TicketId, cmd.TargetStateId, cmd.ChangedByUserId, ct);
        return Result<TransitionTicketStateResult>.Success(new TransitionTicketStateResult(updated.Id, previousStateId, updated.WorkflowStateId, updated.ClosedAt));
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
