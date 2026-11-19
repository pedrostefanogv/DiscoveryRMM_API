using Discovery.Api.Filters;
using Discovery.Core.Cqrs.AgentAuth.Tickets;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

public class AgentAuthTicketsController : AgentAuthControllerBase
{
    private readonly ISiteRepository _siteRepo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly IDepartmentCustomFieldService _departmentCustomFieldService;
    private readonly ITicketFormSchemaService _ticketFormSchemaService;

    public AgentAuthTicketsController(
        IMediator mediator,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IWorkflowRepository workflowRepo,
        IDepartmentRepository departmentRepo,
        IWorkflowProfileRepository workflowProfileRepo,
        IDepartmentCustomFieldService departmentCustomFieldService,
        ITicketFormSchemaService ticketFormSchemaService)
        : base(mediator, agentRepo)
    {
        _siteRepo = siteRepo;
        _workflowRepo = workflowRepo;
        _departmentRepo = departmentRepo;
        _workflowProfileRepo = workflowProfileRepo;
        _departmentCustomFieldService = departmentCustomFieldService;
        _ticketFormSchemaService = ticketFormSchemaService;
    }

    /// <summary>
    /// Payload de fechamento/avaliação. Aceita "feedback" (portal), "comment"
    /// (support do agente) e "notes" (versões antigas do agente Go).
    /// </summary>
    public record CloseAndRateTicketRequest(
        int? Rating,
        string? Feedback = null,
        string? Comment = null,
        string? Notes = null,
        Guid? WorkflowStateId = null);

    /// <summary>Payload de reabertura pelo agent (motivo opcional).</summary>
    public record ReopenMyTicketRequest(string? Reason = null);

    /// <summary>Payload de avaliação (CSAT) pelo agent.</summary>
    public record RateMyTicketRequest(int Rating, string? Feedback = null);

