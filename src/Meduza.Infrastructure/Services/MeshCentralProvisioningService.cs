using System.Text.RegularExpressions;
using Meduza.Core.Configuration;
using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Meduza.Infrastructure.Services;

public class MeshCentralProvisioningService : IMeshCentralProvisioningService
{
    private static readonly Regex InvalidGroupChars = new("[^a-zA-Z0-9._ -]", RegexOptions.Compiled);

    private readonly MeshCentralOptions _options;

    public MeshCentralProvisioningService(IOptions<MeshCentralOptions> options)
    {
        _options = options.Value;
    }

    public MeshCentralInstallInstructions BuildInstallInstructions(Client client, Site site, string meduzaDeployToken)
    {
        if (!_options.Enabled || !_options.EnableProvisioningHints)
            throw new InvalidOperationException("MeshCentral provisioning hints are disabled.");

        if (string.IsNullOrWhiteSpace(_options.AgentInstallUrlTemplate))
            throw new InvalidOperationException("MeshCentral AgentInstallUrlTemplate is not configured.");

        var groupName = BuildGroupName(client, site);
        var installUrl = _options.AgentInstallUrlTemplate
            .Replace("{CLIENT_ID}", client.Id.ToString("D"), StringComparison.Ordinal)
            .Replace("{SITE_ID}", site.Id.ToString("D"), StringComparison.Ordinal)
            .Replace("{CLIENT_NAME}", Uri.EscapeDataString(client.Name), StringComparison.Ordinal)
            .Replace("{SITE_NAME}", Uri.EscapeDataString(site.Name), StringComparison.Ordinal)
            .Replace("{GROUP_NAME}", Uri.EscapeDataString(groupName), StringComparison.Ordinal)
            .Replace("{MEDUZA_DEPLOY_TOKEN}", Uri.EscapeDataString(meduzaDeployToken), StringComparison.Ordinal);

        var windowsCommand = $"powershell -ExecutionPolicy Bypass -Command \"iwr -UseBasicParsing '{installUrl}' -OutFile meshcentral-agent.exe; .\\meshcentral-agent.exe\"";
        var linuxCommand = $"curl -fsSL '{installUrl}' | sh";

        return new MeshCentralInstallInstructions
        {
            GroupName = groupName,
            InstallUrl = installUrl,
            WindowsCommand = windowsCommand,
            LinuxCommand = linuxCommand
        };
    }

    private static string BuildGroupName(Client client, Site site)
    {
        var clientPart = Normalize(client.Name);
        var sitePart = Normalize(site.Name);
        return $"meduza-{clientPart}-{sitePart}";
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "unknown";

        var sanitized = InvalidGroupChars.Replace(trimmed, "-");
        return Regex.Replace(sanitized, "\\s+", "-").ToLowerInvariant();
    }
}
