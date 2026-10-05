using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Tickets;
using Discovery.Core.Cqrs.Tickets.Commands;
using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

public sealed class GetMyTicketsHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<GetMyTicketsQuery, Result<IReadOnlyList<AgentTicketDto>>>
{
    public async Task<Result<IReadOnlyList<AgentTicketDto>>> Handle(GetMyTicketsQuery q, CancellationToken ct)
    {
        var tickets = await ticketRepo.GetByAgentIdAsync(q.AgentId, q.WorkflowStateId);
        var dtos = (tickets ?? []).Select(ticket => ticket.ToAgentTicketDto()).ToList();
        return Result<IReadOnlyList<AgentTicketDto>>.Success(dtos);
    }
}

public sealed class GetMyTicketHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<GetMyTicketQuery, Result<AgentTicketDto>>
{
    public async Task<Result<AgentTicketDto>> Handle(GetMyTicketQuery q, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(q.TicketId);
        // Isolamento por agente: sem isso, qualquer agente autenticado leria
        // qualquer ticket por GUID (IDOR). Não revela existência de terceiros.
        if (ticket is null || ticket.AgentId != q.AgentId)
            return Result<AgentTicketDto>.Failure(Error.NotFound("Ticket not found."));

        return Result<AgentTicketDto>.Success(ticket.ToAgentTicketDto());
    }
}

