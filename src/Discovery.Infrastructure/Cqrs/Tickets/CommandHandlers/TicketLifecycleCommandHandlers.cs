using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Tickets.CommandHandlers;

public sealed class ReopenTicketCommandHandler(
    ITicketRepository ticketRepo,
    IWorkflowRepository workflowRepo,
    ISlaService slaService,
    IActivityLogService activityLog,
    INotificationService notification,
    ILogger<ReopenTicketCommandHandler> logger
) : IRequestHandler<ReopenTicketCommand, Result<TicketDetailDto>>
{
    public async Task<Result<TicketDetailDto>> Handle(ReopenTicketCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (ticket is null || ticket.DeletedAt != null)
            return Result<TicketDetailDto>.Failure(Error.NotFound($"Ticket {cmd.TicketId} not found"));

        var states = (await workflowRepo.GetStatesAsync(ticket.ClientId)).ToList();
        var current = states.FirstOrDefault(s => s.Id == ticket.WorkflowStateId);
        if (current is not null && !current.IsFinal)
            return Result<TicketDetailDto>.Failure(
                Error.Validation("TicketId", "Somente chamados encerrados podem ser reabertos."));

        var initial = states.Where(s => s.IsInitial).OrderBy(s => s.SortOrder).FirstOrDefault()
            ?? await workflowRepo.GetInitialStateAsync(ticket.ClientId);
        if (initial is null)
            return Result<TicketDetailDto>.Failure(
                Error.Validation("WorkflowState", "Nenhum estado inicial configurado para o workflow do cliente."));

        var reopenedAt = DateTime.UtcNow;
        ticket.WorkflowStateId = initial.Id;
        ticket.ClosedAt = null;
        ticket.SlaBreached = false;
        ticket.SlaHoldStartedAt = null;
        ticket.SlaPausedSeconds = 0;
        ticket.UpdatedAt = reopenedAt;

        // Reabrir invalida a avaliação anterior (CSAT do fechamento antigo).
        ticket.Rating = null;
        ticket.RatingFeedback = null;
        ticket.RatedAt = null;
        ticket.RatedBy = null;

        if (ticket.WorkflowProfileId.HasValue)
        {
            try
            {
                ticket.SlaExpiresAt = await slaService.CalculateSlaExpiryAsync(ticket.WorkflowProfileId.Value, reopenedAt);
                ticket.SlaFirstResponseExpiresAt = await slaService.CalculateFirstResponseExpiryAsync(ticket.WorkflowProfileId.Value, reopenedAt);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Falha ao recalcular SLA ao reabrir o ticket {TicketId}", ticket.Id);
            }
        }

        await ticketRepo.UpdateAsync(ticket);
        await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.Reopened, cmd.ChangedByUserId,
            current?.Id.ToString(), initial.Id.ToString(), cmd.Reason ?? "Chamado reaberto.");

        if (ticket.AssignedToUserId.HasValue)
        {
            await notification.PublishAsync(new NotificationPublishRequest(
                EventType: "ticket.reopened",
                Topic: "tickets",
                Title: "Chamado reaberto",
                Message: $"O chamado '{ticket.Title}' foi reaberto.",
                Severity: NotificationSeverity.Warning,
                Payload: new { ticketId = ticket.Id },
                RecipientUserId: ticket.AssignedToUserId), ct);
        }

        logger.LogInformation("Ticket {TicketId} reopened into state {StateId}", ticket.Id, initial.Id);
        return Result<TicketDetailDto>.Success(TicketCommandService.ToDto(ticket));
    }
}

public sealed class RateTicketCommandHandler(
    ITicketRepository ticketRepo,
    IWorkflowRepository workflowRepo,
    IActivityLogService activityLog,
    ILogger<RateTicketCommandHandler> logger
) : IRequestHandler<RateTicketCommand, Result<TicketDetailDto>>
{
    public async Task<Result<TicketDetailDto>> Handle(RateTicketCommand cmd, CancellationToken ct)
    {
        if (cmd.Rating is < 1 or > 5)
            return Result<TicketDetailDto>.Failure(
                Error.Validation("Rating", "A avaliação deve estar entre 1 e 5."));

        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (ticket is null || ticket.DeletedAt != null)
            return Result<TicketDetailDto>.Failure(Error.NotFound($"Ticket {cmd.TicketId} not found"));

        var states = (await workflowRepo.GetStatesAsync(ticket.ClientId)).ToList();
        var current = states.FirstOrDefault(s => s.Id == ticket.WorkflowStateId);
        var isClosed = ticket.ClosedAt.HasValue || current?.IsFinal == true;
        if (!isClosed)
            return Result<TicketDetailDto>.Failure(
                Error.Validation("TicketId", "Só é possível avaliar chamados encerrados."));

        ticket.Rating = cmd.Rating;
        ticket.RatingFeedback = cmd.Feedback;
        ticket.RatedAt = DateTime.UtcNow;
        ticket.RatedBy = cmd.RatedByName;
        ticket.UpdatedAt = DateTime.UtcNow;

        await ticketRepo.UpdateAsync(ticket);
        await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.Rated, cmd.RatedByUserId,
            null, cmd.Rating.ToString(),
            string.IsNullOrWhiteSpace(cmd.Feedback) ? "Chamado avaliado." : $"Chamado avaliado: {cmd.Feedback}");

        logger.LogInformation("Ticket {TicketId} rated {Rating}", ticket.Id, cmd.Rating);
        return Result<TicketDetailDto>.Success(TicketCommandService.ToDto(ticket));
    }
}
