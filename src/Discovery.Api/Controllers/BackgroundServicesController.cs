using Discovery.Api.Services.BackgroundServices;
using Discovery.Api.Services.Quartz;
using Discovery.Api.Filters;
using Discovery.Core.Enums.Identity;
using Microsoft.AspNetCore.Mvc;
using Quartz;
using Quartz.Impl.Matchers;

namespace Discovery.Api.Controllers;

/// <summary>
/// Admin dashboard for every long-running process in the API:
/// <list type="bullet">
///   <item><c>IHostedService</c> instances (NATS bridge, SignalR bridge, embeddings, reconciliations, etc.)</item>
///   <item>Quartz.NET scheduled jobs (purge, retention, P2P maintenance)</item>
/// </list>
///
/// All data is in-memory (registry + Quartz history listener). Endpoints
/// require an authenticated admin user.
/// </summary>
[ApiController]
[RequireUserAuth]
[Route("api/v{version:apiVersion}/admin/background-services")]
public class BackgroundServicesController : ControllerBase
{
    private readonly BackgroundServiceRegistry _registry;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IEnumerable<IHostedService> _hostedServices;

    public BackgroundServicesController(
        BackgroundServiceRegistry registry,
        ISchedulerFactory schedulerFactory,
        IEnumerable<IHostedService> hostedServices)
    {
        _registry = registry;
        _schedulerFactory = schedulerFactory;
        _hostedServices = hostedServices;
    }

    // ── Hosted service dashboard ───────────────────────────────────────────

    /// <summary>
    /// Returns every registered <c>IHostedService</c> with state, uptime,
    /// iteration count, error count, last error, and custom metrics.
    /// </summary>
    [HttpGet]
    public IActionResult GetAll()
    {
        // Make sure every running hosted service is at least surfaced (even if it
        // never reported a heartbeat).
        EnsureAllHostedServicesRegistered();

        var snapshots = _registry.Snapshot();
        var grouped = snapshots
            .GroupBy(s => s.Category)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                category = g.Key,
                count = g.Count(),
                services = g.OrderBy(s => s.Name).ToList()
            });

        return Ok(new
        {
            totalServices = snapshots.Count,
            running = snapshots.Count(s => s.State == BackgroundServiceState.Running),
            degraded = snapshots.Count(s => s.State == BackgroundServiceState.Degraded),
            stopped = snapshots.Count(s => s.State == BackgroundServiceState.Stopped),
            unknown = snapshots.Count(s => s.State == BackgroundServiceState.Unknown),
            categories = grouped
        });
    }

    /// <summary>
    /// Returns details for a single hosted service by name (type name).
    /// </summary>
    [HttpGet("{name}")]
    public IActionResult GetByName(string name)
    {
        EnsureAllHostedServicesRegistered();
        var snap = _registry.Get(name);
        if (snap is null)
            return NotFound(new { error = $"Background service '{name}' not registered." });
        return Ok(snap);
    }

    // ── Unified dashboard (HostedServices + Quartz) ────────────────────────

    /// <summary>
    /// Single-pane-of-glass view: hosted services + Quartz jobs + Quartz
    /// scheduler state. Designed for an admin UI dashboard.
    /// </summary>
    [HttpGet("/api/v{version:apiVersion}/admin/dashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken ct)
    {
        EnsureAllHostedServicesRegistered();
        var hosted = _registry.Snapshot();

        // Quartz section
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct);
        var quartzJobs = new List<object>();
        foreach (var key in jobKeys)
        {
            var detail = await scheduler.GetJobDetail(key, ct);
            var triggers = await scheduler.GetTriggersOfJob(key, ct);
            var history = JobExecutionHistoryStore.GetHistory(key);
            var last = history.Count > 0 ? history[^1] : null;

            DateTime? nextFire = null;
            DateTime? prevFire = null;
            string? triggerState = null;
            var firstTrigger = triggers.FirstOrDefault();
            if (firstTrigger is not null)
            {
                nextFire = firstTrigger.GetNextFireTimeUtc()?.UtcDateTime;
                prevFire = firstTrigger.GetPreviousFireTimeUtc()?.UtcDateTime;
                triggerState = (await scheduler.GetTriggerState(firstTrigger.Key, ct)).ToString();
            }

            quartzJobs.Add(new
            {
                jobName = key.Name,
                jobGroup = key.Group,
                description = detail?.Description,
                triggerState,
                previousFireTimeUtc = prevFire,
                nextFireTimeUtc = nextFire,
                executionsRecorded = history.Count,
                lastExecution = last is null ? null : new
                {
                    last.StartedAtUtc,
                    last.CompletedAtUtc,
                    durationMs = last.Duration?.TotalMilliseconds,
                    last.Succeeded,
                    last.ErrorMessage
                }
            });
        }

        return Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            hostedServices = new
            {
                total = hosted.Count,
                running = hosted.Count(s => s.State == BackgroundServiceState.Running),
                degraded = hosted.Count(s => s.State == BackgroundServiceState.Degraded),
                stopped = hosted.Count(s => s.State == BackgroundServiceState.Stopped),
                unknown = hosted.Count(s => s.State == BackgroundServiceState.Unknown),
                items = hosted
            },
            quartz = new
            {
                schedulerName = scheduler.SchedulerName,
                isStarted = scheduler.IsStarted,
                inStandbyMode = scheduler.InStandbyMode,
                jobCount = quartzJobs.Count,
                jobs = quartzJobs
            }
        });
    }

    /// <summary>
    /// Auto-registers every <c>IHostedService</c> currently resolvable so the
    /// dashboard surfaces them even if they never push a heartbeat.
    /// </summary>
    private void EnsureAllHostedServicesRegistered()
    {
        foreach (var svc in _hostedServices)
        {
            var typeName = svc.GetType().Name;
            // Skip Quartz's own hosted service; it's tracked in the Quartz section.
            if (typeName.Contains("QuartzHostedService", StringComparison.OrdinalIgnoreCase))
                continue;
            var category = BackgroundServiceCategoryMap.Resolve(typeName);
            _registry.EnsureRegistered(typeName, svc.GetType().FullName ?? typeName, category);
        }
    }
}
