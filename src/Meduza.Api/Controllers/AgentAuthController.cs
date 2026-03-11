using System.Text.Json;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/agent-auth")]
public class AgentAuthController : ControllerBase
{
    private readonly IAgentRepository _agentRepo;
    private readonly IAgentHardwareRepository _hardwareRepo;
    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly IConfigurationResolver _configResolver;
    private readonly ITicketRepository _ticketRepo;
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IWorkflowProfileRepository _workflowProfileRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly ISlaService _slaService;
    private readonly IActivityLogService _activityLogService;
    private readonly IAiChatService _aiChatService;
    private readonly IAppStoreService _appStoreService;

    public AgentAuthController(
        IAgentRepository agentRepo,
        IAgentHardwareRepository hardwareRepo,
        IAgentSoftwareRepository softwareRepo,
        ICommandRepository commandRepo,
        IConfigurationResolver configResolver,
        ITicketRepository ticketRepo,
        IWorkflowRepository workflowRepo,
        IWorkflowProfileRepository workflowProfileRepo,
        ISiteRepository siteRepo,
        ISlaService slaService,
        IActivityLogService activityLogService,
        IAiChatService aiChatService,
        IAppStoreService appStoreService)
    {
        _agentRepo = agentRepo;
        _hardwareRepo = hardwareRepo;
        _softwareRepo = softwareRepo;
        _commandRepo = commandRepo;
        _configResolver = configResolver;
        _ticketRepo = ticketRepo;
        _workflowRepo = workflowRepo;
        _workflowProfileRepo = workflowProfileRepo;
        _siteRepo = siteRepo;
        _slaService = slaService;
        _activityLogService = activityLogService;
        _aiChatService = aiChatService;
        _appStoreService = appStoreService;
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound();

        // Buscar o Site para obter o ClientId
        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found for this agent." });

        // Retornar os dados do agent + clientId
        return Ok(new
        {
            agent.Id,
            agent.SiteId,
            ClientId = site.ClientId,
            agent.Hostname,
            agent.DisplayName,
            agent.Status,
            agent.OperatingSystem,
            agent.OsVersion,
            agent.AgentVersion,
            agent.LastIpAddress,
            agent.MacAddress,
            agent.LastSeenAt,
            agent.CreatedAt,
            agent.UpdatedAt
        });
    }

    /// <summary>
    /// Retorna a configuração efetiva do agent (hierarquia resolvida: Server → Client → Site).
    /// Usada pelo agent para saber seu intervalo de inventário, features habilitadas, etc.
    /// </summary>
    [HttpGet("me/configuration")]
    public async Task<IActionResult> GetConfiguration()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound();

