using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class MeshCentralGroupPolicySyncService : IMeshCentralGroupPolicySyncService
{
    private readonly IClientRepository _clientRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly ISiteConfigurationRepository _siteConfigurationRepository;
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly ILogger<MeshCentralGroupPolicySyncService> _logger;

    public MeshCentralGroupPolicySyncService(
        IClientRepository clientRepository,
        ISiteRepository siteRepository,
        ISiteConfigurationRepository siteConfigurationRepository,
        IConfigurationResolver configurationResolver,
        IMeshCentralApiService meshCentralApiService,
        ILogger<MeshCentralGroupPolicySyncService> logger)
    {
        _clientRepository = clientRepository;
        _siteRepository = siteRepository;
        _siteConfigurationRepository = siteConfigurationRepository;
        _configurationResolver = configurationResolver;
        _meshCentralApiService = meshCentralApiService;
        _logger = logger;
    }

    public async Task<MeshCentralGroupPolicySiteStatus> GetSiteStatusAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        var site = await _siteRepository.GetByIdAsync(siteId)
            ?? throw new InvalidOperationException($"Site '{siteId}' not found.");

        var client = await _clientRepository.GetByIdAsync(site.ClientId)
            ?? throw new InvalidOperationException($"Client '{site.ClientId}' not found.");

        var siteConfig = await _siteConfigurationRepository.GetBySiteIdAsync(site.Id);
        var resolved = await _configurationResolver.ResolveForSiteAsync(site.Id);

        return BuildStatus(siteConfig, client, site, resolved.SupportEnabled, resolved.MeshCentralGroupPolicyProfile);
    }

    public async Task<MeshCentralGroupPolicyBackfillReport> RunBackfillAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        bool applyChanges = false,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var items = new List<MeshCentralGroupPolicyBackfillItem>();
        var updated = 0;
        var drifted = 0;
        var failed = 0;

        var targets = await ResolveTargetsAsync(clientId, siteId, cancellationToken);

        foreach (var (client, site) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var siteConfig = await _siteConfigurationRepository.GetBySiteIdAsync(site.Id);
                var resolved = await _configurationResolver.ResolveForSiteAsync(site.Id);
                var status = BuildStatus(siteConfig, client, site, resolved.SupportEnabled, resolved.MeshCentralGroupPolicyProfile);

                if (status.HasDrift)
                    drifted++;

                if (!applyChanges || !status.SupportEnabled || !status.HasDrift)
                {
                    items.Add(new MeshCentralGroupPolicyBackfillItem
                    {
                        ClientId = client.Id,
                        SiteId = site.Id,
                        ClientName = client.Name,
                        SiteName = site.Name,
                        SupportEnabled = status.SupportEnabled,
                        DesiredProfile = status.DesiredProfile,
                        AppliedProfileBefore = status.AppliedProfile,
                        AppliedProfileAfter = status.AppliedProfile,
                        MeshIdBefore = status.MeshId,
                        MeshIdAfter = status.MeshId,
                        GroupNameBefore = status.GroupName,
                        GroupNameAfter = status.GroupName,
                        HasDrift = status.HasDrift,
                        Applied = false,
                        Success = true,
                        DriftReasons = status.DriftReasons
                    });
                    continue;
                }

                var sync = await _meshCentralApiService.EnsureSiteGroupBindingAsync(
                    client,
                    site,
                    status.DesiredProfile,
                    cancellationToken);

                updated++;
                items.Add(new MeshCentralGroupPolicyBackfillItem
                {
                    ClientId = client.Id,
                    SiteId = site.Id,
                    ClientName = client.Name,
                    SiteName = site.Name,
                    SupportEnabled = true,
                    DesiredProfile = status.DesiredProfile,
                    AppliedProfileBefore = sync.PreviousAppliedProfile,
                    AppliedProfileAfter = sync.AppliedProfile,
                    MeshIdBefore = sync.PreviousMeshId,
                    MeshIdAfter = sync.MeshId,
                    GroupNameBefore = sync.PreviousGroupName,
                    GroupNameAfter = sync.GroupName,
                    HasDrift = true,
                    Applied = true,
                    Success = true,
                    DriftReasons = status.DriftReasons
                });
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "MeshCentral group policy reconciliation failed for site {SiteId}.", site.Id);
                items.Add(new MeshCentralGroupPolicyBackfillItem
                {
                    ClientId = site.ClientId,
                    SiteId = site.Id,
                    ClientName = client.Name,
                    SiteName = site.Name,
                    SupportEnabled = false,
                    DesiredProfile = string.Empty,
                    HasDrift = true,
                    Applied = false,
                    Success = false,
                    Error = ex.Message,
                    DriftReasons = ["error"]
                });
            }
        }

        return new MeshCentralGroupPolicyBackfillReport
        {
            StartedAtUtc = startedAt,
            FinishedAtUtc = DateTime.UtcNow,
            ApplyChanges = applyChanges,
            TotalSites = targets.Count,
            UpdatedSites = updated,
            DriftedSites = drifted,
            FailedSites = failed,
            Items = items
        };
    }

    private static MeshCentralGroupPolicySiteStatus BuildStatus(
        SiteConfiguration? siteConfig,
        Client client,
        Site site,
        bool supportEnabled,
        string desiredProfile)
    {
        var reasons = new List<string>();

        if (!supportEnabled)
            reasons.Add("support-disabled");

        if (string.IsNullOrWhiteSpace(siteConfig?.MeshCentralGroupName))
            reasons.Add("group-name-missing");

        if (string.IsNullOrWhiteSpace(siteConfig?.MeshCentralMeshId))
            reasons.Add("mesh-id-missing");

        if (!string.Equals(siteConfig?.MeshCentralAppliedGroupPolicyProfile, desiredProfile, StringComparison.OrdinalIgnoreCase))
            reasons.Add("profile-mismatch");

        return new MeshCentralGroupPolicySiteStatus
        {
            ClientId = client.Id,
            SiteId = site.Id,
            ClientName = client.Name,
            SiteName = site.Name,
            SupportEnabled = supportEnabled,
            DesiredProfile = desiredProfile,
            AppliedProfile = siteConfig?.MeshCentralAppliedGroupPolicyProfile,
            MeshId = siteConfig?.MeshCentralMeshId,
            GroupName = siteConfig?.MeshCentralGroupName,
            AppliedAtUtc = siteConfig?.MeshCentralAppliedGroupPolicyAt,
            HasDrift = reasons.Count > 0,
            DriftReasons = reasons.ToArray()
        };
    }

    private async Task<List<(Client client, Site site)>> ResolveTargetsAsync(
        Guid? clientId,
        Guid? siteId,
        CancellationToken cancellationToken)
    {
        var targets = new List<(Client client, Site site)>();

        if (siteId.HasValue)
        {
            var site = await _siteRepository.GetByIdAsync(siteId.Value)
                ?? throw new InvalidOperationException($"Site '{siteId.Value}' not found.");

            var client = await _clientRepository.GetByIdAsync(site.ClientId)
                ?? throw new InvalidOperationException($"Client '{site.ClientId}' not found.");

            if (clientId.HasValue && client.Id != clientId.Value)
                throw new InvalidOperationException("Provided clientId does not match site client.");

            targets.Add((client, site));
            return targets;
        }

        if (clientId.HasValue)
        {
            var client = await _clientRepository.GetByIdAsync(clientId.Value)
                ?? throw new InvalidOperationException($"Client '{clientId.Value}' not found.");

            var sites = await _siteRepository.GetByClientIdAsync(client.Id, includeInactive: false);
            foreach (var site in sites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                targets.Add((client, site));
            }

            return targets;
        }

        var clients = await _clientRepository.GetAllAsync(includeInactive: false);
        foreach (var client in clients)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sites = await _siteRepository.GetByClientIdAsync(client.Id, includeInactive: false);
            foreach (var site in sites)
            {
                targets.Add((client, site));
            }
        }

        return targets;
    }
}
