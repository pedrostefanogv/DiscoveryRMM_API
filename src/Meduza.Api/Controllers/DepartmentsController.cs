using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DepartmentsController : ControllerBase
{
    private readonly IDepartmentRepository _repo;

    public DepartmentsController(IDepartmentRepository repo) => _repo = repo;

    /// <summary>
    /// Obtém departamentos globais.
    /// </summary>
    [HttpGet("global")]
    public async Task<IActionResult> GetGlobal()
    {
        var departments = await _repo.GetGlobalAsync();
        return Ok(departments);
    }

    /// <summary>
    /// Obtém departamentos de um cliente (incluindo globais por padrão).
    /// </summary>
    [HttpGet]
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
    public async Task<IActionResult> GetById(Guid id)
    {
        var department = await _repo.GetByIdAsync(id);
        return department is null ? NotFound() : Ok(department);
    }

    /// <summary>
    /// Cria um novo departamento (global ou para um cliente específico).
    /// </summary>
    [HttpPost]
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
    public async Task<IActionResult> Delete(Guid id)
    {
        var department = await _repo.GetByIdAsync(id);
        if (department is null) return NotFound();

        await _repo.DeleteAsync(id);
        return NoContent();
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