        var resolved = await _configResolver.ResolveForSiteAsync(agent.SiteId);
        return Ok(resolved);
    }

    [HttpGet("me/app-store")]
    public async Task<IActionResult> GetAppStoreEffective(
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null) return NotFound();

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null) return NotFound(new { error = "Site not found for this agent." });

        var effective = await _appStoreService.GetEffectiveAppsAsync(
            site.ClientId,
            site.Id,
            agent.Id,
            installationType,
            cancellationToken);

        return Ok(new
        {
            installationType,
            count = effective.Count,
            items = effective
        });
    }

    [HttpGet("me/hardware")]
    public async Task<IActionResult> GetHardware()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var hardware = await _hardwareRepo.GetByAgentIdAsync(agentId);
        var disks = await _hardwareRepo.GetDisksAsync(agentId);
        var network = await _hardwareRepo.GetNetworkAdaptersAsync(agentId);
        var memory = await _hardwareRepo.GetMemoryModulesAsync(agentId);

        return Ok(new { Hardware = hardware, Disks = disks, NetworkAdapters = network, MemoryModules = memory });
    }

    [HttpPost("me/hardware")]
    public async Task<IActionResult> ReportHardwarePost([FromBody] HardwareReportRequest request)
        => await UpsertHardwareAsync(request);

    [HttpPut("me/hardware")]
    public async Task<IActionResult> ReportHardwarePut([FromBody] HardwareReportRequest request)
        => await UpsertHardwareAsync(request);

    private async Task<IActionResult> UpsertHardwareAsync(HardwareReportRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound();

        var hasAgentUpdate = request.Hostname is not null
            || request.DisplayName is not null
            || request.Status.HasValue
            || request.OperatingSystem is not null
            || request.OsVersion is not null
            || request.AgentVersion is not null
            || request.LastIpAddress is not null
            || request.MacAddress is not null;

        if (hasAgentUpdate)
        {
            if (request.Hostname is not null)
                agent.Hostname = request.Hostname;

            if (request.DisplayName is not null)
                agent.DisplayName = request.DisplayName;

            if (request.Status.HasValue)
                agent.Status = request.Status.Value;

            if (request.OperatingSystem is not null)
                agent.OperatingSystem = request.OperatingSystem;

            if (request.OsVersion is not null)
                agent.OsVersion = request.OsVersion;

            if (request.AgentVersion is not null)
                agent.AgentVersion = request.AgentVersion;

            if (request.LastIpAddress is not null)
                agent.LastIpAddress = request.LastIpAddress;

            if (request.MacAddress is not null)
                agent.MacAddress = request.MacAddress;

            await _agentRepo.UpdateAsync(agent);
        }

        string? inventoryRaw = null;
        if (request.InventoryRaw.HasValue && request.InventoryRaw.Value.ValueKind != JsonValueKind.Null)
            inventoryRaw = request.InventoryRaw.Value.GetRawText();

        var hasInventoryPayload = inventoryRaw is not null
            || request.InventorySchemaVersion is not null
            || request.InventoryCollectedAt.HasValue;

        if (request.Hardware is not null || hasInventoryPayload)
        {
            var hardware = request.Hardware ?? new AgentHardwareInfo { AgentId = agentId };
            hardware.AgentId = agentId;

            if (hasInventoryPayload)
            {
                hardware.InventoryRaw = inventoryRaw;
                hardware.InventorySchemaVersion = request.InventorySchemaVersion;
                hardware.InventoryCollectedAt = request.InventoryCollectedAt ?? DateTime.UtcNow;
            }
            else if (request.Hardware is not null)
            {
                var existing = await _hardwareRepo.GetByAgentIdAsync(agentId);
                if (existing is not null)
                {
                    hardware.InventoryRaw = existing.InventoryRaw;
                    hardware.InventorySchemaVersion = existing.InventorySchemaVersion;
                    hardware.InventoryCollectedAt = existing.InventoryCollectedAt;
                }
            }

            await _hardwareRepo.UpsertAsync(hardware);
        }

        if (request.Disks is not null)
            await _hardwareRepo.ReplaceDiskInfoAsync(agentId, request.Disks);

        if (request.NetworkAdapters is not null)
            await _hardwareRepo.ReplaceNetworkAdaptersAsync(agentId, request.NetworkAdapters);

        if (request.MemoryModules is not null)
            await _hardwareRepo.ReplaceMemoryModulesAsync(agentId, request.MemoryModules);

        return Ok();
    }

    [HttpGet("me/commands")]
    public async Task<IActionResult> GetCommands([FromQuery] int limit = 50)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var commands = await _commandRepo.GetByAgentIdAsync(agentId, limit);
        return Ok(commands);
    }

    [HttpGet("me/software")]
    public async Task<IActionResult> GetSoftwareInventory()
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var software = await _softwareRepo.GetCurrentByAgentIdAsync(agentId);
        return Ok(software);
    }

    [HttpPost("me/software")]
    public async Task<IActionResult> ReportSoftwarePost([FromBody] SoftwareInventoryReportRequest request)
        => await UpsertSoftwareInventoryAsync(request);

    [HttpPut("me/software")]
    public async Task<IActionResult> ReportSoftwarePut([FromBody] SoftwareInventoryReportRequest request)
        => await UpsertSoftwareInventoryAsync(request);

    private async Task<IActionResult> UpsertSoftwareInventoryAsync(SoftwareInventoryReportRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound();

        var collectedAt = request.CollectedAt ?? DateTime.UtcNow;
        var software = (request.Software ?? new List<SoftwareInventoryItemRequest>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new SoftwareInventoryEntry
            {
                Name = x.Name,
                Version = x.Version,
                Publisher = x.Publisher,
                InstallId = x.InstallId,
                Serial = x.Serial,
                Source = x.Source
            });

        await _softwareRepo.ReplaceInventoryAsync(agentId, collectedAt, software);
        return Ok(new { Message = "Software inventory updated." });
    }

    // === TICKETS ===

    /// <summary>
    /// Retorna todos os tickets associados a este agente.
    /// </summary>
    [HttpGet("me/tickets")]
    public async Task<IActionResult> GetMyTickets([FromQuery] Guid? workflowStateId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var tickets = await _ticketRepo.GetByAgentIdAsync(agentId, workflowStateId);
        
        // Enriquecer com informações do workflow state
        var ticketsWithState = new List<object>();
        foreach (var ticket in tickets)
        {
            var state = await _workflowRepo.GetStateByIdAsync(ticket.WorkflowStateId);
            ticketsWithState.Add(new
            {
                ticket.Id,
                ticket.ClientId,
                ticket.SiteId,
                ticket.AgentId,
                ticket.DepartmentId,
                ticket.WorkflowProfileId,
                ticket.Title,
                ticket.Description,
                ticket.Category,
                ticket.WorkflowStateId,
                WorkflowState = state != null ? new
                {
                    state.Id,
                    state.Name,
                    state.Color,
                    state.IsInitial,
                    state.IsFinal,
                    state.SortOrder
                } : null,
                ticket.Priority,
                ticket.AssignedToUserId,
                ticket.SlaExpiresAt,
                ticket.SlaBreached,
                ticket.Rating,
                ticket.RatedAt,
                ticket.RatedBy,
                ticket.CreatedAt,
                ticket.UpdatedAt,
                ticket.ClosedAt,
                ticket.DaysOpen
            });
        }
        
        return Ok(ticketsWithState);
    }

    /// <summary>
    /// Retorna um ticket específico se ele pertencer a este agente.
    /// </summary>
    [HttpGet("me/tickets/{ticketId:guid}")]
    public async Task<IActionResult> GetMyTicket(Guid ticketId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        // Enriquecer com informações do workflow state
        var state = await _workflowRepo.GetStateByIdAsync(ticket.WorkflowStateId);
        
        return Ok(new
        {
            ticket.Id,
            ticket.ClientId,
            ticket.SiteId,
            ticket.AgentId,
            ticket.DepartmentId,
            ticket.WorkflowProfileId,
            ticket.Title,
            ticket.Description,
            ticket.Category,
            ticket.WorkflowStateId,
            WorkflowState = state != null ? new
            {
                state.Id,
                state.Name,
                state.Color,
                state.IsInitial,
                state.IsFinal,
                state.SortOrder
            } : null,
            ticket.Priority,
            ticket.AssignedToUserId,
            ticket.SlaExpiresAt,
            ticket.SlaBreached,
            ticket.Rating,
            ticket.RatedAt,
            ticket.RatedBy,
            ticket.CreatedAt,
            ticket.UpdatedAt,
            ticket.ClosedAt,
            ticket.DaysOpen
        });
    }

    /// <summary>
    /// Cria um novo ticket para este agente.
    /// O agente é automaticamente associado ao ticket.
    /// </summary>
    [HttpPost("me/tickets")]
    public async Task<IActionResult> CreateMyTicket([FromBody] AgentCreateTicketRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
            return NotFound(new { error = "Agent not found." });

        // Buscar o site para obter o ClientId
        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return BadRequest(new { error = "Site not found for this agent." });

        // Buscar estado inicial do workflow para o client do agente
        var initialState = await _workflowRepo.GetInitialStateAsync(site.ClientId);
        if (initialState is null)
            return BadRequest(new { error = "No initial workflow state configured for this client." });

        // Calcular SLA se houver workflow profile
        WorkflowProfile? workflowProfile = null;
        DateTime? slaExpiresAt = null;

        if (request.WorkflowProfileId.HasValue)
        {
            workflowProfile = await _workflowProfileRepo.GetByIdAsync(request.WorkflowProfileId.Value);
            if (workflowProfile is null)
                return BadRequest(new { error = "Workflow profile not found." });
        }
        else if (request.DepartmentId.HasValue)
        {
            // Pegar profile padrão do departamento se não especificado
            workflowProfile = await _workflowProfileRepo.GetDefaultByDepartmentAsync(request.DepartmentId.Value);
        }

        if (workflowProfile != null)
        {
            slaExpiresAt = await _slaService.CalculateSlaExpiryAsync(workflowProfile.Id, DateTime.UtcNow);
        }

        var ticket = new Ticket
        {
            ClientId = site.ClientId,
            SiteId = agent.SiteId,
            AgentId = agentId,
            DepartmentId = request.DepartmentId,
            WorkflowProfileId = request.WorkflowProfileId,
            Title = request.Title,
            Description = request.Description,
            Priority = request.Priority ?? (workflowProfile?.DefaultPriority ?? TicketPriority.Medium),
            Category = request.Category,
            WorkflowStateId = initialState.Id,
            SlaExpiresAt = slaExpiresAt
        };

        var created = await _ticketRepo.CreateAsync(ticket);

        // Log da criação
        await _activityLogService.LogActivityAsync(
            created.Id,
            TicketActivityType.Created,
            null,
            $"Agent {agent.Hostname}",
            initialState.Id.ToString(),
            "Ticket criado pelo agente"
        );

        return CreatedAtAction(nameof(GetMyTicket), new { ticketId = created.Id }, created);
    }

    /// <summary>
    /// Adiciona um comentário a um ticket do agente.
    /// </summary>
    [HttpPost("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> AddMyTicketComment(Guid ticketId, [FromBody] AgentAddCommentRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        var agent = await _agentRepo.GetByIdAsync(agentId);
        var comment = new TicketComment
        {
            TicketId = ticketId,
            Author = $"Agent: {agent?.Hostname ?? agentId.ToString()}",
            Content = request.Content,
            IsInternal = request.IsInternal ?? false
        };

        var created = await _ticketRepo.AddCommentAsync(comment);
        return Created($"api/agent-auth/me/tickets/{ticketId}/comments", created);
    }

    /// <summary>
    /// Lista os comentários de um ticket do agente.
    /// </summary>
    [HttpGet("me/tickets/{ticketId:guid}/comments")]
    public async Task<IActionResult> GetMyTicketComments(Guid ticketId)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        var comments = await _ticketRepo.GetCommentsAsync(ticketId);
        return Ok(comments);
    }

    /// <summary>
    /// Atualiza o estado de workflow de um ticket do agente.
    /// Útil para o agente "fechar" ou "resolver" um ticket automaticamente.
    /// </summary>
    [HttpPatch("me/tickets/{ticketId:guid}/workflow-state")]
    public async Task<IActionResult> UpdateMyTicketWorkflowState(Guid ticketId, [FromBody] AgentUpdateWorkflowStateRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        var agent = await _agentRepo.GetByIdAsync(agentId);

        // Validar se a transição é permitida
        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, request.WorkflowStateId, ticket.ClientId);
        if (!valid)
            return BadRequest(new { error = "Invalid workflow transition." });

        var oldStateId = ticket.WorkflowStateId;

        // Verificar se o novo estado é final (para setar ClosedAt)
        var newState = await _workflowRepo.GetStateByIdAsync(request.WorkflowStateId);
        if (newState?.IsFinal == true)
            ticket.ClosedAt = DateTime.UtcNow;

        await _ticketRepo.UpdateWorkflowStateAsync(ticketId, request.WorkflowStateId);

        // Log da mudança de estado
        await _activityLogService.LogActivityAsync(
            ticketId,
            TicketActivityType.StateChanged,
            null,
            oldStateId.ToString(),
            request.WorkflowStateId.ToString(),
            $"Alterado pelo agente {agent?.Hostname ?? agentId.ToString()}"
        );

        // Recarregar o ticket atualizado
        var updatedTicket = await _ticketRepo.GetByIdAsync(ticketId);

        return Ok(new { message = "Workflow state updated", ticket = updatedTicket });
    }

    /// <summary>
    /// Fecha um ticket e opcionalmente avalia de 0 a 5 estrelas.
    /// Move o ticket para um estado final (Closed ou Resolved).
    /// </summary>
    [HttpPost("me/tickets/{ticketId:guid}/close")]
    public async Task<IActionResult> CloseAndRateTicket(Guid ticketId, [FromBody] AgentCloseTicketRequest request)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null)
            return NotFound(new { error = "Ticket not found." });

        if (ticket.AgentId != agentId)
            return Forbid();

        var agent = await _agentRepo.GetByIdAsync(agentId);

        // Validar rating se fornecido
        if (request.Rating.HasValue && (request.Rating.Value < 0 || request.Rating.Value > 5))
            return BadRequest(new { error = "Rating must be between 0 and 5." });

        // Buscar um estado final para fechar o ticket
        Guid targetStateId;
        
        if (request.WorkflowStateId.HasValue)
        {
            // Usar o estado fornecido
            var targetState = await _workflowRepo.GetStateByIdAsync(request.WorkflowStateId.Value);
            if (targetState is null)
                return BadRequest(new { error = "Workflow state not found." });
            
            if (!targetState.IsFinal)
                return BadRequest(new { error = "Specified workflow state is not a final state." });
            
            targetStateId = request.WorkflowStateId.Value;
        }
        else
        {
            // Buscar estado "Closed" ou qualquer estado final
            var finalStates = await _workflowRepo.GetStatesAsync(ticket.ClientId);
            var closedState = finalStates.FirstOrDefault(s => s.IsFinal && s.Name.Contains("Closed", StringComparison.OrdinalIgnoreCase))
                           ?? finalStates.FirstOrDefault(s => s.IsFinal);
            
            if (closedState is null)
                return BadRequest(new { error = "No final workflow state available for this client." });
            
            targetStateId = closedState.Id;
        }

        // Validar se a transição é permitida
        var valid = await _workflowRepo.IsTransitionValidAsync(ticket.WorkflowStateId, targetStateId, ticket.ClientId);
        if (!valid)
            return BadRequest(new { error = "Invalid workflow transition to close ticket." });

        var oldStateId = ticket.WorkflowStateId;

        // Atualizar o ticket
        ticket.WorkflowStateId = targetStateId;
        ticket.ClosedAt = DateTime.UtcNow;
        
        if (request.Rating.HasValue)
        {
            ticket.Rating = request.Rating.Value;
            ticket.RatedAt = DateTime.UtcNow;
            ticket.RatedBy = $"Agent: {agent?.Hostname ?? agentId.ToString()}";
        }

        await _ticketRepo.UpdateAsync(ticket);

        // Log da mudança de estado
        await _activityLogService.LogActivityAsync(
            ticketId,
            TicketActivityType.StateChanged,
            null,
            oldStateId.ToString(),
            targetStateId.ToString(),
            request.Rating.HasValue 
                ? $"Ticket fechado pelo agente {agent?.Hostname ?? agentId.ToString()} com avaliação {request.Rating.Value}/5"
                : $"Ticket fechado pelo agente {agent?.Hostname ?? agentId.ToString()}"
        );

        // Adicionar comentário se fornecido
        if (!string.IsNullOrWhiteSpace(request.Comment))
        {
            var comment = new TicketComment
            {
                TicketId = ticketId,
                Author = $"Agent: {agent?.Hostname ?? agentId.ToString()}",
                Content = request.Comment,
                IsInternal = false
            };
            await _ticketRepo.AddCommentAsync(comment);
        }

        // Recarregar o ticket atualizado
        var updatedTicket = await _ticketRepo.GetByIdAsync(ticketId);

        return Ok(new 
        { 
            message = "Ticket closed successfully", 
            ticket = updatedTicket,
            rating = request.Rating
        });
    }

    /// <summary>
    /// Chat síncrono com IA (respostas curtas, < 5s)
    /// </summary>
    [HttpPost("me/ai-chat")]
    public async Task<IActionResult> ChatSync([FromBody] AgentChatRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        try
        {
            var response = await _aiChatService.ProcessSyncAsync(
                agentId, 
                request.Message, 
                request.SessionId, 
                ct);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(408, new { error = "Request timeout" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Internal error processing chat request" });
        }
    }

    /// <summary>
    /// Chat assíncrono com IA (respostas longas, processamento em background)
    /// </summary>
    [HttpPost("me/ai-chat/async")]
    public async Task<IActionResult> ChatAsync([FromBody] AgentChatAsyncRequest request, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        try
        {
            var jobId = await _aiChatService.ProcessAsyncAsync(
                agentId, 
                request.Message, 
                request.SessionId, 
                ct);
            return Accepted(new 
            { 
                jobId, 
                statusUrl = $"/api/agent-auth/me/ai-chat/jobs/{jobId}" 
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Internal error creating async chat job" });
        }
    }

    /// <summary>
    /// Consulta status de job assíncrono de chat
    /// </summary>
    [HttpGet("me/ai-chat/jobs/{jobId}")]
    public async Task<IActionResult> GetJobStatus(Guid jobId, CancellationToken ct)
    {
        if (!TryGetAuthenticatedAgentId(out var agentId))
            return Unauthorized(new { error = "Agent not authenticated." });

        try
        {
            var status = await _aiChatService.GetJobStatusAsync(jobId, agentId, ct);
            if (status == null)
                return NotFound(new { error = "Job not found or unauthorized" });
            
            return Ok(status);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Internal error retrieving job status" });
        }
    }

    private bool TryGetAuthenticatedAgentId(out Guid agentId)
    {
        agentId = Guid.Empty;

        if (!HttpContext.Items.TryGetValue("AgentId", out var value) || value is not Guid parsed)
            return false;

        agentId = parsed;
        return true;
    }
}

// === Agent-specific request DTOs ===

/// <summary>
/// Request para o agente criar um ticket.
/// ClientId, SiteId e AgentId são inferidos do agente autenticado.
/// </summary>
public record AgentCreateTicketRequest(
    string Title,
    string Description,
    Guid? DepartmentId = null,
    Guid? WorkflowProfileId = null,
    TicketPriority? Priority = null,
    string? Category = null);

/// <summary>
/// Request para o agente adicionar um comentário a um ticket.
/// </summary>
public record AgentAddCommentRequest(
    string Content,
    bool? IsInternal = null);

/// <summary>
/// Request para o agente atualizar o estado de workflow de um ticket.
/// </summary>
public record AgentUpdateWorkflowStateRequest(
    Guid WorkflowStateId);

/// <summary>
/// Request para o agente fechar um ticket e opcionalmente avaliar (0-5 estrelas).
/// </summary>
public record AgentCloseTicketRequest(
    int? Rating = null,
    string? Comment = null,
    Guid? WorkflowStateId = null);
