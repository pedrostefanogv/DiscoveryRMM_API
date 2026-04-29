using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public class WorkflowProfilesController : ControllerBase
{
    private readonly IWorkflowProfileRepository _repo;
    private readonly IDepartmentRepository _departmentRepo;

    public WorkflowProfilesController(
        IWorkflowProfileRepository repo,
        IDepartmentRepository departmentRepo)
    {
        _repo = repo;
        _departmentRepo = departmentRepo;
    }

    /// <summary>
    /// Obtém perfis de workflow globais.
    /// </summary>
    [HttpGet("global")]
    public async Task<IActionResult> GetGlobal()
    {
        var profiles = await _repo.GetGlobalAsync();
        return Ok(profiles);
    }

    /// <summary>
    /// Obtém perfis de workflow de um cliente (incluindo globais por padrão).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetByClient([FromQuery] Guid? clientId, [FromQuery] bool includeGlobal = true)
    {
        var profiles = await _repo.GetByClientAsync(clientId, includeGlobal);
        return Ok(profiles);
    }

    /// <summary>
    /// Obtém perfis de um departamento específico.
    /// </summary>
    [HttpGet("by-department/{departmentId:guid}")]
    public async Task<IActionResult> GetByDepartment(Guid departmentId)
    {
        var profiles = await _repo.GetByDepartmentAsync(departmentId);
        return Ok(profiles);
    }

    /// <summary>
    /// Obtém um perfil de workflow específico pelo ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var profile = await _repo.GetByIdAsync(id);
        return profile is null ? NotFound() : Ok(profile);
    }

    /// <summary>
    /// Cria um novo perfil de workflow.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkflowProfileRequest request)
    {
        // Validar departamento
        var department = await _departmentRepo.GetByIdAsync(request.DepartmentId);
        if (department is null)
            return BadRequest("Departamento não encontrado.");

        var profile = new WorkflowProfile
        {
            ClientId = request.ClientId,
            DepartmentId = request.DepartmentId,
            Name = request.Name,
            Description = request.Description,
            SlaHours = request.SlaHours,
            DefaultPriority = request.DefaultPriority ?? TicketPriority.Medium,
            IsActive = true
        };

        var created = await _repo.CreateAsync(profile);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Atualiza um perfil de workflow existente.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWorkflowProfileRequest request)
    {
        var profile = await _repo.GetByIdAsync(id);
        if (profile is null) return NotFound();

        if (!profile.DepartmentId.Equals(request.DepartmentId))
        {
            var department = await _departmentRepo.GetByIdAsync(request.DepartmentId);
            if (department is null)
                return BadRequest("Departamento não encontrado.");
        }

        profile.Name = request.Name;
        profile.Description = request.Description;
        profile.DepartmentId = request.DepartmentId;
        profile.SlaHours = request.SlaHours;
        profile.DefaultPriority = request.DefaultPriority;
        profile.IsActive = request.IsActive;

        await _repo.UpdateAsync(profile);
        return Ok(profile);
    }

    /// <summary>
    /// Deleta (soft delete) um perfil de workflow.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var profile = await _repo.GetByIdAsync(id);
        if (profile is null) return NotFound();

        await _repo.DeleteAsync(id);
        return NoContent();
    }
}

public record CreateWorkflowProfileRequest(
    Guid? ClientId,
    Guid DepartmentId,
    string Name,
    string? Description,
    int SlaHours,
    TicketPriority? DefaultPriority);

public record UpdateWorkflowProfileRequest(
    string Name,
    string? Description,
    Guid DepartmentId,
    int SlaHours,
    TicketPriority DefaultPriority,
    bool IsActive);
