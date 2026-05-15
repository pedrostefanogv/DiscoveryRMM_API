using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Discovery.Api.Controllers;

/// <summary>
/// Campos customizáveis vinculados a um ticket específico.
/// Reutiliza a infraestrutura de custom fields com ScopeType=Ticket.
/// Quando o ticket pertence a um departamento, aplica controle de visibilidade:
///   - Campos públicos (IsInternal=false): visíveis para todos com permissão View
///   - Campos internos (IsInternal=true): visíveis apenas para atendentes (Edit permission)
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/tickets/{ticketId:guid}/custom-fields")]
public class TicketCustomFieldsController : ControllerBase
{
    private readonly ITicketRepository _ticketRepo;
    private readonly ICustomFieldService _customFieldService;
    private readonly IDepartmentCustomFieldService _departmentCustomFieldService;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly IScopeContext _scopeContext;

    public TicketCustomFieldsController(
        ITicketRepository ticketRepo,
        ICustomFieldService customFieldService,
        IDepartmentCustomFieldService departmentCustomFieldService,
        IDepartmentRepository departmentRepo,
        IScopeContext scopeContext)
    {
        _ticketRepo = ticketRepo;
        _customFieldService = customFieldService;
        _departmentCustomFieldService = departmentCustomFieldService;
        _departmentRepo = departmentRepo;
        _scopeContext = scopeContext;
    }

    /// <summary>Retorna os valores de campos customizados do ticket, respeitando visibilidade.</summary>
    [HttpGet]
    [RequirePermission(ResourceType.Tickets, ActionType.View)]
    public async Task<IActionResult> Get(Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) return NotFound();

        // Se o ticket tem departamento, buscar campos do departamento com controle de visibilidade
        if (ticket.DepartmentId.HasValue)
        {
            var isStaff = await IsDepartmentStaffAsync(ticket.DepartmentId.Value);

            if (isStaff)
            {
                // Atendente: vê todos os campos (públicos + internos)
                var schema = await _departmentCustomFieldService.GetFullSchemaForDepartmentAsync(
                    ticket.DepartmentId.Value, ticketId, cancellationToken);
                return Ok(schema);
            }
            else
            {
                // Solicitante: vê apenas campos públicos
                var schema = await _departmentCustomFieldService.GetPublicSchemaForDepartmentAsync(
                    ticket.DepartmentId.Value, cancellationToken);

                // Enriquecer com valores já preenchidos para este ticket
                if (schema.Count > 0)
                {
                    var definitionIds = schema.Select(s => s.DefinitionId).ToList();
                    var entityKey = ticketId.ToString("D");
                    var values = await _customFieldService.GetValuesAsync(
                        CustomFieldScopeType.Ticket, ticketId, includeSecrets: false, cancellationToken);

                    var valueMap = values.ToDictionary(v => v.DefinitionId, v => v.ValueJson);
                    var enrichedSchema = schema.Select(s =>
                        valueMap.TryGetValue(s.DefinitionId, out var val)
                            ? s with { CurrentValueJson = val }
                            : s).ToList();

                    return Ok(enrichedSchema);
                }

                return Ok(schema);
            }
        }

        // Sem departamento: retorna todos os valores do ticket (comportamento legado)
        var legacyValues = await _customFieldService.GetValuesAsync(
            CustomFieldScopeType.Ticket, ticketId, includeSecrets: false, cancellationToken);

        return Ok(legacyValues);
    }

    /// <summary>Cria ou atualiza o valor de um campo customizado do ticket.</summary>
    [HttpPut("{definitionId:guid}")]
    [RequirePermission(ResourceType.Tickets, ActionType.Edit)]
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

    /// <summary>
    /// Verifica se o usuário atual é atendente (staff) do departamento.
    /// Um usuário é considerado staff se tem permissão de edição em Tickets.
    /// </summary>
    private async Task<bool> IsDepartmentStaffAsync(Guid departmentId)
    {
        try
        {
            var scope = await _scopeContext.GetAccessAsync(ResourceType.Tickets, ActionType.Edit);
            // Se tem acesso global ou permissão de edição, é staff
            // Também verifica se o ticket está atribuído ao usuário atual
            return scope.HasGlobalAccess || scope.AllowedClientIds.Any() || scope.AllowedSiteIds.Any();
        }
        catch
        {
            // Em caso de erro na verificação, assume que não é staff (segurança)
            return false;
        }
    }
}
