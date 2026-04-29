using System.Text.Json;
using Discovery.Api.Services;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Discovery.Api.Controllers;

/// <summary>
/// Base partial class for admin-facing agent endpoints (/api/agents/*).
/// Contains shared DI, Redis cache helpers, and scope invalidation.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
public partial class AgentsController : ControllerBase
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);
    private const int AgentCacheTtlSeconds = 30;

    private readonly IAgentRepository _agentRepo;
    private readonly IAgentHardwareRepository _hardwareRepo;
    private readonly IAgentSoftwareRepository _softwareRepo;
    private readonly ICommandRepository _commandRepo;
    private readonly IAgentAuthService _authService;
    private readonly IAutomationTaskService _automationTaskService;
    private readonly IAutomationScriptService _automationScriptService;
    private readonly IAutomationExecutionReportRepository _automationExecutionReportRepository;
    private readonly IAgentCommandDispatcher _commandDispatcher;
    private readonly IAgentMessaging _messaging;
    private readonly ISiteRepository _siteRepository;
    private readonly IRedisService _redisService;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly IPermissionService _permissionService;
    private readonly IRemoteDebugSessionManager _remoteDebugSessionManager;
    private readonly ICustomFieldService _customFieldService;
    private readonly ILogger<AgentsController> _logger;

    public AgentsController(
        IAgentRepository agentRepo,
        IAgentHardwareRepository hardwareRepo,
        IAgentSoftwareRepository softwareRepo,
        ICommandRepository commandRepo,
        IAgentAuthService authService,
        IAutomationTaskService automationTaskService,
        IAutomationScriptService automationScriptService,
        IAutomationExecutionReportRepository automationExecutionReportRepository,
        IAgentCommandDispatcher commandDispatcher,
        IAgentMessaging messaging,
        ISiteRepository siteRepository,
        IRedisService redisService,
        IConfigurationResolver configurationResolver,
        IMeshCentralApiService meshCentralApiService,
        IPermissionService permissionService,
        IRemoteDebugSessionManager remoteDebugSessionManager,
        ICustomFieldService customFieldService,
        ILogger<AgentsController> logger)
    {
        _agentRepo = agentRepo;
        _hardwareRepo = hardwareRepo;
        _softwareRepo = softwareRepo;
        _commandRepo = commandRepo;
        _authService = authService;
        _automationTaskService = automationTaskService;
        _automationScriptService = automationScriptService;
        _automationExecutionReportRepository = automationExecutionReportRepository;
        _commandDispatcher = commandDispatcher;
        _messaging = messaging;
        _siteRepository = siteRepository;
        _redisService = redisService;
        _configurationResolver = configurationResolver;
        _meshCentralApiService = meshCentralApiService;
        _permissionService = permissionService;
        _remoteDebugSessionManager = remoteDebugSessionManager;
        _customFieldService = customFieldService;
        _logger = logger;
    }

    // ── Redis Cache Helpers ───────────────────────────────────────────────

    private async Task<T?> GetOrSetCacheAsync<T>(string cacheKey, Func<Task<T?>> factory)
    {
        var cached = await _redisService.GetAsync(cacheKey);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<T>(cached, CacheJsonOptions);
                if (deserialized is not null)
                    return deserialized;
            }
            catch (JsonException)
            {
                await _redisService.DeleteAsync(cacheKey);
            }
        }

        var value = await factory();
        if (value is null) return value;

        var payload = JsonSerializer.Serialize(value, CacheJsonOptions);
        await _redisService.SetAsync(cacheKey, payload, AgentCacheTtlSeconds);
        return value;
    }

    private async Task InvalidateAgentScopeCachesAsync(Guid currentSiteId, Guid? previousSiteId = null, Guid? agentId = null)
    {
        await _redisService.DeleteAsync("agents:all-ids");
        await _redisService.DeleteByPrefixAsync("software-inventory:");

        var siteIds = new HashSet<Guid> { currentSiteId };
        if (previousSiteId.HasValue) siteIds.Add(previousSiteId.Value);

        foreach (var siteId in siteIds)
        {
            await _redisService.DeleteAsync($"agents:by-site:{siteId:N}");
            var site = await _siteRepository.GetByIdAsync(siteId);
            if (site is not null)
                await _redisService.DeleteAsync($"agents:by-client:{site.ClientId:N}");
        }

        if (agentId.HasValue)
        {
            await _redisService.DeleteAsync($"agents:single:{agentId.Value:N}");
            await _redisService.DeleteAsync($"agents:hardware:{agentId.Value:N}");
            await _redisService.DeleteAsync($"agents:software:snapshot:{agentId.Value:N}");
        }
    }

    // ── Online Grace Helpers ──────────────────────────────────────────────

    private static void ApplyEffectiveStatus(Agent agent, int onlineGraceSeconds)
    {
        if (agent.Status != AgentStatus.Online) return;
        var cutoffUtc = DateTime.UtcNow.AddSeconds(-onlineGraceSeconds);
        if (!agent.LastSeenAt.HasValue || agent.LastSeenAt.Value < cutoffUtc)
            agent.Status = AgentStatus.Offline;
    }

    private async Task<int> GetOnlineGraceSecondsForSiteAsync(Guid siteId)
    {
        try { var r = await _configurationResolver.ResolveForSiteAsync(siteId); return r.AgentOnlineGraceSeconds; }
        catch { return 120; }
    }

    private async Task<Dictionary<Guid, int>> GetOnlineGraceSecondsBySiteAsync(IEnumerable<Guid> siteIds)
    {
        var ids = siteIds.Distinct().ToList();
        var tasks = ids.Select(async siteId =>
        {
            try { var r = await _configurationResolver.ResolveForSiteAsync(siteId); return (siteId, grace: r.AgentOnlineGraceSeconds); }
            catch { return (siteId, grace: 120); }
        });
        return (await Task.WhenAll(tasks)).ToDictionary(e => e.siteId, e => e.grace);
    }

    private sealed record AgentHardwareCachePayload(
        AgentHardwareInfo? Hardware, IReadOnlyList<DiskInfo> Disks, IReadOnlyList<NetworkAdapterInfo> NetworkAdapters,
        IReadOnlyList<MemoryModuleInfo> MemoryModules, IReadOnlyList<PrinterInfo> Printers,
        IReadOnlyList<ListeningPortInfo> ListeningPorts, IReadOnlyList<OpenSocketInfo> OpenSockets);
}
