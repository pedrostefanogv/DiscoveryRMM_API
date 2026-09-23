using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Software.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class UpdateAgentSoftwareCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IAgentSoftwareRepository softwareRepo,
    IAppStoreService appStoreService,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
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
        var installationType = source == "chocolatey" ? AppInstallationType.Chocolatey : AppInstallationType.Winget;

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var approved = await appStoreService.IsPackageApprovedAsync(
            site?.ClientId, agent.SiteId, cmd.AgentId, installationType, packageId, ct);

        if (!approved && !cmd.ConfirmUnapproved)
        {
            return Result<VoidResult>.Failure(Error.Conflict(
                "Este aplicativo não está aprovado na loja para este agente. Confirme para atualizar mesmo assim."));
        }

        var payload = JsonSerializer.Serialize(new
        {
            packageId,
            installationType = source,
            source
        });

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.SoftwareUpdate,
            Payload = payload
        };
        var created = await dispatcher.DispatchAsync(command, ct);

        // Auditoria: registra a execução no histórico de operações de automação,
        // atualizado com o resultado quando o agent responde.
        await RunAutomationTaskCommandHandler.CreateReportAsync(
            reportRepo, created, null, null, AutomationExecutionSourceType.SoftwareUpdate,
            new { inventoryId = cmd.InventoryId, packageId, source, approved, confirmUnapproved = cmd.ConfirmUnapproved });

        return Result<VoidResult>.Success(VoidResult.Value);
    }

    private static string NormalizeUpdateSource(string? updateSource, string? source)
    {
        var value = (updateSource ?? source ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("choco"))
            return "chocolatey";
        return "winget";
    }
}

public sealed class UninstallAgentSoftwareCommandHandler(
    IAgentRepository agentRepo,
    IAgentSoftwareRepository softwareRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<UninstallAgentSoftwareCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(UninstallAgentSoftwareCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var installed = await softwareRepo.GetByInventoryIdAsync(cmd.InventoryId);
        if (installed is null || installed.AgentId != cmd.AgentId)
            return Result<VoidResult>.Failure(Error.NotFound("Software inventory item not found for this agent."));

        var source = NormalizeUpdateSource(installed.UpdateSource, installed.Source);
        // Id do gerenciador, quando o agent o reconheceu (winget list).
        var packageId = (installed.UpdatePackageId ?? string.Empty).Trim();

        var payload = JsonSerializer.Serialize(new
        {
            name = installed.Name,
            packageId,
            installationType = source,
            source,
            // Identificadores do registro, usados pelo agent como fallback:
            // MSI ProductCode e UninstallString/InstallLocation.
            installId = installed.InstallId,
            serial = installed.Serial,
            installSource = installed.InstallSource
        });

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.SoftwareUninstall,
            Payload = payload
        };
        var created = await dispatcher.DispatchAsync(command, ct);

        await RunAutomationTaskCommandHandler.CreateReportAsync(
            reportRepo, created, null, null, AutomationExecutionSourceType.SoftwareUninstall,
            new { inventoryId = cmd.InventoryId, packageId, source });

        return Result<VoidResult>.Success(VoidResult.Value);
    }

    private static string NormalizeUpdateSource(string? updateSource, string? source)
    {
        var value = (updateSource ?? source ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("choco"))
            return "chocolatey";
        return "winget";
    }
}
