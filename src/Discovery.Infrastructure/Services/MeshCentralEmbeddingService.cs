using System.Text;
using System.Text.RegularExpressions;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

public class MeshCentralEmbeddingService : IMeshCentralEmbeddingService
{
    private static readonly Regex ValidUsername = new("^[a-zA-Z0-9._@-]{1,64}$", RegexOptions.Compiled);

    private readonly MeshCentralOptions _options;
    private readonly IMeshCentralConfigService _meshCentralConfigService;
    private readonly IMeshCentralTokenService _meshCentralTokenService;

    public MeshCentralEmbeddingService(
        IOptions<MeshCentralOptions> options,
        IMeshCentralConfigService meshCentralConfigService,
        IMeshCentralTokenService meshCentralTokenService)
    {
        _options = options.Value;
        _meshCentralConfigService = meshCentralConfigService;
        _meshCentralTokenService = meshCentralTokenService;
    }

    public Task<MeshCentralEmbedUrlResult> GenerateAgentEmbedUrlAsync(
        Agent agent,
        Guid clientId,
        int viewMode,
        int? hideMask,
        string? meshNodeId,
        string? gotoDeviceName,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("MeshCentral integration is disabled.");

        if (Array.IndexOf(_options.AllowedViewModes, viewMode) < 0)
            throw new InvalidOperationException($"ViewMode {viewMode} is not allowed.");

        var baseUri = _meshCentralConfigService.GetPublicBaseUrl();
        var loginToken = _meshCentralTokenService.GenerateLoginToken(_meshCentralConfigService.GetTechnicalUsername());
        var effectiveMeshNodeId = string.IsNullOrWhiteSpace(agent.MeshCentralNodeId)
            ? meshNodeId
            : agent.MeshCentralNodeId;

        var query = new Dictionary<string, string>
        {
            ["login"] = loginToken,
            ["viewmode"] = viewMode.ToString(),
            ["hide"] = (hideMask ?? _options.DefaultHideMask).ToString(),
            ["discoveryClientId"] = clientId.ToString("D"),
            ["discoverySiteId"] = agent.SiteId.ToString("D"),
            ["discoveryAgentId"] = agent.Id.ToString("D")
        };

        if (!string.IsNullOrWhiteSpace(effectiveMeshNodeId))
        {
            query["gotonode"] = effectiveMeshNodeId;
        }
        else
        {
            var targetDeviceName = string.IsNullOrWhiteSpace(gotoDeviceName)
                ? (agent.DisplayName ?? agent.Hostname)
                : gotoDeviceName;

            if (!string.IsNullOrWhiteSpace(targetDeviceName))
                query["gotodevicename"] = targetDeviceName;
        }

        var url = BuildUrl(baseUri, query);
        var expiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SuggestedSessionMinutes));

        return Task.FromResult(new MeshCentralEmbedUrlResult
        {
            Url = url,
            ExpiresAtUtc = expiresAt,
            ViewMode = viewMode,
            HideMask = hideMask ?? _options.DefaultHideMask
        });
    }

    public Task<MeshCentralEmbedUrlResult> GenerateUserEmbedUrlAsync(
        string meshUsername,
        Guid clientId,
        Guid siteId,
        Guid? agentId,
        int viewMode,
        int? hideMask,
        string? meshNodeId,
        string? gotoDeviceName,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("MeshCentral integration is disabled.");

        if (string.IsNullOrWhiteSpace(meshUsername) || !ValidUsername.IsMatch(meshUsername))
            throw new InvalidOperationException("Mesh username is invalid.");

        if (Array.IndexOf(_options.AllowedViewModes, viewMode) < 0)
            throw new InvalidOperationException($"ViewMode {viewMode} is not allowed.");

        var loginToken = _meshCentralTokenService.GenerateLoginToken(meshUsername);
        var baseUri = _meshCentralConfigService.GetPublicBaseUrl();

        var query = new Dictionary<string, string>
        {
            ["login"] = loginToken,
            ["viewmode"] = viewMode.ToString(),
            ["hide"] = (hideMask ?? _options.DefaultHideMask).ToString(),
            ["discoveryClientId"] = clientId.ToString("D"),
            ["discoverySiteId"] = siteId.ToString("D")
        };

        if (agentId.HasValue)
            query["discoveryAgentId"] = agentId.Value.ToString("D");

        if (!string.IsNullOrWhiteSpace(meshNodeId))
        {
            query["gotonode"] = meshNodeId;
        }
        else if (!string.IsNullOrWhiteSpace(gotoDeviceName))
        {
            query["gotodevicename"] = gotoDeviceName;
        }

        var url = BuildUrl(baseUri, query);
        var expiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.SuggestedSessionMinutes));

        return Task.FromResult(new MeshCentralEmbedUrlResult
        {
            Url = url,
            ExpiresAtUtc = expiresAt,
            ViewMode = viewMode,
            HideMask = hideMask ?? _options.DefaultHideMask
        });
    }

    private static string BuildUrl(string baseUrl, Dictionary<string, string> query)
    {
        var sb = new StringBuilder(baseUrl);
        sb.Append(baseUrl.Contains('?') ? '&' : '?');

        var first = true;
        foreach (var kv in query)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }

        return sb.ToString();
    }
}
