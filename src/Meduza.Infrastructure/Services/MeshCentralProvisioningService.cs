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
    private readonly ISiteConfigurationRepository _siteConfigurationRepository;

    public MeshCentralProvisioningService(
        IOptions<MeshCentralOptions> options,
        ISiteConfigurationRepository siteConfigurationRepository)
    {
        _options = options.Value;
        _siteConfigurationRepository = siteConfigurationRepository;
    }

    public async Task<MeshCentralInstallInstructions> BuildInstallInstructionsAsync(
        Client client,
        Site site,
        string meduzaDeployToken,
        bool meshCentralEnabledForScope = true)
    {
        if (!meshCentralEnabledForScope)
            throw new InvalidOperationException("MeshCentral support is disabled for this scope.");

        if (!_options.Enabled || !_options.EnableProvisioningHints)
            throw new InvalidOperationException("MeshCentral provisioning hints are disabled.");

        _ = meduzaDeployToken;

        var siteConfig = await _siteConfigurationRepository.GetBySiteIdAsync(site.Id);
        if (string.IsNullOrWhiteSpace(siteConfig?.MeshCentralMeshId))
            throw new InvalidOperationException("MeshCentral mesh binding is not available for this site.");

        var groupName = string.IsNullOrWhiteSpace(siteConfig.MeshCentralGroupName)
            ? BuildGroupName(client, site)
            : siteConfig.MeshCentralGroupName!;
        var installUrl = MeshCentralInstallUrlBuilder.BuildDirectInstallUrl(_options, siteConfig.MeshCentralMeshId!);

        var installMode = ResolveInstallMode(_options.InstallExecutionMode);
        var windowsBackground = BuildWindowsCommandBackground(installUrl);
        var windowsInteractive = BuildWindowsCommandInteractive(installUrl);
        var linuxBackground = BuildLinuxCommandBackground(installUrl);
        var linuxInteractive = BuildLinuxCommandInteractive(installUrl);

        return new MeshCentralInstallInstructions
        {
            GroupName = groupName,
            MeshId = siteConfig.MeshCentralMeshId,
            InstallUrl = installUrl,
            InstallMode = installMode,
            WindowsCommandBackground = windowsBackground,
            WindowsCommandInteractive = windowsInteractive,
            LinuxCommandBackground = linuxBackground,
            LinuxCommandInteractive = linuxInteractive,
            WindowsCommand = installMode == "interactive" ? windowsInteractive : windowsBackground,
            LinuxCommand = installMode == "interactive" ? linuxInteractive : linuxBackground
        };
    }

    private static string ResolveInstallMode(string? raw)
    {
        return string.Equals(raw, "interactive", StringComparison.OrdinalIgnoreCase)
            ? "interactive"
            : "background";
    }

    private static string BuildWindowsCommandInteractive(string installUrl)
    {
        return $"powershell -ExecutionPolicy Bypass -Command \"iwr -UseBasicParsing '{installUrl}' -OutFile meshcentral-agent.exe; .\\meshcentral-agent.exe\"";
    }

    private static string BuildWindowsCommandBackground(string installUrl)
    {
        return $"powershell -ExecutionPolicy Bypass -Command \"iwr -UseBasicParsing '{installUrl}' -OutFile meshcentral-agent.exe; Start-Process -FilePath .\\meshcentral-agent.exe -WindowStyle Hidden\"";
    }

    private static string BuildLinuxCommandInteractive(string installUrl)
    {
        return $"curl -fsSL '{installUrl}' | sh";
    }

    private static string BuildLinuxCommandBackground(string installUrl)
    {
        return $"nohup sh -c \"curl -fsSL '{installUrl}' | sh\" >/tmp/meshcentral-agent-install.log 2>&1 &";
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
