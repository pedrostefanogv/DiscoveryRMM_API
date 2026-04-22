using Discovery.Api.Services;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
namespace Discovery.Api.Controllers;

/// <summary>
/// Gerencia alertas PSADT enviados ao endpoint do agent.
/// Suporta dois tipos: Toast (fecha sozinho por timeout) e Modal (exige confirmação).
/// O escopo de entrega pode ser: Agent, Site, Client ou Label.
/// </summary>
[ApiController]
[Route("api/agent-alerts")]
public class AgentAlertsController : ControllerBase
{
    private readonly IAgentAlertService _alertService;
    private readonly AlertDispatchService _dispatchService;
    private readonly IAgentRepository _agentRepo;
    private readonly IClientRepository _clientRepo;
    private readonly IAgentLabelRepository _labelRepo;
    private readonly IAlertToTicketService _alertToTicketService;

    public AgentAlertsController(
        IAgentAlertService alertService,
        AlertDispatchService dispatchService,
        IAgentRepository agentRepo,
        IClientRepository clientRepo,
        IAgentLabelRepository labelRepo,
        IAlertToTicketService alertToTicketService)
    {
        _alertService = alertService;
        _dispatchService = dispatchService;
        _agentRepo = agentRepo;
        _clientRepo = clientRepo;
        _labelRepo = labelRepo;
        _alertToTicketService = alertToTicketService;
    }

