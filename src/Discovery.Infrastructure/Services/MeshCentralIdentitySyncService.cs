using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

public class MeshCentralIdentitySyncService : IMeshCentralIdentitySyncService
{
    private readonly MeshCentralOptions _options;
    private readonly IUserRepository _userRepository;
    private readonly IUserGroupRepository _userGroupRepository;
    private readonly IClientRepository _clientRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly ISiteConfigurationRepository _siteConfigurationRepository;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly IMeshCentralAclSyncService _meshCentralAclSyncService;
    private readonly IMeshCentralIdentityMapper _meshCentralIdentityMapper;
    private readonly IMeshCentralPolicyResolver _meshCentralPolicyResolver;
    private readonly ILogger<MeshCentralIdentitySyncService> _logger;

    public MeshCentralIdentitySyncService(
        IOptions<MeshCentralOptions> options,
        IUserRepository userRepository,
        IUserGroupRepository userGroupRepository,
        IClientRepository clientRepository,
        ISiteRepository siteRepository,
        ISiteConfigurationRepository siteConfigurationRepository,
        IMeshCentralApiService meshCentralApiService,
        IMeshCentralAclSyncService meshCentralAclSyncService,
        IMeshCentralIdentityMapper meshCentralIdentityMapper,
        IMeshCentralPolicyResolver meshCentralPolicyResolver,
        ILogger<MeshCentralIdentitySyncService> logger)
    {
        _options = options.Value;
        _userRepository = userRepository;
        _userGroupRepository = userGroupRepository;
        _clientRepository = clientRepository;
        _siteRepository = siteRepository;
        _siteConfigurationRepository = siteConfigurationRepository;
        _meshCentralApiService = meshCentralApiService;
        _meshCentralAclSyncService = meshCentralAclSyncService;
        _meshCentralIdentityMapper = meshCentralIdentityMapper;
        _meshCentralPolicyResolver = meshCentralPolicyResolver;
        _logger = logger;
    }

    public async Task<MeshCentralIdentitySyncPreview> BuildPreviewAsync(
        Guid clientId,
        Guid siteId,
        string localUsername,
        CancellationToken cancellationToken = default)
    {
        var normalized = _meshCentralIdentityMapper.SuggestUsername(localUsername);
        var suggestedGroup = BuildSiteGroupName(clientId, siteId);
        var config = await _siteConfigurationRepository.GetBySiteIdAsync(siteId);

        return new MeshCentralIdentitySyncPreview
        {
            ClientId = clientId,
            SiteId = siteId,
            LocalUsername = localUsername,
            SuggestedMeshUsername = normalized,
            SuggestedGroupName = suggestedGroup,
            RequiresCreateUser = true,
            RequiresGroupBinding = string.IsNullOrWhiteSpace(config?.MeshCentralMeshId)
        };
    }

