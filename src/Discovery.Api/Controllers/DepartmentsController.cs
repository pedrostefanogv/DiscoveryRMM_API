using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class DepartmentsController : ControllerBase
{
    private readonly IDepartmentRepository _repo;
    private readonly IDepartmentCustomFieldService _customFieldService;

    public DepartmentsController(
        IDepartmentRepository repo,
        IDepartmentCustomFieldService customFieldService)
    {
        _repo = repo;
        _customFieldService = customFieldService;
    }

    /// <summary>
    /// Obtém departamentos globais.
    /// </summary>
    [HttpGet("global")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = "Long")]
    public async Task<IActionResult> GetGlobal()
    {
        var departments = await _repo.GetGlobalAsync();
        return Ok(departments);
    }

    /// <summary>
    /// Obtém departamentos de um cliente (incluindo globais por padrão).
    /// </summary>
    [HttpGet]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = "Medium")]
    public async Task<IActionResult> GetByClient([FromQuery] Guid? clientId, [FromQuery] bool includeGlobal = true, [FromQuery] bool activeOnly = true)
    {
        if (clientId.HasValue)
        {
            var departments = await _repo.GetByClientAsync(clientId.Value, includeGlobal, activeOnly);
            return Ok(departments);
        }

        // Se não especificar cliente, retornar globais
        var global = await _repo.GetGlobalAsync();
        return Ok(global);
    }

    /// <summary>
    /// Obtém um departamento específico pelo ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var department = await _repo.GetByIdAsync(id);
        return department is null ? NotFound() : Ok(department);
    }

    /// <summary>
    /// Cria um novo departamento (global ou para um cliente específico).
    /// </summary>
    [HttpPost]
    [RequirePermission(ResourceType.Clients, ActionType.Create)]
    public async Task<IActionResult> Create([FromBody] CreateDepartmentRequest request)
    {
        // Validar se já existe um departamento com este nome
        if (await _repo.ExistsByNameAsync(request.ClientId, request.Name))
            return BadRequest($"Departamento '{request.Name}' já existe para este contexto.");

        var department = new Department
        {
            ClientId = request.ClientId,
            Name = request.Name,
            Description = request.Description,
            InheritFromGlobalId = request.InheritFromGlobalId,
            SortOrder = request.SortOrder ?? 0,
            IsActive = true
        };

        var created = await _repo.CreateAsync(department);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Atualiza um departamento existente.
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDepartmentRequest request)
    {
        var department = await _repo.GetByIdAsync(id);
        if (department is null) return NotFound();

        // Validar se novo nome já existe
        if (department.Name != request.Name && await _repo.ExistsByNameAsync(department.ClientId, request.Name))
            return BadRequest($"Departamento '{request.Name}' já existe para este contexto.");

        department.Name = request.Name;
        department.Description = request.Description;
        department.InheritFromGlobalId = request.InheritFromGlobalId;
        department.SortOrder = request.SortOrder;
        department.IsActive = request.IsActive;

        await _repo.UpdateAsync(department);
        return Ok(department);
    }

    /// <summary>
    /// Deleta (soft delete) um departamento.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Delete)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var department = await _repo.GetByIdAsync(id);
        if (department is null) return NotFound();

        await _repo.DeleteAsync(id);
        return NoContent();
    }

    // ─────────────────────────────────────────────────────────────────
    //  Custom Fields do Departamento
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lista todos os campos customizados de um departamento.
    /// </summary>
    [HttpGet("{departmentId:guid}/custom-fields")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetCustomFields(
        Guid departmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var fields = await _customFieldService.GetDefinitionsByDepartmentAsync(departmentId, cancellationToken);
            return Ok(fields);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cria um novo campo customizado para o departamento.
    /// </summary>
    [HttpPost("{departmentId:guid}/custom-fields")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> CreateCustomField(
        Guid departmentId,
        [FromBody] CreateDepartmentCustomFieldRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _customFieldService.CreateDepartmentFieldAsync(
                departmentId,
                request.ToInput(),
                HttpContext.Items["Username"] as string ?? "api",
                cancellationToken);

            return CreatedAtAction(
                nameof(GetCustomFields),
                new { departmentId },
                created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Atualiza um campo customizado existente do departamento.
    /// </summary>
    [HttpPut("{departmentId:guid}/custom-fields/{fieldId:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Edit)]
    public async Task<IActionResult> UpdateCustomField(
        Guid departmentId,
        Guid fieldId,
        [FromBody] UpdateDepartmentCustomFieldRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _customFieldService.UpdateDepartmentFieldAsync(
                fieldId,
                departmentId,
                request.ToInput(),
                HttpContext.Items["Username"] as string ?? "api",
                cancellationToken);

            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Remove (soft-delete) um campo customizado do departamento.
    /// </summary>
    [HttpDelete("{departmentId:guid}/custom-fields/{fieldId:guid}")]
    [RequirePermission(ResourceType.Clients, ActionType.Delete)]
    public async Task<IActionResult> DeleteCustomField(
        Guid departmentId,
        Guid fieldId,
        CancellationToken cancellationToken)
    {
        try
        {
            var removed = await _customFieldService.DeleteDepartmentFieldAsync(
                fieldId, departmentId, cancellationToken);
            return removed ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retorna o schema público de campos para o formulário de abertura de chamado.
    /// Inclui apenas campos não-internos e ativos.
    /// </summary>
    [HttpGet("{departmentId:guid}/ticket-schema")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetTicketSchema(
        Guid departmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var schema = await _customFieldService.GetPublicSchemaForDepartmentAsync(
                departmentId, cancellationToken);
            return Ok(schema);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retorna o schema completo de campos do departamento (públicos + internos).
    /// Query param includeInternal=true requer permissão de edição.
    /// </summary>
    [HttpGet("{departmentId:guid}/custom-fields/schema")]
    [RequirePermission(ResourceType.Clients, ActionType.View)]
    public async Task<IActionResult> GetFullSchema(
        Guid departmentId,
        [FromQuery] bool includeInternal = false,
        [FromQuery] Guid? ticketId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (includeInternal)
            {
                var schema = await _customFieldService.GetFullSchemaForDepartmentAsync(
                    departmentId, ticketId, cancellationToken);
                return Ok(schema);
            }

            var publicSchema = await _customFieldService.GetPublicSchemaForDepartmentAsync(
                departmentId, cancellationToken);
            return Ok(publicSchema);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}

public record CreateDepartmentRequest(
    Guid? ClientId,
    string Name,
    string? Description,
    Guid? InheritFromGlobalId,
    int? SortOrder);

public record UpdateDepartmentRequest(
    string Name,
    string? Description,
    Guid? InheritFromGlobalId,
    int SortOrder,
    bool IsActive);

public record CreateDepartmentCustomFieldRequest(
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsInternal,
    bool IsActive,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue)
{
    public CreateDepartmentCustomFieldInput ToInput() => new(
        Name, Label, Description, DataType, IsRequired, IsInternal, IsActive,
        Options, ValidationRegex, MinLength, MaxLength, MinValue, MaxValue);
}

public record UpdateDepartmentCustomFieldRequest(
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsInternal,
    bool IsActive,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue)
{
    public UpdateDepartmentCustomFieldInput ToInput() => new(
        Name, Label, Description, DataType, IsRequired, IsInternal, IsActive,
        Options, ValidationRegex, MinLength, MaxLength, MinValue, MaxValue);
}
