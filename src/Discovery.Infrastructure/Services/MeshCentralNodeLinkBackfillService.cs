using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class MeshCentralNodeLinkBackfillService : IMeshCentralNodeLinkBackfillService
{
    private readonly IAgentRepository _agentRepository;
    private readonly ISiteConfigurationRepository _siteConfigurationRepository;
    private readonly IMeshCentralApiService _meshCentralApiService;
    private readonly ILogger<MeshCentralNodeLinkBackfillService> _logger;

    public MeshCentralNodeLinkBackfillService(
        IAgentRepository agentRepository,
        ISiteConfigurationRepository siteConfigurationRepository,
        IMeshCentralApiService meshCentralApiService,
        ILogger<MeshCentralNodeLinkBackfillService> logger)
    {
        _agentRepository = agentRepository;
        _siteConfigurationRepository = siteConfigurationRepository;
        _meshCentralApiService = meshCentralApiService;
        _logger = logger;
    }

    public async Task<MeshCentralNodeLinkBackfillReport> RunBackfillAsync(
        Guid? clientId = null,
        Guid? siteId = null,
        bool applyChanges = false,
        CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTime.UtcNow;
        var agents = await LoadAgentsAsync(clientId, siteId);
        var items = new List<MeshCentralNodeLinkBackfillItem>();
        var siteConfigCache = new Dictionary<Guid, SiteConfiguration?>(agents.Count);
        var meshNodesCache = new Dictionary<string, IReadOnlyCollection<MeshCentralNodeRef>>(StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        var verified = 0;
        var missing = 0;
        var ambiguous = 0;

        foreach (var agent in agents.OrderBy(a => a.SiteId).ThenBy(a => a.Hostname, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!siteConfigCache.TryGetValue(agent.SiteId, out var siteConfig))
                {
                    siteConfig = await _siteConfigurationRepository.GetBySiteIdAsync(agent.SiteId);
                    siteConfigCache[agent.SiteId] = siteConfig;
                }

                if (string.IsNullOrWhiteSpace(siteConfig?.MeshCentralMeshId))
                {
                    missing++;
                    items.Add(BuildItem(agent, "missing-mesh", candidateNodeIds: [], error: "Site has no MeshCentral mesh binding."));
                    continue;
                }

                if (!meshNodesCache.TryGetValue(siteConfig.MeshCentralMeshId, out var meshNodes))
                {
                    meshNodes = await _meshCentralApiService.ListNodesAsync(siteConfig.MeshCentralMeshId, cancellationToken);
                    meshNodesCache[siteConfig.MeshCentralMeshId] = meshNodes;
                }

                var currentNodeId = NormalizeNodeId(agent.MeshCentralNodeId);
                if (!string.IsNullOrWhiteSpace(currentNodeId)
                    && meshNodes.Any(node => string.Equals(node.NodeId, currentNodeId, StringComparison.OrdinalIgnoreCase)))
                {
                    verified++;
                    items.Add(BuildItem(agent, "verified", currentNodeId, currentNodeId, false, []));
                    continue;
                }

                var candidates = FindCandidates(agent, meshNodes);
                if (candidates.Count == 1)
                {
                    var suggestedNodeId = candidates[0].NodeId;
                    var applied = false;

                    if (applyChanges)
                    {
                        agent.MeshCentralNodeId = suggestedNodeId;
                        await _agentRepository.UpdateAsync(agent);
                        applied = true;
                        updated++;
                    }
                    else
                    {
                        missing++;
                    }

                    items.Add(BuildItem(
                        agent,
                        applied ? "linked" : "suggested",
                        currentNodeId,
                        suggestedNodeId,
                        applied,
                        candidates.Select(candidate => candidate.NodeId).ToArray()));
                    continue;
                }

                if (candidates.Count == 0)
                {
                    missing++;
                    items.Add(BuildItem(agent, "unmatched", currentNodeId, candidateNodeIds: []));
                    continue;
                }

                ambiguous++;
                items.Add(BuildItem(
                    agent,
                    "ambiguous",
                    currentNodeId,
                    candidateNodeIds: candidates.Select(candidate => candidate.NodeId).ToArray(),
                    error: $"Found {candidates.Count} candidate nodes in mesh {siteConfig.MeshCentralMeshId}."));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MeshCentral node link backfill failed for agent {AgentId}", agent.Id);
                items.Add(BuildItem(agent, "error", agent.MeshCentralNodeId, candidateNodeIds: [], error: ex.Message));
            }
        }

        return new MeshCentralNodeLinkBackfillReport
        {
            ApplyChanges = applyChanges,
            StartedAtUtc = startedAtUtc,
            FinishedAtUtc = DateTime.UtcNow,
            TotalAgents = agents.Count,
            UpdatedAgents = updated,
            VerifiedAgents = verified,
            MissingAgents = missing,
            AmbiguousAgents = ambiguous,
            Items = items
        };
    }

    private async Task<List<Agent>> LoadAgentsAsync(Guid? clientId, Guid? siteId)
    {
        if (siteId.HasValue)
            return (await _agentRepository.GetBySiteIdAsync(siteId.Value)).ToList();

        if (clientId.HasValue)
            return (await _agentRepository.GetByClientIdAsync(clientId.Value)).ToList();

        return (await _agentRepository.GetAllAsync()).ToList();
    }

    private static List<MeshCentralNodeRef> FindCandidates(Agent agent, IReadOnlyCollection<MeshCentralNodeRef> nodes)
    {
        var agentKeys = BuildMatchKeys(agent.Hostname, agent.DisplayName);
        if (agentKeys.Count == 0)
            return [];

        return nodes
            .Where(node => agentKeys.Overlaps(BuildMatchKeys(node.Hostname, node.Name)))
            .OrderBy(node => node.Hostname ?? node.Name ?? node.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static HashSet<string> BuildMatchKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var normalized = NormalizeMatchValue(value);
            if (!string.IsNullOrWhiteSpace(normalized))
                keys.Add(normalized);
        }

        return keys;
    }

    private static string? NormalizeMatchValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeNodeId(string? meshNodeId)
    {
        if (string.IsNullOrWhiteSpace(meshNodeId))
            return null;

        var normalized = meshNodeId.Trim();
        return normalized.StartsWith("node/", StringComparison.OrdinalIgnoreCase) ? normalized : null;
    }

    private static MeshCentralNodeLinkBackfillItem BuildItem(
        Agent agent,
        string status,
        string? currentNodeId = null,
        string? suggestedNodeId = null,
        bool applied = false,
        IReadOnlyCollection<string>? candidateNodeIds = null,
        string? error = null)
    {
        return new MeshCentralNodeLinkBackfillItem
        {
            AgentId = agent.Id,
            SiteId = agent.SiteId,
            Hostname = agent.Hostname,
            DisplayName = agent.DisplayName,
            CurrentNodeId = string.IsNullOrWhiteSpace(currentNodeId) ? agent.MeshCentralNodeId : currentNodeId,
            SuggestedNodeId = suggestedNodeId,
            Status = status,
            Applied = applied,
            CandidateNodeIds = candidateNodeIds ?? [],
            Error = error
        };
    }
}