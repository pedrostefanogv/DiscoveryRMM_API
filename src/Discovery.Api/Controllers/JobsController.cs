using Discovery.Api.Filters;
using Discovery.Api.Services.Quartz;
using Discovery.Core.Enums.Identity;
using Microsoft.AspNetCore.Mvc;
using Quartz;
using Quartz.Impl.Matchers;

namespace Discovery.Api.Controllers;

/// <summary>
/// Admin-only endpoint for managing Quartz.NET scheduled jobs.
/// Allows viewing job status, triggering manual execution, pausing/resuming,
/// and reading execution history (last run, next run, error state).
///
/// Access: requires an authenticated user with ServerConfig view/edit
/// (the global RequireUserAuthAttribute adds authentication on top).
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/admin/jobs")]
public class JobsController(ISchedulerFactory schedulerFactory) : ControllerBase
{
    // ── Dashboard ──────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all registered Quartz jobs with their current state.
    /// </summary>
    [HttpGet]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetAllJobs(CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct);

        var jobs = new List<object>();
        foreach (var key in jobKeys)
        {
            var detail = await scheduler.GetJobDetail(key, ct);
            var triggers = await scheduler.GetTriggersOfJob(key, ct);

            var triggerInfo = new List<object>();
            foreach (var trigger in triggers)
            {
                var state = await scheduler.GetTriggerState(trigger.Key, ct);
                triggerInfo.Add(new
                {
                    triggerName = trigger.Key.Name,
                    triggerGroup = trigger.Key.Group,
                    state = state.ToString(),
                    description = trigger.Description,
                    previousFireTimeUtc = trigger.GetPreviousFireTimeUtc()?.UtcDateTime,
                    nextFireTimeUtc = trigger.GetNextFireTimeUtc()?.UtcDateTime,
                    finalFireTimeUtc = trigger.FinalFireTimeUtc?.UtcDateTime,
                    mayFireAgain = trigger.GetMayFireAgain(),
                });
            }

            jobs.Add(new
            {
                jobName = key.Name,
                jobGroup = key.Group,
                description = detail?.Description,
                concurrentExecutionDisallowed = detail?.ConcurrentExecutionDisallowed ?? false,
                durable = detail?.Durable ?? false,
                triggers = triggerInfo
            });
        }

