using Meduza.Core.Configuration;
using Meduza.Core.Entities;
using Meduza.Core.Entities.Identity;
using Meduza.Core.Enums.Identity;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meduza.Infrastructure.Services;

public class MeshCentralPolicyResolver : IMeshCentralPolicyResolver
{
    private const int RightEditGroup = 1;
    private const int RightManageUsers = 2;
    private const int RightManageDevices = 4;
    private const int RightRemoteControl = 8;
    private const int RightAgentConsole = 16;
    private const int RightServerFiles = 32;
    private const int RightWakeDevice = 64;
    private const int RightSetNotes = 128;
    private const int RightDesktopViewOnly = 256;
    private const int RightLimitedDesktop = 4096;
    private const int RightLimitedEvents = 8192;
    private const int RightChatNotify = 16384;
    private const int RightUninstallAgent = 32768;
    private const int RightRemoteCommands = 131072;
    private const int RightPowerActions = 262144;

    private readonly MeshCentralOptions _options;
    private readonly IUserGroupRepository _userGroupRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly IMeshCentralRightsProfileRepository _meshCentralRightsProfileRepository;
    private readonly IClientRepository _clientRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly ILogger<MeshCentralPolicyResolver> _logger;

    public MeshCentralPolicyResolver(
        IOptions<MeshCentralOptions> options,
        IUserGroupRepository userGroupRepository,
        IRoleRepository roleRepository,
        IMeshCentralRightsProfileRepository meshCentralRightsProfileRepository,
        IClientRepository clientRepository,
        ISiteRepository siteRepository,
        ILogger<MeshCentralPolicyResolver> logger)
    {
        _options = options.Value;
        _userGroupRepository = userGroupRepository;
        _roleRepository = roleRepository;
        _meshCentralRightsProfileRepository = meshCentralRightsProfileRepository;
        _clientRepository = clientRepository;
        _siteRepository = siteRepository;
        _logger = logger;
    }

    public async Task<MeshCentralUserPolicyResolution> ResolveForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var assignments = (await _userGroupRepository.GetRolesForUserAsync(userId)).ToList();
        if (assignments.Count == 0)
        {
            return new MeshCentralUserPolicyResolution
            {
                UserId = userId,
                Sites = []
            };
        }

        var profiles = await LoadProfilesAsync();
        var roleCache = new Dictionary<Guid, RoleResolutionContext>();
        var rightsBySite = new Dictionary<Guid, int>();
        var sourcesBySite = new Dictionary<Guid, HashSet<string>>();
        IReadOnlyCollection<Site>? globalSites = null;

        foreach (var assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var roleContext = await GetRoleContextAsync(assignment.RoleId, roleCache, cancellationToken);
            if (roleContext is null)
                continue;

            var roleRights = ResolveRoleRights(roleContext, profiles);
            roleRights = ApplyStrictGuardrails(roleRights, roleContext.Permissions);

            var siteIds = await ResolveAssignmentSiteIdsAsync(assignment, globalSites, cancellationToken);
            if (assignment.ScopeLevel == ScopeLevel.Global && globalSites is null)
            {
                globalSites = await LoadAllSitesAsync(cancellationToken);
                siteIds = globalSites.Select(s => s.Id);
            }

            foreach (var siteId in siteIds)
            {
                if (!rightsBySite.TryGetValue(siteId, out var currentRights))
                {
                    currentRights = 0;
                }

                rightsBySite[siteId] = currentRights | roleRights;

                if (!sourcesBySite.TryGetValue(siteId, out var sources))
                {
                    sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    sourcesBySite[siteId] = sources;
                }

                sources.Add($"{roleContext.RoleName}:{assignment.ScopeLevel}");
            }
        }