    /// <summary>
    /// Lista alertas com filtros opcionais.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] AlertDefinitionStatus? status,
        [FromQuery] AlertScopeType? scopeType,
        [FromQuery] Guid? scopeClientId,
        [FromQuery] Guid? scopeSiteId,
        [FromQuery] Guid? scopeAgentId,
        [FromQuery] Guid? ticketId,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0)
    {
        var items = await _alertService.GetAllAsync(status, scopeType, scopeClientId, scopeSiteId, scopeAgentId, ticketId, limit, offset);
        return Ok(items);
    }

    /// <summary>
    /// Retorna um alerta por ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var alert = await _alertService.GetByIdAsync(id);
        return alert is null ? NotFound() : Ok(alert);
    }

    /// <summary>
    /// Cria um novo alerta PSADT.
    /// Se ScheduledAt for nulo, o alerta é despachado imediatamente.
    /// Se ScheduledAt for definido, o scheduler enviará no momento correto.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlertRequest request, CancellationToken cancellationToken)
    {
        if (!ValidateRequest(request, out var validationError))
            return BadRequest(new { error = validationError });

        var timeoutSeconds = ResolveTimeout(request);

        var alert = await _alertService.CreateAsync(new CreateAgentAlertRequest(
            Title: request.Title,
            Message: request.Message,
            AlertType: request.AlertType,
            TimeoutSeconds: timeoutSeconds,
            ActionsJson: request.ActionsJson,
            DefaultAction: request.DefaultAction,
            Icon: request.Icon ?? "info",
            ScopeType: request.ScopeType,
            ScopeAgentId: request.ScopeAgentId,
            ScopeSiteId: request.ScopeSiteId,
            ScopeClientId: request.ScopeClientId,
            ScopeLabelName: request.ScopeLabelName,
            ScheduledAt: request.ScheduledAt,
            ExpiresAt: request.ExpiresAt,
            TicketId: request.TicketId,
            CreatedBy: request.CreatedBy),
            cancellationToken);

        // Despacho imediato se não agendado
        if (!request.ScheduledAt.HasValue)
            await _dispatchService.DispatchAsync(alert, cancellationToken);

        var updated = await _alertService.GetByIdAsync(alert.Id);
        return CreatedAtAction(nameof(GetById), new { id = alert.Id }, updated);
    }

    /// <summary>
    /// Despacha manualmente um alerta existente (re-envio ou disparo de rascunho).
    /// </summary>
    [HttpPost("{id:guid}/dispatch")]
    public async Task<IActionResult> Dispatch(Guid id, CancellationToken cancellationToken)
    {
        var alert = await _alertService.GetByIdAsync(id);
        if (alert is null)
            return NotFound();

        if (alert.Status is AlertDefinitionStatus.Dispatched)
            return BadRequest(new { error = "Alerta já foi despachado." });

        if (alert.Status is AlertDefinitionStatus.Cancelled or AlertDefinitionStatus.Expired)
            return BadRequest(new { error = $"Alerta com status '{alert.Status}' não pode ser despachado." });

        await _dispatchService.DispatchAsync(alert, cancellationToken);
        var updated = await _alertService.GetByIdAsync(id);
        return Ok(updated);
    }

    /// <summary>
    /// Cria (ou retorna existente) um ticket vinculado a este alerta.
    /// O ClientId é obrigatório pois define o workflow de estado inicial.
    /// Se o alerta já tiver TicketId, retorna o ticket existente sem criar novo.
    /// </summary>
    [HttpPost("{id:guid}/create-ticket")]
    public async Task<IActionResult> CreateTicket(
        Guid id,
        [FromBody] CreateTicketFromAlertRequest request,
        CancellationToken cancellationToken)
    {
        var alert = await _alertService.GetByIdAsync(id);
        if (alert is null)
            return NotFound(new { error = "Alerta não encontrado." });

        if (request.ClientId == Guid.Empty)
            return BadRequest(new { error = "ClientId é obrigatório." });

        var ticket = await _alertToTicketService.CreateTicketFromAlertAsync(
            alert,
            request.ClientId,
            request.SiteId,
            request.AgentId ?? alert.ScopeAgentId,
            request.Priority ?? TicketPriority.Medium,
            cancellationToken);

        return Ok(new { ticketId = ticket.Id, ticket.Title, ticket.WorkflowStateId, alertId = id });
    }

    /// <summary>
    /// Cancela um alerta que ainda não foi despachado.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var cancelled = await _alertService.CancelAsync(id);
        if (!cancelled)
            return BadRequest(new { error = "Alerta não encontrado ou não pode ser cancelado (já despachado, expirado ou cancelado)." });

        return Ok(new { message = "Alerta cancelado com sucesso.", id });
    }

    /// <summary>
    /// Retorna as opções disponíveis para popular os seletores de escopo no frontend:
    /// lista de agents, sites, clients e labels distintas.
    /// </summary>
    [HttpGet("scope-options")]
    public async Task<IActionResult> GetScopeOptions()
    {
        var agents = (await _agentRepo.GetAllAsync())
            .Select(a => new { a.Id, label = a.DisplayName ?? a.Hostname, a.Hostname, a.Status })
            .OrderBy(a => a.label);

        var clients = (await _clientRepo.GetAllAsync())
            .Select(c => new { c.Id, c.Name })
            .OrderBy(c => c.Name);

        var labels = await _labelRepo.GetDistinctLabelsAsync();

        return Ok(new { agents, clients, labels });    }

    /// <summary>
    /// Disparo de teste: envia o alerta para um agent específico sem gravar no banco,
    /// permitindo validar a configuração antes do disparo real.
    /// O agent de destino é obrigatório (testAgentId).
    /// </summary>
    [HttpPost("test-dispatch")]
    public async Task<IActionResult> TestDispatch([FromBody] TestDispatchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { error = "Title é obrigatório." });

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Message é obrigatório." });

        if (request.TestAgentId == Guid.Empty)
            return BadRequest(new { error = "TestAgentId é obrigatório." });

        var allowed = new[] { 5, 15, 30 };
        var timeout = request.AlertType == PsadtAlertType.Modal ? (int?)null
            : (request.TimeoutSeconds.HasValue && allowed.Contains(request.TimeoutSeconds.Value)
                ? request.TimeoutSeconds.Value : 15);

        // Cria um AgentAlertDefinition temporário (não persiste no banco)
        var testAlert = new AgentAlertDefinition
        {
            Id = Guid.NewGuid(),
            Title = request.Title,
            Message = request.Message,
            AlertType = request.AlertType,
            TimeoutSeconds = timeout,
            ActionsJson = request.ActionsJson,
            DefaultAction = request.DefaultAction,
            Icon = request.Icon ?? "info",
            ScopeType = AlertScopeType.Agent,
            ScopeAgentId = request.TestAgentId,
            Status = AlertDefinitionStatus.Draft,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _dispatchService.DispatchAsync(testAlert, cancellationToken);

        return Ok(new { message = "Alerta de teste enviado.", testAgentId = request.TestAgentId, alertId = testAlert.Id });
    }

    // ── Validação / helpers ───────────────────────────────────────────────

    private static bool ValidateRequest(CreateAlertRequest request, out string error)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            error = "Title é obrigatório.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            error = "Message é obrigatório.";
            return false;
        }

        switch (request.ScopeType)
        {
            case AlertScopeType.Agent when !request.ScopeAgentId.HasValue:
                error = "ScopeAgentId é obrigatório quando ScopeType = Agent.";
                return false;
            case AlertScopeType.Site when !request.ScopeSiteId.HasValue:
                error = "ScopeSiteId é obrigatório quando ScopeType = Site.";
                return false;
            case AlertScopeType.Client when !request.ScopeClientId.HasValue:
                error = "ScopeClientId é obrigatório quando ScopeType = Client.";
                return false;
            case AlertScopeType.Label when string.IsNullOrWhiteSpace(request.ScopeLabelName):
                error = "ScopeLabelName é obrigatório quando ScopeType = Label.";
                return false;
        }

        error = string.Empty;
        return true;
    }

    private static int? ResolveTimeout(CreateAlertRequest request)
    {
        if (request.AlertType == PsadtAlertType.Modal)
            return null;

        // Toast: aceitar valores comuns; forçar padrão=15 se não informado ou inválido
        var allowed = new[] { 5, 15, 30 };
        if (request.TimeoutSeconds.HasValue && allowed.Contains(request.TimeoutSeconds.Value))
            return request.TimeoutSeconds.Value;

        return 15;
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────

public record CreateAlertRequest(
    string Title,
    string Message,
    PsadtAlertType AlertType,
    int? TimeoutSeconds,
    string? ActionsJson,
    string? DefaultAction,
    string? Icon,
    AlertScopeType ScopeType,
    Guid? ScopeAgentId,
    Guid? ScopeSiteId,
    Guid? ScopeClientId,
    string? ScopeLabelName,
    DateTime? ScheduledAt,
    DateTime? ExpiresAt,
    Guid? TicketId,
    string? CreatedBy);

public record TestDispatchRequest(
    Guid TestAgentId,
    string Title,
    string Message,
    PsadtAlertType AlertType,
    int? TimeoutSeconds,
    string? ActionsJson,
    string? DefaultAction,
    string? Icon);

public record CreateTicketFromAlertRequest(
    Guid ClientId,
    Guid? SiteId,
    Guid? AgentId,
    TicketPriority? Priority);