/// <summary>
/// Campos personalizados do departamento vinculados ao chamado, com os valores
/// já gravados. Campos internos não são expostos ao agent.
/// </summary>
public sealed class GetMyTicketFieldsHandler(
    ITicketRepository ticketRepo,
    IDepartmentCustomFieldService departmentCustomFieldService
) : IRequestHandler<GetMyTicketFieldsQuery, Result<IReadOnlyList<AgentTicketFieldDto>>>
{
    public async Task<Result<IReadOnlyList<AgentTicketFieldDto>>> Handle(GetMyTicketFieldsQuery q, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(q.TicketId);
        if (ticket is null || ticket.AgentId != q.AgentId)
            return Result<IReadOnlyList<AgentTicketFieldDto>>.Failure(Error.NotFound("Ticket not found."));

        if (ticket.DepartmentId is null)
            return Result<IReadOnlyList<AgentTicketFieldDto>>.Success([]);

        var schema = await departmentCustomFieldService
            .GetFullSchemaForDepartmentAsync(ticket.DepartmentId.Value, ticket.Id, ct);

        var dtos = schema
            .Where(field => !field.IsInternal)
            .Select(field => new AgentTicketFieldDto(
                field.DefinitionId,
                field.Label,
                field.DataType.ToString(),
                field.IsRequired,
                field.CurrentValueJson))
            .ToList();

        return Result<IReadOnlyList<AgentTicketFieldDto>>.Success(dtos);
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
                // Defaults dos campos do departamento (definitionId -> valor):
                // permitem pré-preencher o formulário antes do envio.
                template.CustomFieldDefaultsJson,
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
                    // Limites: necessários para validar no cliente do agent.
                    q.MinLength,
                    q.MaxLength,
                    q.MinValue,
                    q.MaxValue,
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
    IWorkflowRepository workflowRepo,
    ILogger<AddMyTicketCommentHandler> logger
) : IRequestHandler<AddMyTicketCommentCommand, Result<AgentTicketCommentDto>>
{
    public async Task<Result<AgentTicketCommentDto>> Handle(AddMyTicketCommentCommand cmd, CancellationToken ct)
    {
        var owned = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (owned is null || owned.AgentId != cmd.AgentId)
            return Result<AgentTicketCommentDto>.Failure(Error.NotFound("Ticket not found."));

        // Chamado encerrado não aceita novos comentários: a UI orienta a reabrir.
        // ClosedAt cobre o fluxo normal (fechamento pelo portal/agent/mesclagem);
        // a consulta de estados só é necessária para o caso legado de chamado em
        // estado final sem ClosedAt — evitando um round-trip extra por comentário.
        var closedMessage = "Chamado encerrado. Reabra o chamado para comentar.";
        if (owned.ClosedAt.HasValue)
            return Result<AgentTicketCommentDto>.Failure(Error.Validation("TicketId", closedMessage));

        var states = await workflowRepo.GetStatesAsync(owned.ClientId);
        if (states.Any(s => s.Id == owned.WorkflowStateId && s.IsFinal))
            return Result<AgentTicketCommentDto>.Failure(Error.Validation("TicketId", closedMessage));

        try
        {
            // Usa ITicketCommandService para consistência com o fluxo web UI (activity logging incluso).
            // Opção de produto (a): o agente NUNCA cria nota interna — é ferramenta
            // do técnico no portal. O parâmetro fica no command apenas por contrato.
            var comment = await ticketCommandService.AddCommentAsync(
                cmd.TicketId, cmd.Content, false,
                userId: null, userName: "Agent", ct);

            return Result<AgentTicketCommentDto>.Success(
                new AgentTicketCommentDto(comment.Id, comment.Author, comment.Content, comment.CreatedAt));
        }
        catch (KeyNotFoundException)
        {
            return Result<AgentTicketCommentDto>.Failure(Error.NotFound("Ticket not found."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add agent comment on ticket {TicketId}", cmd.TicketId);
            return Result<AgentTicketCommentDto>.Failure(Error.Internal("Failed to add comment."));
        }
    }
}

public sealed class GetMyTicketCommentsHandler(
    ITicketRepository ticketRepo
) : IRequestHandler<GetMyTicketCommentsQuery, Result<IReadOnlyList<AgentTicketCommentDto>>>
{
    public async Task<Result<IReadOnlyList<AgentTicketCommentDto>>> Handle(GetMyTicketCommentsQuery q, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(q.TicketId);
        if (ticket is null || ticket.AgentId != q.AgentId)
            return Result<IReadOnlyList<AgentTicketCommentDto>>.Failure(Error.NotFound("Ticket not found."));

        var comments = await ticketRepo.GetCommentsAsync(q.TicketId);
        // Opção de produto (a): notas internas não vazam para o agente. O DTO
        // público não expõe IsInternal/TicketId.
        var visible = (comments ?? Enumerable.Empty<Discovery.Core.Entities.TicketComment>())
            .Where(comment => !comment.IsInternal)
            .Select(comment => new AgentTicketCommentDto(
                comment.Id, comment.Author, comment.Content, comment.CreatedAt))
            .ToList();
        return Result<IReadOnlyList<AgentTicketCommentDto>>.Success(visible);
    }
}

/// <summary>
/// Respostas do mini questionário do template com os valores gravados. Somente
/// leitura; embedding e ValueJson não são expostos.
/// </summary>
public sealed class GetMyTicketAnswersHandler(
    ITicketRepository ticketRepo,
    DiscoveryDbContext db
) : IRequestHandler<GetMyTicketAnswersQuery, Result<IReadOnlyList<AgentTicketAnswerDto>>>
{
    public async Task<Result<IReadOnlyList<AgentTicketAnswerDto>>> Handle(GetMyTicketAnswersQuery q, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(q.TicketId);
        if (ticket is null || ticket.AgentId != q.AgentId)
            return Result<IReadOnlyList<AgentTicketAnswerDto>>.Failure(Error.NotFound("Ticket not found."));

        var answers = await db.TicketAnswers.AsNoTracking()
            .Where(a => a.TicketId == q.TicketId)
            .OrderBy(a => a.SortOrder)
            .Select(a => new AgentTicketAnswerDto(
                a.Id, a.QuestionKey, a.QuestionLabel, a.ValueText, a.CreatedAt))
            .ToListAsync(ct);

        return Result<IReadOnlyList<AgentTicketAnswerDto>>.Success(answers);
    }
}

/// <summary>
/// Reabertura pelo agent: valida a posse e delega para o handler do portal,
/// que volta ao estado inicial, limpa ClosedAt/avaliação e recalcula SLA/FRT.
/// </summary>
/// <remarks>
/// A delegação é feita chamando o handler concreto (não via <c>ISender</c>):
/// despachar outro <c>ICommand</c> pelo mediator abriria uma SEGUNDA transação
/// dentro da transação do <c>TransactionBehavior</c> do comando externo
/// (EF Core não permite transação aninhada na mesma conexão). Assim, o comando
/// externo permanece dono da única transação e o handler do portal continua
/// sendo a fonte única da regra de reabertura.
/// </remarks>
public sealed class ReopenMyTicketHandler(
    ITicketRepository ticketRepo,
    IRequestHandler<ReopenTicketCommand, Result<TicketDetailDto>> reopenHandler
) : IRequestHandler<ReopenMyTicketCommand, Result<TicketDetailDto>>
{
    public async Task<Result<TicketDetailDto>> Handle(ReopenMyTicketCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        // Isolamento por agent: sem isso qualquer agent autenticado reabriria
        // chamados de terceiros conhecendo o GUID (IDOR).
        if (ticket is null || ticket.AgentId != cmd.AgentId)
            return Result<TicketDetailDto>.Failure(Error.NotFound("Ticket not found."));

        return await reopenHandler.Handle(new ReopenTicketCommand(cmd.TicketId, cmd.Reason, null), ct);
    }
}

/// <summary>
/// Avaliação (CSAT) pelo agent: valida a posse e delega para o handler do
/// portal (exige chamado encerrado e nota 1..5). Ver observação em
/// <see cref="ReopenMyTicketHandler"/> sobre a chamada direta ao handler.
/// </summary>
public sealed class RateMyTicketHandler(
    ITicketRepository ticketRepo,
    IRequestHandler<RateTicketCommand, Result<TicketDetailDto>> rateHandler
) : IRequestHandler<RateMyTicketCommand, Result<TicketDetailDto>>
{
    public async Task<Result<TicketDetailDto>> Handle(RateMyTicketCommand cmd, CancellationToken ct)
    {
        var ticket = await ticketRepo.GetByIdAsync(cmd.TicketId);
        if (ticket is null || ticket.AgentId != cmd.AgentId)
            return Result<TicketDetailDto>.Failure(Error.NotFound("Ticket not found."));

        return await rateHandler.Handle(
            new RateTicketCommand(cmd.TicketId, cmd.Rating, cmd.Feedback, null, cmd.RatedByName), ct);
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

        // Idempotente para reenvios: fecha de novo não reescreve a data original
        // de fechamento (que define a ordem "aguardando avaliação").
        ticket.ClosedAt ??= DateTime.UtcNow;
        // Fechamento pelo agente encerra o hold, acumulando a pausa corrente.
        SlaHold.Apply(ticket, ticket.SlaHoldStartedAt.HasValue, willBeOnHold: false, DateTime.UtcNow);
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