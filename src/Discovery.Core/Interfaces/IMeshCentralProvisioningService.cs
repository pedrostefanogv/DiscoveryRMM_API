using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IMeshCentralProvisioningService
{
    Task<MeshCentralInstallInstructions> BuildInstallInstructionsAsync(
        Client client,
        Site site,
        string discoveryDeployToken,
        bool meshCentralEnabledForScope = true);
}

public sealed class MeshCentralInstallInstructions
{
    public required string GroupName { get; init; }
    public string? MeshId { get; init; }
    public required string InstallUrl { get; init; }
    public string InstallMode { get; init; } = "background";
    public required string WindowsCommandBackground { get; init; }
    public required string WindowsCommandInteractive { get; init; }
    public required string LinuxCommandBackground { get; init; }
    public required string LinuxCommandInteractive { get; init; }
    public required string WindowsCommand { get; init; }
    public required string LinuxCommand { get; init; }
}
