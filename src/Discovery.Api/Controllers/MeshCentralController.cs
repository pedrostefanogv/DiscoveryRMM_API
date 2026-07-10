using Discovery.Core.Cqrs.MeshCentral.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/meshcentral")]
public class MeshCentralController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetStatus()
    {
        var result = await mediator.Send(new GetMeshCentralStatusQuery());
        return result.ToActionResult();
    }
}