        return Ok(new
        {
            schedulerName = scheduler.SchedulerName,
            schedulerInstanceId = scheduler.SchedulerInstanceId,
            isStarted = scheduler.IsStarted,
            inStandbyMode = scheduler.InStandbyMode,
            jobCount = jobs.Count,
            jobs
        });
    }

    // ── Manual Trigger ─────────────────────────────────────────────────────

    /// <summary>
    /// Triggers a job immediately by name and group.
    /// </summary>
    [HttpPost("{jobGroup}/{jobName}/trigger")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> TriggerJob(
        string jobGroup, string jobName, [FromQuery] bool force, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var key = await ResolveJobKeyAsync(jobGroup, jobName, scheduler.CheckExists, ct);

        if (key is null)
            return NotFound(new { error = $"Job '{jobGroup}.{jobName}' not found." });

        // force=true: os ciclos de métricas/triagem ignoram o vencimento do escopo
        // e (métricas) a validade do snapshot neste disparo.
        await scheduler.TriggerJob(key, new JobDataMap { ["force"] = force }, ct);

        return Ok(new { message = $"Job '{jobGroup}.{jobName}' triggered.", force, triggeredAtUtc = DateTime.UtcNow });
    }

    // ── Pause / Resume ─────────────────────────────────────────────────────

    /// <summary>
    /// Pauses all triggers for a job (no new executions until resumed).
    /// </summary>
    [HttpPost("{jobGroup}/{jobName}/pause")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> PauseJob(string jobGroup, string jobName, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var key = await ResolveJobKeyAsync(jobGroup, jobName, scheduler.CheckExists, ct);

        if (key is null)
            return NotFound(new { error = $"Job '{jobGroup}.{jobName}' not found." });

        await scheduler.PauseJob(key, ct);

        return Ok(new { message = $"Job '{jobGroup}.{jobName}' paused.", pausedAtUtc = DateTime.UtcNow });
    }

    /// <summary>
    /// Resumes all triggers for a paused job.
    /// </summary>
    [HttpPost("{jobGroup}/{jobName}/resume")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> ResumeJob(string jobGroup, string jobName, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var key = await ResolveJobKeyAsync(jobGroup, jobName, scheduler.CheckExists, ct);

        if (key is null)
            return NotFound(new { error = $"Job '{jobGroup}.{jobName}' not found." });

        await scheduler.ResumeJob(key, ct);

        return Ok(new { message = $"Job '{jobGroup}.{jobName}' resumed.", resumedAtUtc = DateTime.UtcNow });
    }

    // ── Scheduler Control ──────────────────────────────────────────────────

    /// <summary>
    /// Puts the scheduler in standby (pauses ALL jobs).
    /// </summary>
    [HttpPost("scheduler/standby")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> StandbyScheduler(CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        await scheduler.Standby(ct);
        return Ok(new { message = "Scheduler is now in standby mode." });
    }

    /// <summary>
    /// Starts the scheduler if it was in standby.
    /// </summary>
    [HttpPost("scheduler/start")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.Edit)]
    public async Task<IActionResult> StartScheduler(CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        await scheduler.Start(ct);
        return Ok(new { message = "Scheduler started." });
    }

    // ── Job History / Metadata ─────────────────────────────────────────────

    /// <summary>
    /// Retrieves detailed info about a specific job, including last execution result.
    /// </summary>
    [HttpGet("{jobGroup}/{jobName}")]
    [RequirePermission(ResourceType.ServerConfig, ActionType.View)]
    public async Task<IActionResult> GetJobDetail(string jobGroup, string jobName, CancellationToken ct)
    {
        var scheduler = await schedulerFactory.GetScheduler(ct);
        var key = await ResolveJobKeyAsync(jobGroup, jobName, scheduler.CheckExists, ct);

        if (key is null)
            return NotFound(new { error = $"Job '{jobGroup}.{jobName}' not found." });

        var detail = await scheduler.GetJobDetail(key, ct);
        var triggers = await scheduler.GetTriggersOfJob(key, ct);
        var jobDataMap = detail?.JobDataMap;

        var contextData = new Dictionary<string, object?>();
        if (jobDataMap is not null)
        {
            foreach (var k in jobDataMap.Keys)
                contextData[k] = jobDataMap[k];
        }

        // Get execution history from listener (if available)
        var history = JobExecutionHistoryStore.GetHistory(key);

        return Ok(new
        {
            jobName = key.Name,
            jobGroup = key.Group,
            description = detail?.Description,
            jobType = detail?.JobType.FullName,
            concurrentExecutionDisallowed = detail?.ConcurrentExecutionDisallowed ?? false,
            durable = detail?.Durable ?? false,
            requestsRecovery = detail?.RequestsRecovery ?? false,
            jobData = contextData,
            triggers = triggers.Select(t => new
            {
                name = t.Key.Name,
                group = t.Key.Group,
                description = t.Description,
                previousFireTimeUtc = t.GetPreviousFireTimeUtc()?.UtcDateTime,
                nextFireTimeUtc = t.GetNextFireTimeUtc()?.UtcDateTime,
            }),
            executionHistory = history
        });
    }

    // ── Job key resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Resolve o <see cref="JobKey"/> a partir do nome usado pela configuração/UI.
    ///
    /// Os jobs são agendados com <c>ScheduleJob&lt;T&gt;</c> do
    /// Quartz.Extensions.DependencyInjection, que deriva a identidade do job a
    /// partir da identidade do trigger. O contrato atual usa o MESMO nome para
    /// job e trigger (ex.: "technician-metrics-refresh"), alinhado ao nome
    /// exposto pela configuração em /status.
    ///
    /// O fallback "{nome}-trigger" é mantido por compatibilidade: releases
    /// anteriores registravam o trigger com esse sufixo e o JobKey herdava o
    /// sufixo (era a causa do 404 no acionamento manual).
    /// </summary>
    internal static async Task<JobKey?> ResolveJobKeyAsync(
        string jobGroup, string jobName,
        Func<JobKey, CancellationToken, Task<bool>> exists,
        CancellationToken ct)
    {
        var direct = new JobKey(jobName, jobGroup);
        if (await exists(direct, ct))
            return direct;

        var suffixed = new JobKey($"{jobName}-trigger", jobGroup);
        return await exists(suffixed, ct) ? suffixed : null;
    }
}
