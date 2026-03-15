using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IMeshCentralProvisioningService
{
    MeshCentralInstallInstructions BuildInstallInstructions(Client client, Site site, string meduzaDeployToken);
}

public sealed class MeshCentralInstallInstructions
{
    public required string GroupName { get; init; }
    public required string InstallUrl { get; init; }
    public required string WindowsCommand { get; init; }
    public required string LinuxCommand { get; init; }
}