    [HttpGet("me/tickets")]
    public async Task<IActionResult> GetMyTickets([FromQuery] Guid? workflowStateId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketsQuery(id, workflowStateId)), Ok);
    }

    [HttpGet("me/tickets/{ticketId:guid}")]
    public async Task<IActionResult> GetMyTicket(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketQuery(id, ticketId)), Ok);
    }

    /// <summary>
    /// Campos personalizados do departamento do chamado com os valores gravados,
    /// para o detalhe do agent (somente leitura; campos internos ficam de fora).
    /// </summary>
    [HttpGet("me/tickets/{ticketId:guid}/fields")]
    public async Task<IActionResult> GetMyTicketFields(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketFieldsQuery(id, ticketId)), Ok);
    }

    /// <summary>
    /// Respostas do mini questionário do template do chamado (somente leitura).
    /// </summary>
    [HttpGet("me/tickets/{ticketId:guid}/answers")]
    public async Task<IActionResult> GetMyTicketAnswers(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketAnswersQuery(id, ticketId)), Ok);
    }

    [HttpGet("me/ticket-templates")]
    public async Task<IActionResult> GetMyTicketTemplates()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketTemplatesQuery(id)), Ok);
    }

    [HttpPost("me/tickets")]
    [IdempotencyFilter]
    public async Task<IActionResult> CreateMyTicket([FromBody] CreateMyTicketCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }), dto => CreatedAtAction(nameof(GetMyTicket), new { ticketId = (dto as dynamic)?.Id }, dto));
    }

    [HttpPost("me/tickets/{ticketId:guid}/comments")]
    [IdempotencyFilter]
    public async Task<IActionResult> AddMyTicketComment(Guid ticketId, [FromBody] AddMyTicketCommentCommand cmd)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, TicketId = ticketId }), Ok);
    }

    [HttpGet("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> GetMyTicketComments(Guid ticketId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetMyTicketCommentsQuery(id, ticketId)), Ok);
    }

    /// <summary>
    /// Lista os estados de workflow disponíveis para os tickets do agente.
    /// O seletor de estado final da UI de suporte do agente chama este endpoint.
    /// </summary>
    [HttpGet("me/tickets/workflow-states")]
    public async Task<IActionResult> GetMyTicketWorkflowStates()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        Guid? clientId = null;
        if (agent!.SiteId != Guid.Empty)
        {
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            clientId = site?.ClientId;
        }

        var states = await _workflowRepo.GetStatesAsync(clientId);
        return Ok(states.Select(s => new
        {
            s.Id,
            s.Name,
            s.Color,
            s.IsInitial,
            s.IsFinal,
            s.SortOrder
        }));
    }

    /// <summary>
    /// Opções para o formulário de abertura de chamado do agente (departamento e
    /// perfil de workflow), filtradas pelo cliente do site do agente. Alimenta o
    /// picker da UI de suporte; sem departamento o servidor não calcula SLA.
    /// </summary>
    [HttpGet("me/tickets/options")]
    public async Task<IActionResult> GetMyTicketOptions()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        Guid? clientId = null;
        if (agent!.SiteId != Guid.Empty)
        {
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            clientId = site?.ClientId;
        }

        var departments = clientId.HasValue
            ? await _departmentRepo.GetByClientAsync(clientId.Value, includeGlobal: true, activeOnly: true)
            : new List<Discovery.Core.Entities.Department>();
        var profiles = await _workflowProfileRepo.GetByClientAsync(clientId, includeGlobal: true);

        return Ok(new
        {
            departments = departments.Select(d => new { d.Id, d.Name }).ToList(),
            workflowProfiles = profiles
                .Where(p => p.IsActive)
                .Select(p => new { p.Id, p.Name, p.DepartmentId })
                .ToList()
        });
    }

    /// <summary>
    /// Campos personalizados públicos de um departamento, para o formulário de
    /// abertura do agent. Os campos do departamento valem para TODO chamado do
    /// departamento — com ou sem template — e são validados pelo servidor.
    /// </summary>
    [HttpGet("me/tickets/departments/{departmentId:guid}/fields")]
    public async Task<IActionResult> GetMyDepartmentFields(Guid departmentId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        Guid? clientId = null;
        if (agent!.SiteId != Guid.Empty)
        {
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            clientId = site?.ClientId;
        }

        // Escopo: departamento do próprio cliente ou global (mesma regra da
        // abertura). Não revela existência de departamentos de terceiros.
        var department = await _departmentRepo.GetByIdAsync(departmentId);
        if (department is null || (department.ClientId.HasValue && department.ClientId.Value != clientId))
            return NotFound(new { error = "Departamento não encontrado para este agente." });

        var fields = await _departmentCustomFieldService.GetPublicSchemaForDepartmentAsync(departmentId);
        return Ok(fields.Select(f => new
        {
            f.DefinitionId,
            f.Name,
            f.Label,
            f.Description,
            DataType = f.DataType.ToString(),
            f.IsRequired,
            f.Options,
            f.ValidationRegex,
            f.InputMask,
            f.MinLength,
            f.MaxLength,
            f.MinValue,
            f.MaxValue,
        }));
    }

    /// <summary>
    /// Schema do formulário de abertura de chamado do agent para um
    /// departamento (campos com opções: categoria e prioridade). O agent não tem
    /// lista fixa: renderiza exatamente o que este endpoint devolve, e as
    /// opções variam conforme os modelos e o histórico do departamento. Por isso
    /// o agent só chama este endpoint depois que o departamento é escolhido.
    /// </summary>
    [HttpGet("me/tickets/departments/{departmentId:guid}/form-schema")]
    public async Task<IActionResult> GetMyDepartmentFormSchema(Guid departmentId)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        Guid? clientId = null;
        if (agent!.SiteId != Guid.Empty)
        {
            var site = await _siteRepo.GetByIdAsync(agent.SiteId);
            clientId = site?.ClientId;
        }

        // Mesma regra de escopo dos campos do departamento: departamento do
        // próprio cliente ou global. Não revela existência de terceiros.
        var department = await _departmentRepo.GetByIdAsync(departmentId);
        if (department is null || (department.ClientId.HasValue && department.ClientId.Value != clientId))
            return NotFound(new { error = "Departamento não encontrado para este agente." });

        var schema = await _ticketFormSchemaService.GetForDepartmentAsync(departmentId, clientId);
        return Ok(schema);
    }

    [HttpPost("me/tickets/{ticketId:guid}/close")]
    [IdempotencyFilter]
    public async Task<IActionResult> CloseAndRateTicket(Guid ticketId, [FromBody] CloseAndRateTicketRequest req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var feedback = req.Feedback ?? req.Comment ?? req.Notes;
        var cmd = new CloseAndRateMyTicketCommand(id, ticketId, req.Rating, feedback, req.WorkflowStateId);
        return MapResult(await _mediator.Send(cmd), Ok);
    }

    /// <summary>
    /// Reabre um chamado encerrado vinculado ao agent. A UI usa para liberar
    /// novos comentários; a avaliação anterior é descartada pelo servidor.
    /// </summary>
    [HttpPost("me/tickets/{ticketId:guid}/reopen")]
    [IdempotencyFilter]
    public async Task<IActionResult> ReopenMyTicket(Guid ticketId, [FromBody] ReopenMyTicketRequest req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var cmd = new ReopenMyTicketCommand(id, ticketId, req?.Reason);
        return MapResult(await _mediator.Send(cmd), dto => Ok(dto));
    }

    /// <summary>
    /// Avalia (CSAT 1..5 + feedback) um chamado encerrado vinculado ao agent.
    /// </summary>
    [HttpPost("me/tickets/{ticketId:guid}/rating")]
    [IdempotencyFilter]
    public async Task<IActionResult> RateMyTicket(Guid ticketId, [FromBody] RateMyTicketRequest req)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        // RatedByName = hostname da máquina: a UI exibe "Avaliado por: <PC>".
        // req pode ser nulo se o corpo vier "null"; a validação de 1..5 fica no
        // handler do portal (0 cai em erro de validação, não em 500).
        var cmd = new RateMyTicketCommand(id, ticketId, req?.Rating ?? 0, req?.Feedback, agent?.Hostname);
        return MapResult(await _mediator.Send(cmd), dto => Ok(dto));
    }
}
