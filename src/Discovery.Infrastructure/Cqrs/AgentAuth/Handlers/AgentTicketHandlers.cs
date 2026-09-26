using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Tickets;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
        // Isolamento por agente: sem isso, qualquer agente autenticado leria
        // qualquer ticket por GUID (IDOR). Não revela existência de terceiros.
        if (ticket is null || ticket.AgentId != q.AgentId)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        return Result<object>.Success(ticket);
    }
}

public sealed class CreateMyTicketHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    ITicketCommandService ticketCommandService,
    ITicketSubmissionService ticketSubmissionService,
    IDepartmentCustomFieldService departmentCustomFieldService
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

        // Template (opcional) + validação dos campos personalizados + snapshot.
        var submission = await ticketSubmissionService.PrepareAsync(
            new TicketSubmissionRequest(
                site.ClientId, cmd.DepartmentId, cmd.TemplateId,
                cmd.Title, cmd.Description, cmd.Category, cmd.Priority,
                cmd.CustomFieldValues, cmd.TemplateAnswers),
            ct);

        if (!submission.IsValid)
        {
            return Result<object>.Failure(
                submission.Errors
                    .Select(e => Error.Validation(e.FieldName, e.ErrorMessage))
                    .ToList());
        }

        // Defesa em profundidade: o agent Go já valida, mas o endpoint é
        // autenticado por agent e pode receber payloads arbitrários.
        if (string.IsNullOrWhiteSpace(submission.Title))
            return Result<object>.Failure(Error.Validation("Title", "Title é obrigatório."));
        if (submission.Title.Length > 200)
            return Result<object>.Failure(Error.Validation("Title", "Title excede 200 caracteres."));
        if (submission.Description.Length > 8000)
            return Result<object>.Failure(Error.Validation("Description", "Description excede 8000 caracteres."));

        var priority = Enum.TryParse<Core.Enums.TicketPriority>(submission.Priority, ignoreCase: true, out var prio)
            ? prio
            : Core.Enums.TicketPriority.Medium;

        // Reutiliza o fluxo canônico: estado inicial do workflow, SLA/FRT,
        // activity log e evento de criação (antes o create do agente nascia
        // sem estado e sem SLA).
        var created = await ticketCommandService.CreateTicketAsync(
            submission.Title,
            submission.Description,
            priority,
            site.ClientId,
            agent.SiteId,
            cmd.AgentId,
            submission.DepartmentId,
            cmd.WorkflowProfileId,
            assignedToUserId: null,
            category: submission.Category,
            ct,
            submission.SnapshotMarkdown,
            submission.TemplateId,
            submission.TemplateName);

        if (submission.CustomFieldValues.Count > 0 && submission.DepartmentId.HasValue)
        {
            await departmentCustomFieldService.SaveTicketFieldValuesAsync(
                created.Id, submission.DepartmentId.Value,
                submission.CustomFieldValues, updatedBy: "agent", ct);
        }

        if (submission.Answers is { Count: > 0 })
        {
            await ticketSubmissionService.SaveTemplateAnswersAsync(
                created.Id, submission.TemplateId, submission.Answers, ct);
        }

        return Result<object>.Success(created);
    }
}

/// <summary>
/// Lista os templates de chamado aplicáveis ao agente (global + cliente) já com
/// o schema público do departamento, para o agent de chat renderizar o formulário.
/// </summary>
public sealed class GetMyTicketTemplatesHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IDepartmentCustomFieldService departmentCustomFieldService,
    DiscoveryDbContext db
) : IRequestHandler<GetMyTicketTemplatesQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetMyTicketTemplatesQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<object>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<object>.Failure(Error.NotFound("Site not found for agent."));

        var templates = await db.TicketTemplates
            .AsNoTracking()
            // Excluídos (lixeira) não podem ser ofertados ao agent/chat.
            .Where(t => t.DeletedAt == null && t.IsActive && (t.ClientId == site.ClientId || t.ClientId == null))
            // Título é o nome exibido ao usuário (a chave é identificador).
            .OrderBy(t => t.Title)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

        var result = new List<object>(templates.Count);
        foreach (var template in templates)
        {
            IReadOnlyList<DepartmentFieldSchemaItemDto> fields = template.DepartmentId.HasValue
                ? await departmentCustomFieldService.GetPublicSchemaForDepartmentAsync(template.DepartmentId.Value, ct)
                : Array.Empty<DepartmentFieldSchemaItemDto>();

            var questions = Discovery.Core.DTOs.TicketTemplateQuestions.Parse(template.QuestionsJson);

            result.Add(new
            {
                template.Id,
                template.Name,
                template.Title,
                template.Description,
                Priority = template.Priority?.ToString(),
                template.Category,
                template.DepartmentId,
                // Mini questionário do modelo (o que o usuário deve responder).
                Questions = questions.Select(q => new
                {
                    q.Key,
                    q.Label,
                    DataType = q.DataType.ToString(),
                    q.IsRequired,
                    q.Options,
                    q.ValidationRegex,
                    q.InputMask,
                    q.HelpText,
                }).ToList(),
                // Campos do departamento (sempre presentes no chamado, com a
                // obrigatoriedade configurada em cada campo).
                Fields = fields.Select(f => new
                {
                    DefinitionId = f.DefinitionId,
                    f.Name,
                    f.Label,
                    DataType = f.DataType.ToString(),
                    f.IsRequired,
                    f.Options,
                    f.ValidationRegex,
                    f.InputMask,
                }).ToList(),
            });
        }

        return Result<object>.Success(result);
    }
}

