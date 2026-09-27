using System.Text.Json;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Departments.Commands;
using Discovery.Core.Cqrs.Support.Departments;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Cqrs.Departments.Queries;
using Discovery.Core.Cqrs.Support.Assignments;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

public record AddDepartmentMemberRequest(Guid UserId);

public record DepartmentCustomFieldRequest(
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    bool? IsRequired,
    bool? IsInternal,
    bool? IsActive,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    string? InputMask,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue);

[ApiController]
[Route("api/v{version:apiVersion}/departments")]
public class DepartmentsController(
    IMediator mediator,
    ICustomFieldService customFieldService,
    IDepartmentCustomFieldService departmentCustomFieldService,
    IAiAssignmentLearningService learningService) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";
    private Guid? CurrentUserId => HttpContext.Items["UserId"] is Guid uid ? uid : null;
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null,
        [FromQuery] bool includeGlobal = true)
    {
        var result = await mediator.Send(new ListDepartmentsQuery(clientId, includeGlobal));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetDepartmentByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> Create([FromBody] CreateDepartmentCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDepartmentCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteDepartmentCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ── Members (round-robin) ────────────────────────────────────────────

    [HttpGet("{id:guid}/members")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetMembers(Guid id)
        => (await mediator.Send(new ListDepartmentMembersQuery(id), HttpContext.RequestAborted)).ToActionResult();

    [HttpPost("{id:guid}/members")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddDepartmentMemberRequest request)
        => (await mediator.Send(new AddDepartmentMemberCommand(id, request.UserId), HttpContext.RequestAborted)).ToActionResult();

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
        => (await mediator.Send(new RemoveDepartmentMemberCommand(id, userId), HttpContext.RequestAborted)).ToActionResult();

    // ── Triagem por IA (auto-atribuição) ────────────────────────────────

    /// <summary>Perfil (competências/capacidade) + métricas de cada membro da equipe.</summary>
    [HttpGet("{id:guid}/assignment/team-metrics")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetAssignmentTeamMetrics(Guid id)
        => (await mediator.Send(new GetDepartmentAssignmentTeamMetricsQuery(id), HttpContext.RequestAborted)).ToActionResult();

    /// <summary>Atualiza competências, teto de chamados, peso e opt-out do membro.</summary>
    [HttpPut("{id:guid}/members/{userId:guid}/profile")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> UpdateMemberProfile(
        Guid id, Guid userId, [FromBody] UpdateDepartmentMemberProfileRequest request)
        => (await mediator.Send(new UpdateDepartmentMemberProfileCommand(
                id, userId, request.SkillTags, request.SkillLevel, request.MaxOpenTickets,
                request.Weight, request.AcceptsAiAssignment, request.ClearMaxOpenTickets),
            HttpContext.RequestAborted)).ToActionResult();

    /// <summary>Força o recálculo dos snapshots de métricas usados pela triagem.</summary>
    [HttpPost("{id:guid}/assignment/metrics/refresh")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> RefreshAssignmentMetrics(Guid id)
        => (await mediator.Send(new RefreshTechnicianMetricsCommand(id), HttpContext.RequestAborted)).ToActionResult();

    // ── Aprendizado da triagem por IA (competências e pesos) ────────────

    /// <summary>Sugestões pendentes de competências e de recalibração de pesos.</summary>
    [HttpGet("{id:guid}/learning/suggestions")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetLearningSuggestions(Guid id)
        => Ok(await learningService.GetSuggestionsAsync(id, HttpContext.RequestAborted));

    /// <summary>Roda um ciclo de aprendizado sob demanda para o departamento.</summary>
    [HttpPost("{id:guid}/learning/run")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> RunLearningCycle(Guid id)
    {
        var created = await learningService.RunCycleAsync(id, CurrentUserId, HttpContext.RequestAborted);
        return Ok(new { created });
    }

    [HttpPost("{id:guid}/learning/skills/{suggestionId:guid}/apply")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> ApplySkillSuggestion(Guid id, Guid suggestionId)
    {
        var result = await learningService.ApplySkillSuggestionAsync(
            id, suggestionId, CurrentUserId, HttpContext.RequestAborted);
        return result is null
            ? NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sugestão não encontrada ou já decidida." } } })
            : Ok(result);
    }

    [HttpPost("{id:guid}/learning/skills/{suggestionId:guid}/discard")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> DiscardSkillSuggestion(Guid id, Guid suggestionId)
    {
        var discarded = await learningService.DiscardSkillSuggestionAsync(
            id, suggestionId, CurrentUserId, HttpContext.RequestAborted);
        return discarded ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/learning/weights/{suggestionId:guid}/apply")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> ApplyWeightSuggestion(Guid id, Guid suggestionId)
    {
        var result = await learningService.ApplyWeightSuggestionAsync(
            id, suggestionId, CurrentUserId, HttpContext.RequestAborted);
        return result is null
            ? NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sugestão não encontrada ou já decidida." } } })
            : Ok(result);
    }

    [HttpPost("{id:guid}/learning/weights/{suggestionId:guid}/discard")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> DiscardWeightSuggestion(Guid id, Guid suggestionId)
    {
        var discarded = await learningService.DiscardWeightSuggestionAsync(
            id, suggestionId, CurrentUserId, HttpContext.RequestAborted);
        return discarded ? NoContent() : NotFound();
    }

    // ── Custom Fields ────────────────────────────────────────────────────

    // Lista as DEFINIÇÕES de campos do departamento (não os valores). A tela de
    // configuração do departamento espera id/dataType/isInternal/optionsJson para
    // editar/excluir; devolver valores deixava o id vazio e o PUT ia para
    // ".../custom-fields//definition" (404).
    [HttpGet("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetCustomFields(Guid id)
        => Ok(await departmentCustomFieldService.GetDefinitionsByDepartmentAsync(id, HttpContext.RequestAborted));

    // Compatibilidade: preserva o acesso aos VALORES do departamento (o contrato
    // antigo de GET /custom-fields agora devolve definições).
    [HttpGet("{id:guid}/custom-fields/values")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetCustomFieldValues(Guid id, [FromQuery] bool includeSecrets = false)
        => Ok(await customFieldService.GetValuesAsync(CustomFieldScopeType.Department, id, includeSecrets, HttpContext.RequestAborted));

    [HttpPut("{id:guid}/custom-fields/{definitionId:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomField(Guid id, Guid definitionId, [FromBody] JsonElement body)
    {
        var valueJson = body.TryGetProperty("value", out var prop) ? prop.GetRawText() : body.GetRawText();
        var result = await customFieldService.UpsertValueAsync(
            new UpsertCustomFieldValueInput(definitionId, CustomFieldScopeType.Department, id, valueJson, Username),
            HttpContext.RequestAborted);
        return Ok(result);
    }

    // ── Custom Field Definitions (formulário de abertura) ────────────────

    [HttpPost("{id:guid}/custom-fields")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> CreateCustomFieldDefinition(Guid id, [FromBody] DepartmentCustomFieldRequest request)
    {
        try
        {
            var created = await departmentCustomFieldService.CreateDepartmentFieldAsync(
                id, ToCreateInput(request), Username, HttpContext.RequestAborted);
            return CreatedAtAction(nameof(GetTicketSchema), new { id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    [HttpPut("{id:guid}/custom-fields/{fieldId:guid}/definition")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> UpdateCustomFieldDefinition(Guid id, Guid fieldId, [FromBody] DepartmentCustomFieldRequest request)
    {
        try
        {
            var updated = await departmentCustomFieldService.UpdateDepartmentFieldAsync(
                fieldId, id, ToUpdateInput(request), Username, HttpContext.RequestAborted);
            if (updated is null)
                return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Campo não encontrado." } } });
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    [HttpDelete("{id:guid}/custom-fields/{fieldId:guid}")]
    [RequirePermission(ResourceType.Departments, ActionType.Edit)]
    public async Task<IActionResult> DeleteCustomFieldDefinition(Guid id, Guid fieldId)
    {
        try
        {
            var removed = await departmentCustomFieldService.DeleteDepartmentFieldAsync(
                fieldId, id, HttpContext.RequestAborted);
            return removed ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    /// <summary>
    /// Schema público (formulário de abertura) por padrão. Com includeInternal=true
    /// devolve também os campos internos: usado pela tela de detalhe do chamado,
    /// onde o atendente precisa preencher campos marcados como "só de atendente".
    /// O formulário de abertura continua recebendo apenas os públicos.
    /// </summary>
    [HttpGet("{id:guid}/ticket-schema")]
    [RequirePermission(ResourceType.Departments, ActionType.View)]
    public async Task<IActionResult> GetTicketSchema(
        Guid id,
        [FromQuery] bool includeInternal = false,
        [FromQuery] Guid? ticketId = null)
        => Ok(includeInternal
            // Com ticketId o schema já devolve o valor atual de cada campo
            // (CurrentValueJson); sem ele os valores vêm do endpoint do chamado.
            ? await departmentCustomFieldService.GetFullSchemaForDepartmentAsync(id, ticketId, HttpContext.RequestAborted)
            : await departmentCustomFieldService.GetPublicSchemaForDepartmentAsync(id, HttpContext.RequestAborted));

    private static CreateDepartmentCustomFieldInput ToCreateInput(DepartmentCustomFieldRequest r) => new(
        r.Name, r.Label, r.Description, r.DataType,
        r.IsRequired ?? false, r.IsInternal ?? false, r.IsActive ?? true,
        r.Options, r.ValidationRegex, r.InputMask,
        r.MinLength, r.MaxLength, r.MinValue, r.MaxValue);

    private static UpdateDepartmentCustomFieldInput ToUpdateInput(DepartmentCustomFieldRequest r) => new(
        r.Name, r.Label, r.Description, r.DataType,
        r.IsRequired ?? false, r.IsInternal ?? false, r.IsActive ?? true,
        r.Options, r.ValidationRegex, r.InputMask,
        r.MinLength, r.MaxLength, r.MinValue, r.MaxValue);
}
