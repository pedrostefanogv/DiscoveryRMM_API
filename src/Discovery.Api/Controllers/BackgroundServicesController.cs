using Discovery.Api.Filters;
using Discovery.Api.Services.BackgroundServices;
using Discovery.Core.Enums.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/admin/background-services")]
public class BackgroundServicesController(BackgroundServiceRegistry registry) : ControllerBase
{
    [HttpGet]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public IActionResult GetAll()
    {
        var services = registry.Snapshot();
        return Ok(services);
    }

    [HttpGet("{name}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public IActionResult GetByName(string name)
    {
        var service = registry.Get(name);
        if (service is null)
            return NotFound(new { error = $"Background service '{name}' not found." });
        return Ok(service);
    }
}