public sealed class AddMyTicketCommentHandler(
    ITicketCommandService ticketCommandService,
    ITicketRepository ticketRepo,
    ILogger<AddMyTicketCommentHandler> logger
) : IRequestHandler<AddMyTicketCommentCommand, Result<object>>
{
    public async Task<Result<object>> Handle(AddMyTicketCommentCommand cmd, CancellationToken ct)
    {
        var owned = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (owned is null || owned.AgentId != cmd.AgentId)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        try
        {
            // Usa ITicketCommandService para consistência com o fluxo web UI (activity logging incluso).
            // Opção de produto (a): o agente NUNCA cria nota interna — é ferramenta
            // do técnico no portal. O parâmetro fica no command apenas por contrato.
            var comment = await ticketCommandService.AddCommentAsync(
                cmd.TicketId, cmd.Content, false,
                userId: null, userName: "Agent", ct);

            return Result<object>.Success(comment);
        }
        catch (KeyNotFoundException)
        {
            return Result<object>.Failure(Error.NotFound("Ticket not found."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add agent comment on ticket {TicketId}", cmd.TicketId);
            return Result<object>.Failure(Error.Internal("Failed to add comment."));
        }
    }
}

public sealed class GetMyTicketCommentsHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<GetMyTicketCommentsQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetMyTicketCommentsQuery q, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(q.TicketId);
        if (ticket is null || ticket.AgentId != q.AgentId)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        var comments = await ticketRepo.GetCommentsAsync(q.TicketId);
        // Opção de produto (a): notas internas não vazam para o agente.
        var visible = (comments ?? Enumerable.Empty<Discovery.Core.Entities.TicketComment>())
            .Where(comment => !comment.IsInternal);
        return Result<object>.Success(visible);
    }
}

public sealed class UpdateMyTicketWorkflowStateHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<UpdateMyTicketWorkflowStateCommand, Result<object>>
{
    public async Task<Result<object>> Handle(UpdateMyTicketWorkflowStateCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (ticket is null || ticket.AgentId != cmd.AgentId)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        await ticketRepo.UpdateWorkflowStateAsync(cmd.TicketId, cmd.WorkflowStateId);
        return Result<object>.Success(new { ticketId = cmd.TicketId, workflowStateId = cmd.WorkflowStateId });
    }
}

public sealed class CloseAndRateMyTicketHandler(
    ITicketRepository ticketRepo,
    IWorkflowRepository workflowRepo,
    IActivityLogService activityLog
) : IRequestHandler<CloseAndRateMyTicketCommand, Result<object>>
{
    public async Task<Result<object>> Handle(CloseAndRateMyTicketCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (ticket is null || ticket.AgentId != cmd.AgentId)
            return Result<object>.Failure(Error.NotFound("Ticket not found."));

        // Validação da nota (CSAT): 1..5 quando informada.
        if (cmd.Rating.HasValue && (cmd.Rating.Value < 1 || cmd.Rating.Value > 5))
            return Result<object>.Failure(Error.Validation("Rating", "A nota deve estar entre 1 e 5."));

        var oldStateId = ticket.WorkflowStateId;

        // Estado final: usa o informado (se válido para o cliente) ou o primeiro
        // estado marcado como final no workflow do cliente.
        var states = (await workflowRepo.GetStatesAsync(ticket.ClientId)).ToList();
        Guid? targetStateId = null;
        // Só aceita um estado FINAL informado; caso contrário usa o final do fluxo.
        if (cmd.WorkflowStateId.HasValue && states.Any(s => s.Id == cmd.WorkflowStateId.Value && s.IsFinal))
            targetStateId = cmd.WorkflowStateId.Value;
        else
            targetStateId = states.FirstOrDefault(s => s.IsFinal)?.Id;

        if (cmd.Rating.HasValue)
        {
            ticket.Rating = cmd.Rating;
            ticket.RatedAt = DateTime.UtcNow;
            ticket.RatedBy = cmd.AgentId.ToString();
        }
        if (!string.IsNullOrWhiteSpace(cmd.Feedback))
            ticket.RatingFeedback = cmd.Feedback.Trim();

        if (targetStateId.HasValue)
            ticket.WorkflowStateId = targetStateId.Value;

        ticket.ClosedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;
        await ticketRepo.UpdateAsync(ticket);

        if (targetStateId.HasValue && targetStateId.Value != oldStateId)
            await activityLog.LogStateChangeAsync(ticket.Id, null, oldStateId, targetStateId.Value);

        if (cmd.Rating.HasValue)
            await activityLog.LogActivityAsync(
                ticket.Id, TicketActivityType.Rated, null, null,
                cmd.Rating.Value.ToString(), cmd.Feedback);

        return Result<object>.Success(new
        {
            ticketId = ticket.Id,
            closed = true,
            workflowStateId = ticket.WorkflowStateId,
            rating = ticket.Rating
        });
    }
}