    public async Task<MeshCentralIdentitySyncResult> SyncUserOnCreateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_options.IdentitySyncEnabled)
        {
            return new MeshCentralIdentitySyncResult
            {
                UserId = userId,
                LocalLogin = string.Empty,
                Synced = false,
                MeshUsername = string.Empty,
                SiteBindingsApplied = 0,
                DeviceBindingsApplied = 0,
                DeviceBindingsRevoked = 0,
                DeviceBindingsRevocationCandidates = 0,
                Error = "MeshCentral identity sync disabled by configuration."
            };
        }

        var user = await _userRepository.GetByIdAsync(userId);
        if (user is null)
            throw new InvalidOperationException($"User '{userId}' was not found.");

        return await SyncUserInternalAsync(user, cancellationToken);
    }

    public async Task<MeshCentralIdentitySyncResult> SyncUserOnUpdatedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user is null)
            throw new InvalidOperationException($"User '{userId}' was not found.");

        if (!user.IsActive)
            return await DeprovisionUserAsync(userId, deleteRemoteUser: false, cancellationToken);

        return await SyncUserInternalAsync(user, cancellationToken);
    }

    public async Task<MeshCentralIdentitySyncResult> SyncUserScopesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user is null)
            throw new InvalidOperationException($"User '{userId}' was not found.");

        if (!user.IsActive)
            return await DeprovisionUserAsync(userId, deleteRemoteUser: false, cancellationToken);

        return await SyncUserInternalAsync(user, cancellationToken);
    }

    public async Task<MeshCentralIdentitySyncResult> DeprovisionUserAsync(
        Guid userId,
        bool deleteRemoteUser = false,
        CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        if (user is null)
            throw new InvalidOperationException($"User '{userId}' was not found.");

        if (!_options.Enabled || !_options.IdentitySyncEnabled || string.IsNullOrWhiteSpace(user.MeshCentralUserId))
        {
            user.MeshCentralSyncStatus = "disabled";
            user.MeshCentralSyncError = null;
            await _userRepository.UpdateAsync(user);

            return new MeshCentralIdentitySyncResult
            {
                UserId = user.Id,
                LocalLogin = user.Login,
                Synced = false,
                MeshUsername = user.MeshCentralUsername ?? string.Empty,
                MeshUserId = user.MeshCentralUserId,
                SiteBindingsApplied = 0,
                DeviceBindingsApplied = 0,
                DeviceBindingsRevoked = 0,
                DeviceBindingsRevocationCandidates = 0,
                Error = "MeshCentral identity sync disabled or user not provisioned remotely."
            };
        }

        try
        {
            var allMeshIds = await ResolveAllConfiguredMeshIdsAsync(cancellationToken);
            foreach (var meshId in allMeshIds)
            {
                await _meshCentralApiService.RemoveUserFromMeshAsync(user.MeshCentralUserId, meshId, cancellationToken);
            }

            var deviceAclResult = await _meshCentralAclSyncService.SyncUserDeviceAccessAsync(
                user.MeshCentralUserId,
                [],
                forceRevoke: true,
                cancellationToken);

            if (deleteRemoteUser)
            {
                await _meshCentralApiService.DeleteUserAsync(user.MeshCentralUserId, cancellationToken);
                user.MeshCentralUserId = null;
                user.MeshCentralUsername = null;
            }

            user.MeshCentralLastSyncedAt = DateTime.UtcNow;
            user.MeshCentralSyncStatus = deleteRemoteUser ? "deleted" : "revoked";
            user.MeshCentralSyncError = null;
            await _userRepository.UpdateAsync(user);

            return new MeshCentralIdentitySyncResult
            {
                UserId = user.Id,
                LocalLogin = user.Login,
                Synced = true,
                MeshUsername = user.MeshCentralUsername ?? string.Empty,
                MeshUserId = user.MeshCentralUserId,
                SiteBindingsApplied = 0,
                RightsUpdatesApplied = 0,
                DeviceBindingsApplied = deviceAclResult.DeviceBindingsApplied,
                DeviceBindingsRevoked = deviceAclResult.DeviceBindingsRevoked,
                DeviceBindingsRevocationCandidates = deviceAclResult.DeviceBindingsRevocationCandidates
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MeshCentral deprovision failed for user {UserId}", user.Id);
            user.MeshCentralSyncStatus = "failed";
            user.MeshCentralSyncError = ex.Message;
            await _userRepository.UpdateAsync(user);

            return new MeshCentralIdentitySyncResult
            {
                UserId = user.Id,
                LocalLogin = user.Login,
                Synced = false,
                MeshUsername = user.MeshCentralUsername ?? string.Empty,
                MeshUserId = user.MeshCentralUserId,
                SiteBindingsApplied = 0,
                RightsUpdatesApplied = 0,
                DeviceBindingsApplied = 0,
                DeviceBindingsRevoked = 0,
                DeviceBindingsRevocationCandidates = 0,
                Error = ex.Message
            };
        }
    }

    public async Task<MeshCentralIdentityBackfillReport> RunBackfillAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        bool applyChanges = false,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var count = await _userRepository.CountAsync();
        var users = new List<User>(count);

        for (var skip = 0; skip < count; skip += 200)
        {
            var page = await _userRepository.GetAllAsync(skip, 200);
            users.AddRange(page);
        }

        var items = new List<MeshCentralIdentityBackfillItem>();
        var synced = 0;
        var failed = 0;

        foreach (var user in users.Where(u => u.IsActive))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var policy = await _meshCentralPolicyResolver.ResolveForUserAsync(user.Id, cancellationToken);
            var siteIds = policy.Sites.Select(s => s.SiteId).ToHashSet();
            if (siteId.HasValue && !siteIds.Contains(siteId.Value))
                continue;

            if (clientId.HasValue)
            {
                var hasClientScope = false;
                foreach (var scopedSiteId in siteIds)
                {
                    var scopedSite = await _siteRepository.GetByIdAsync(scopedSiteId);
                    if (scopedSite?.ClientId == clientId.Value)
                    {
                        hasClientScope = true;
                        break;
                    }
                }

                if (!hasClientScope)
                    continue;
            }

            var previewUsername = _meshCentralIdentityMapper.SuggestUsername(user.Login, user.Id);

            if (!applyChanges)
            {
                items.Add(new MeshCentralIdentityBackfillItem
                {
                    UserId = user.Id,
                    Login = user.Login,
                    MeshUsername = previewUsername,
                    Applied = false,
                    Success = true,
                    SiteBindingsApplied = siteIds.Count,
                    RightsUpdatesApplied = 0,
                    DeviceBindingsApplied = 0,
                    DeviceBindingsRevoked = 0,
                    DeviceBindingsRevocationCandidates = 0
                });
                continue;
            }

            try
            {
                var result = await SyncUserScopesAsync(user.Id, cancellationToken);
                items.Add(new MeshCentralIdentityBackfillItem
                {
                    UserId = user.Id,
                    Login = user.Login,
                    MeshUsername = result.MeshUsername,
                    Applied = true,
                    Success = result.Synced,
                    SiteBindingsApplied = result.SiteBindingsApplied,
                    RightsUpdatesApplied = result.RightsUpdatesApplied,
                    DeviceBindingsApplied = result.DeviceBindingsApplied,
                    DeviceBindingsRevoked = result.DeviceBindingsRevoked,
                    DeviceBindingsRevocationCandidates = result.DeviceBindingsRevocationCandidates,
                    Error = result.Error
                });

                if (result.Synced)
                    synced++;
                else
                    failed++;
            }
            catch (Exception ex)
            {
                failed++;
                items.Add(new MeshCentralIdentityBackfillItem
                {
                    UserId = user.Id,
                    Login = user.Login,
                    MeshUsername = previewUsername,
                    Applied = true,
                    Success = false,
                    SiteBindingsApplied = siteIds.Count,
                    RightsUpdatesApplied = 0,
                    DeviceBindingsApplied = 0,
                    DeviceBindingsRevoked = 0,
                    DeviceBindingsRevocationCandidates = 0,
                    Error = ex.Message
                });
            }
        }

        var finished = DateTime.UtcNow;
        return new MeshCentralIdentityBackfillReport
        {
            ApplyChanges = applyChanges,
            StartedAtUtc = started,
            FinishedAtUtc = finished,
            TotalUsers = items.Count,
            SyncedUsers = synced,
            FailedUsers = failed,
            Items = items
        };
    }

    private async Task<MeshCentralIdentitySyncResult> SyncUserInternalAsync(User user, CancellationToken cancellationToken)
    {
        var policy = await _meshCentralPolicyResolver.ResolveForUserAsync(user.Id, cancellationToken);
        var effectiveSitePolicies = BuildEffectiveSitePolicies(policy.Sites);
        var meshUsername = _meshCentralIdentityMapper.ResolveProvisioningUsername(user);

        try
        {
            var upsert = await _meshCentralApiService.EnsureUserAsync(user, meshUsername, cancellationToken);
            var desiredMeshRights = await ResolveDesiredMeshMembershipRightsAsync(effectiveSitePolicies, cancellationToken);

            var desiredMeshIds = desiredMeshRights.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allMeshIds = await ResolveAllConfiguredMeshIdsAsync(cancellationToken);
            var rightsUpdatesApplied = 0;

            foreach (var target in desiredMeshRights)
            {
                var membershipResult = await _meshCentralApiService.EnsureUserInMeshAsync(
                    upsert.UserId,
                    target.Key,
                    target.Value,
                    cancellationToken);

                if (membershipResult.RightsUpdated)
                {
                    rightsUpdatesApplied++;
                }
            }

            foreach (var meshId in allMeshIds.Except(desiredMeshIds, StringComparer.OrdinalIgnoreCase))
            {
                await _meshCentralApiService.RemoveUserFromMeshAsync(upsert.UserId, meshId, cancellationToken);
            }

            var deviceAclResult = await _meshCentralAclSyncService.SyncUserDeviceAccessAsync(
                upsert.UserId,
                effectiveSitePolicies,
                forceRevoke: false,
                cancellationToken);

            user.MeshCentralUserId = upsert.UserId;
            user.MeshCentralUsername = upsert.Username;
            user.MeshCentralLastSyncedAt = DateTime.UtcNow;
            user.MeshCentralSyncStatus = "synced";
            user.MeshCentralSyncError = null;
            await _userRepository.UpdateAsync(user);

            return new MeshCentralIdentitySyncResult
            {
                UserId = user.Id,
                LocalLogin = user.Login,
                Synced = true,
                MeshUsername = upsert.Username,
                MeshUserId = upsert.UserId,
                SiteBindingsApplied = desiredMeshIds.Count,
                RightsUpdatesApplied = rightsUpdatesApplied,
                DeviceBindingsApplied = deviceAclResult.DeviceBindingsApplied,
                DeviceBindingsRevoked = deviceAclResult.DeviceBindingsRevoked,
                DeviceBindingsRevocationCandidates = deviceAclResult.DeviceBindingsRevocationCandidates
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MeshCentral sync failed for user {UserId}", user.Id);
            user.MeshCentralSyncStatus = "failed";
            user.MeshCentralSyncError = ex.Message;
            await _userRepository.UpdateAsync(user);

            return new MeshCentralIdentitySyncResult
            {
                UserId = user.Id,
                LocalLogin = user.Login,
                Synced = false,
                MeshUsername = meshUsername,
                SiteBindingsApplied = 0,
                RightsUpdatesApplied = 0,
                DeviceBindingsApplied = 0,
                DeviceBindingsRevoked = 0,
                DeviceBindingsRevocationCandidates = 0,
                Error = ex.Message
            };
        }
    }

    private IReadOnlyCollection<MeshCentralSitePolicyResolution> BuildEffectiveSitePolicies(
        IReadOnlyCollection<MeshCentralSitePolicyResolution> sitePolicies)
    {
        if (!_options.IdentitySyncPolicyEnabled)
        {
            return sitePolicies
                .Select(policy => new MeshCentralSitePolicyResolution
                {
                    SiteId = policy.SiteId,
                    MeshRights = _options.IdentitySyncDefaultMeshRights,
                    MeshMembershipRights = _options.IdentitySyncMeshMembershipRights,
                    Sources = ["default"]
                })
                .ToArray();
        }

        if (!_options.IdentitySyncPolicyDryRun)
            return sitePolicies;

        return sitePolicies
            .Select(policy =>
            {
                _logger.LogInformation(
                    "MeshCentral policy dry-run user-site={SiteId} desiredRights={DesiredRights} fallbackRights={FallbackRights}",
                    policy.SiteId,
                    policy.MeshRights,
                    _options.IdentitySyncDefaultMeshRights);

                return new MeshCentralSitePolicyResolution
                {
                    SiteId = policy.SiteId,
                    MeshRights = _options.IdentitySyncDefaultMeshRights,
                    MeshMembershipRights = _options.IdentitySyncMeshMembershipRights,
                    Sources = policy.Sources
                };
            })
            .ToArray();
    }

    private async Task<HashSet<Guid>> ResolveUserSiteScopesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var siteIds = new HashSet<Guid>();
        var roles = await _userGroupRepository.GetRolesForUserAsync(userId);

        foreach (var role in roles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (role.ScopeLevel == ScopeLevel.Site && role.ScopeId.HasValue)
            {
                siteIds.Add(role.ScopeId.Value);
                continue;
            }

            if (role.ScopeLevel == ScopeLevel.Client && role.ScopeId.HasValue)
            {
                var sites = await _siteRepository.GetByClientIdAsync(role.ScopeId.Value, includeInactive: false);
                foreach (var site in sites)
                {
                    siteIds.Add(site.Id);
                }
            }

            if (role.ScopeLevel != ScopeLevel.Global)
                continue;

            var clients = await _clientRepository.GetAllAsync(includeInactive: false);
            foreach (var client in clients)
            {
                var sites = await _siteRepository.GetByClientIdAsync(client.Id, includeInactive: false);
                foreach (var site in sites)
                {
                    siteIds.Add(site.Id);
                }
            }
        }

        return siteIds;
    }

    private async Task<HashSet<string>> ResolveDesiredMeshIdsAsync(HashSet<Guid> siteIds, CancellationToken cancellationToken)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var siteId in siteIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = await _siteConfigurationRepository.GetBySiteIdAsync(siteId);
            if (!string.IsNullOrWhiteSpace(config?.MeshCentralMeshId))
            {
                desired.Add(config.MeshCentralMeshId);
            }
        }

        return desired;
    }

    private async Task<Dictionary<string, int>> ResolveDesiredMeshMembershipRightsAsync(
        IReadOnlyCollection<MeshCentralSitePolicyResolution> sitePolicies,
        CancellationToken cancellationToken)
    {
        var desired = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sitePolicy in sitePolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = await _siteConfigurationRepository.GetBySiteIdAsync(sitePolicy.SiteId);
            if (string.IsNullOrWhiteSpace(config?.MeshCentralMeshId))
            {
                continue;
            }

            if (desired.TryGetValue(config.MeshCentralMeshId, out var existingRights))
            {
                desired[config.MeshCentralMeshId] = existingRights | sitePolicy.MeshMembershipRights;
                continue;
            }

            desired[config.MeshCentralMeshId] = sitePolicy.MeshMembershipRights;
        }

        return desired;
    }

    private async Task<HashSet<string>> ResolveAllConfiguredMeshIdsAsync(CancellationToken cancellationToken)
    {
        var meshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clients = await _clientRepository.GetAllAsync(includeInactive: false);

        foreach (var client in clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configs = await _siteConfigurationRepository.GetByClientIdAsync(client.Id);
            foreach (var config in configs)
            {
                if (!string.IsNullOrWhiteSpace(config.MeshCentralMeshId))
                {
                    meshes.Add(config.MeshCentralMeshId);
                }
            }
        }

        return meshes;
    }

    private static string BuildSiteGroupName(Guid clientId, Guid siteId)
    {
        return $"discovery-{clientId:N}-{siteId:N}";
    }
}
