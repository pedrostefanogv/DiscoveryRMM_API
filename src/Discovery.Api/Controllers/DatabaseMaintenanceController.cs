using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

/// <summary>
/// Manutenção do banco de dados: diagnóstico físico (índices inválidos, bloat,
/// estatísticas, wraparound) e histórico das execuções do job de manutenção.
/// O disparo do job continua em <c>/api/v1/admin/jobs/maintenance/database-maintenance/trigger</c>.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/admin/database-maintenance")]
public class DatabaseMaintenanceController(
    IDatabaseHealthService healthService,
    DiscoveryDbContext db) : ControllerBase
{
    /// <summary>Diagnóstico físico do PostgreSQL (somente leitura).</summary>
    [HttpGet("health")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetHealth(CancellationToken ct)
        => Ok(await healthService.GetReportAsync(ct));

    /// <summary>Últimas execuções persistidas da manutenção física.</summary>
    [HttpGet("runs")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetRuns([FromQuery] int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 10, 1, 100);
        var runs = await db.DatabaseMaintenanceRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(take)
            .Select(r => new
            {
                r.Id,
                r.StartedAtUtc,
                r.FinishedAtUtc,
                r.Succeeded,
                r.TableCount,
                r.SuccessCount,
                r.ErrorCount,
                r.DatabaseSizeBytes,
                r.ReclaimedBytes,
                r.ErrorMessage,
            })
            .ToListAsync(ct);

        return Ok(runs);
    }
}
