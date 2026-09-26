using Discovery.Core.Cqrs.CustomFieldTemplates;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Catálogo de modelos de campos personalizados (pré-configurados).
/// Assim como /custom-fields, é um catálogo de configuração: exige apenas
/// sessão autenticada (o gate de UI é settings/departments no portal).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/custom-field-templates")]
public class CustomFieldTemplatesController(IMediator mediator) : ControllerBase
{
    private string Username => HttpContext.Items["Username"] as string ?? "api";

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? departmentId = null,
        [FromQuery] bool includeGlobal = true,
        [FromQuery] bool includeInactive = false,
        [FromQuery] bool allScopes = false)
        => (await mediator.Send(
            new ListCustomFieldTemplatesQuery(clientId, departmentId, includeGlobal, includeInactive, allScopes),
            HttpContext.RequestAborted)).ToActionResult();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCustomFieldTemplateCommand cmd)
        => (await mediator.Send(cmd with { CreatedBy = Username }, HttpContext.RequestAborted)).ToActionResult();

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCustomFieldTemplateCommand cmd)
        => (await mediator.Send(cmd with { Id = id }, HttpContext.RequestAborted)).ToActionResult();

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
        => (await mediator.Send(new DeleteCustomFieldTemplateCommand(id), HttpContext.RequestAborted)).ToActionResult();
}