        var sites = rightsBySite
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => new MeshCentralSitePolicyResolution
            {
                SiteId = kvp.Key,
                MeshRights = kvp.Value,
                Sources = sourcesBySite.TryGetValue(kvp.Key, out var src) ? src.OrderBy(s => s).ToArray() : []
            })
            .ToArray();

        return new MeshCentralUserPolicyResolution
        {
            UserId = userId,
            Sites = sites
        };
    }

    private async Task<Dictionary<string, int>> LoadProfilesAsync()
    {
        var profiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var dbProfiles = await _meshCentralRightsProfileRepository.GetAllAsync();
        foreach (var profile in dbProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
                continue;

            profiles[profile.Name] = profile.RightsMask;
        }

        foreach (var configuredProfile in _options.IdentitySyncPolicyProfiles)
        {
            if (string.IsNullOrWhiteSpace(configuredProfile.Key))
                continue;

            profiles.TryAdd(configuredProfile.Key, configuredProfile.Value);
        }

        return profiles;
    }

    private async Task<RoleResolutionContext?> GetRoleContextAsync(
        Guid roleId,
        IDictionary<Guid, RoleResolutionContext> roleCache,
        CancellationToken cancellationToken)
    {
        if (roleCache.TryGetValue(roleId, out var cached))
        {
            return cached;
        }

        var role = await _roleRepository.GetByIdAsync(roleId);
        if (role is null)
        {
            _logger.LogWarning("Mesh policy resolver: role {RoleId} nao encontrada durante calculo.", roleId);
            return null;
        }

        var permissions = (await _roleRepository.GetPermissionsForRoleAsync(roleId))
            .Select(p => (p.ResourceType, p.ActionType))
            .ToHashSet();

        var context = new RoleResolutionContext(
            role.Name,
            permissions,
            role.MeshRightsMask,
            role.MeshRightsProfile);
        roleCache[roleId] = context;
        return context;
    }

    private async Task<IEnumerable<Guid>> ResolveAssignmentSiteIdsAsync(
        UserGroupRole assignment,
        IReadOnlyCollection<Site>? globalSites,
        CancellationToken cancellationToken)
    {
        switch (assignment.ScopeLevel)
        {
            case ScopeLevel.Site when assignment.ScopeId.HasValue:
                return [assignment.ScopeId.Value];

            case ScopeLevel.Client when assignment.ScopeId.HasValue:
                return (await _siteRepository.GetByClientIdAsync(assignment.ScopeId.Value, includeInactive: false))
                    .Select(s => s.Id)
                    .ToArray();

            case ScopeLevel.Global:
                if (globalSites is not null)
                {
                    return globalSites.Select(s => s.Id).ToArray();
                }

                return (await LoadAllSitesAsync(cancellationToken)).Select(s => s.Id).ToArray();

            default:
                return [];
        }
    }

    private async Task<IReadOnlyCollection<Site>> LoadAllSitesAsync(CancellationToken cancellationToken)
    {
        var clients = await _clientRepository.GetAllAsync(includeInactive: false);
        var sites = new List<Site>();

        foreach (var client in clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clientSites = await _siteRepository.GetByClientIdAsync(client.Id, includeInactive: false);
            sites.AddRange(clientSites);
        }

        return sites;
    }

    private int ResolveRoleRights(
        RoleResolutionContext roleContext,
        IReadOnlyDictionary<string, int> profiles)
    {
        if (roleContext.MeshRightsMask.HasValue)
        {
            return roleContext.MeshRightsMask.Value;
        }

        if (!string.IsNullOrWhiteSpace(roleContext.MeshRightsProfile)
            && profiles.TryGetValue(roleContext.MeshRightsProfile, out var roleProfileRights))
        {
            return roleProfileRights;
        }

        if (profiles.TryGetValue(roleContext.RoleName, out var profileByRoleName))
        {
            return profileByRoleName;
        }

        var inferred = InferRightsFromPermissions(roleContext.Permissions);
        if (inferred != 0)
        {
            return inferred;
        }

        if (!string.IsNullOrWhiteSpace(_options.IdentitySyncPolicyDefaultProfile)
            && profiles.TryGetValue(_options.IdentitySyncPolicyDefaultProfile, out var fallbackProfileRights))
        {
            return fallbackProfileRights;
        }

        return _options.IdentitySyncDefaultMeshRights;
    }

    private static int InferRightsFromPermissions(IReadOnlySet<(ResourceType Resource, ActionType Action)> permissions)
    {
        var rights = 0;

        if (HasPermission(permissions, ResourceType.Agents, ActionType.View))
        {
            rights |= RightDesktopViewOnly;
            rights |= RightLimitedEvents;
        }

        if (HasPermission(permissions, ResourceType.Agents, ActionType.Edit))
        {
            rights |= RightManageDevices;
            rights |= RightSetNotes;
        }

        if (HasPermission(permissions, ResourceType.Agents, ActionType.Execute))
        {
            rights |= RightRemoteControl;
            rights |= RightAgentConsole;
            rights |= RightServerFiles;
            rights |= RightWakeDevice;
            rights |= RightChatNotify;
            rights |= RightLimitedDesktop;
        }

        if (HasPermission(permissions, ResourceType.Users, ActionType.Edit))
        {
            rights |= RightManageUsers;
        }

        if (HasPermission(permissions, ResourceType.Sites, ActionType.Edit)
            || HasPermission(permissions, ResourceType.SiteConfig, ActionType.Edit)
            || HasPermission(permissions, ResourceType.ClientConfig, ActionType.Edit))
        {
            rights |= RightEditGroup;
        }

        if (HasPermission(permissions, ResourceType.Deployment, ActionType.Execute))
        {
            rights |= RightRemoteCommands;
        }

        if (HasPermission(permissions, ResourceType.Agents, ActionType.Delete))
        {
            rights |= RightUninstallAgent;
        }

        return rights;
    }

    private int ApplyStrictGuardrails(int rights, IReadOnlySet<(ResourceType Resource, ActionType Action)> permissions)
    {
        if (!_options.IdentitySyncPolicyStrictMode)
        {
            return rights;
        }

        var hasAgentView = HasPermission(permissions, ResourceType.Agents, ActionType.View);
        var hasAgentExecute = HasPermission(permissions, ResourceType.Agents, ActionType.Execute);
        var hasAgentDelete = HasPermission(permissions, ResourceType.Agents, ActionType.Delete);
        var hasUserEdit = HasPermission(permissions, ResourceType.Users, ActionType.Edit);
        var hasGroupEdit = HasPermission(permissions, ResourceType.Sites, ActionType.Edit)
                           || HasPermission(permissions, ResourceType.SiteConfig, ActionType.Edit)
                           || HasPermission(permissions, ResourceType.ClientConfig, ActionType.Edit);
        var hasRemoteCommand = HasPermission(permissions, ResourceType.Deployment, ActionType.Execute);

        if (!hasAgentView)
        {
            rights &= ~RightDesktopViewOnly;
            rights &= ~RightLimitedEvents;
            rights &= ~RightRemoteControl;
            rights &= ~RightAgentConsole;
            rights &= ~RightServerFiles;
            rights &= ~RightWakeDevice;
            rights &= ~RightChatNotify;
            rights &= ~RightLimitedDesktop;
        }

        if (!hasAgentExecute)
        {
            rights &= ~RightRemoteControl;
            rights &= ~RightAgentConsole;
            rights &= ~RightServerFiles;
            rights &= ~RightWakeDevice;
            rights &= ~RightLimitedDesktop;
            rights &= ~RightPowerActions;
        }

        if (!hasRemoteCommand)
        {
            rights &= ~RightRemoteCommands;
        }

        if (!hasAgentDelete)
        {
            rights &= ~RightUninstallAgent;
        }

        if (!hasUserEdit)
        {
            rights &= ~RightManageUsers;
        }

        if (!hasGroupEdit)
        {
            rights &= ~RightEditGroup;
            rights &= ~RightManageDevices;
        }

        return rights;
    }

    private static bool HasPermission(
        IReadOnlySet<(ResourceType Resource, ActionType Action)> permissions,
        ResourceType resource,
        ActionType action)
    {
        return permissions.Contains((resource, action));
    }

    private sealed record RoleResolutionContext(
        string RoleName,
        IReadOnlySet<(ResourceType Resource, ActionType Action)> Permissions,
        int? MeshRightsMask,
        string? MeshRightsProfile);
}
