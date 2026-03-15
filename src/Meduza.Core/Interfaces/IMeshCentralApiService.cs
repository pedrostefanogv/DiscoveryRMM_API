using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IMeshCentralApiService
{
    Task<MeshCentralInstallInstructions> ProvisionInstallAsync(
        Client client,
        Site site,
        string meduzaDeployToken,
        CancellationToken cancellationToken = default);
}
