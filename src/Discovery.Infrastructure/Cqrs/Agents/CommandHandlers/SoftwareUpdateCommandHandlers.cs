using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Software.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class UpdateAgentSoftwareCommandHandler(
    IAgentRepository agentRepo,
    IAgentSoftwareRepository softwareRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<UpdateAgentSoftwareCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(UpdateAgentSoftwareCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var installed = await softwareRepo.GetByInventoryIdAsync(cmd.InventoryId);
        if (installed is null || installed.AgentId != cmd.AgentId)
            return Result<VoidResult>.Failure(Error.NotFound("Software inventory item not found for this agent."));

        // UpdatePackageId é o Id do gerenciador (winget/choco) reportado pelo
        // agent; InstallId do registro costuma ser o ProductCode do MSI.
        var packageId = (installed.UpdatePackageId ?? installed.InstallId ?? installed.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(packageId))
            return Result<VoidResult>.Failure(Error.Validation("installId",
                "O item de inventário não possui identificador de pacote para atualização."));

        var source = NormalizeUpdateSource(installed.UpdateSource, installed.Source);
        var payload = source == "chocolatey"
            ? $"choco upgrade {Quote(packageId)} -y --no-progress"
            : $"winget upgrade --id {Quote(packageId)} --silent --accept-package-agreements --accept-source-agreements";

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.PowerShell,
            Payload = payload
        };
        await dispatcher.DispatchAsync(command, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }

    private static string NormalizeUpdateSource(string? updateSource, string? source)
    {
        var value = (updateSource ?? source ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("choco"))
            return "chocolatey";
        return "winget";
    }

    // Identificadores winget/choco não contêm aspas; removê-las mantém o comando
    // íntegro caso o agent reporte um valor inesperado no inventário.
    private static string Quote(string value) => "\"" + value.Replace("\"", string.Empty) + "\"";
}
