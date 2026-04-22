using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Discovery.Api.Controllers;

/// <summary>
/// Campos customizáveis vinculados a um ticket específico.
/// Reutiliza a infraestrutura de custom fields com ScopeType=Ticket.
/// </summary>
[ApiController]
[Route("api/tickets/{ticketId:guid}/custom-fields")]
public class TicketCustomFieldsController : ControllerBase
{
    private readonly ITicketRepository _ticketRepo;
    private readonly ICustomFieldService _customFieldService;

    public TicketCustomFieldsController(
        ITicketRepository ticketRepo,
        ICustomFieldService customFieldService)
    {
        _ticketRepo = ticketRepo;
        _customFieldService = customFieldService;
    }

    /// <summary>Retorna todos os valores de campos customizados do ticket.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var values = await _customFieldService.GetValuesAsync(
            CustomFieldScopeType.Ticket, ticketId, includeSecrets: false, cancellationToken);

        return Ok(values);
    }

    /// <summary>Cria ou atualiza o valor de um campo customizado do ticket.</summary>
    [HttpPut("{definitionId:guid}")]
    public async Task<IActionResult> Upsert(
        Guid ticketId,
        Guid definitionId,
        [FromBody] JsonElement value,
        CancellationToken cancellationToken)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        var result = await _customFieldService.UpsertValueAsync(
            new UpsertCustomFieldValueInput(
                DefinitionId: definitionId,
                ScopeType: CustomFieldScopeType.Ticket,
                EntityId: ticketId,
                ValueJson: value.GetRawText(),
                UpdatedBy: User.Identity?.Name),
            cancellationToken);

        return Ok(result);
    }
}
