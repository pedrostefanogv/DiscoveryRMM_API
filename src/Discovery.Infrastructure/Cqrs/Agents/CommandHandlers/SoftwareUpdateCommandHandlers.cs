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

/// <summary>
/// Instala no agent um pacote do catálogo da loja que ainda não está na máquina.
/// Como não existe item de inventário, o pacote é identificado por
/// packageId + tipo de instalação vindos do catálogo. A política de aprovação da
/// loja é aplicada aqui (gate do servidor) e reforçada por
/// <paramref name="InstallAgentSoftwareCommand.ConfirmUnapproved"/>.
/// </summary>
public sealed class InstallAgentSoftwareCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IAppStoreService appStoreService,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<InstallAgentSoftwareCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(InstallAgentSoftwareCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var packageId = (cmd.PackageId ?? string.Empty).Trim();
        if (packageId.Length == 0)
            return Result<VoidResult>.Failure(Error.Validation("packageId",
                "O identificador do pacote é obrigatório."));

        var source = SourceFromInstallationType(cmd.InstallationType);

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var approved = await appStoreService.IsPackageApprovedAsync(
            site?.ClientId, agent.SiteId, cmd.AgentId, cmd.InstallationType, packageId, ct);

        if (!approved && !cmd.ConfirmUnapproved)
        {
            return Result<VoidResult>.Failure(Error.Conflict(
                "Este aplicativo não está aprovado na loja para este agente. Confirme para instalar mesmo assim."));
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
            CommandType = CommandType.SoftwareInstall,
            Payload = payload
        };
        var created = await dispatcher.DispatchAsync(command, ct);

        // Auditoria: registra a execução no histórico de operações de automação,
        // atualizado com o resultado quando o agent responde.
        await RunAutomationTaskCommandHandler.CreateReportAsync(
            reportRepo, created, null, null, AutomationExecutionSourceType.SoftwareInstall,
            new { packageId, source, approved, confirmUnapproved = cmd.ConfirmUnapproved });

        return Result<VoidResult>.Success(VoidResult.Value);
    }

    /// <summary>
    /// Converte o tipo do catálogo no identificador aceito pelo agent. Diferente
    /// de <c>NormalizeUpdateSource</c>, preserva <c>custom</c>: o agent usa o
    /// instalador publicado, não o gerenciador de pacotes.
    /// </summary>
    private static string SourceFromInstallationType(AppInstallationType installationType) =>
        installationType switch
        {
            AppInstallationType.Chocolatey => "chocolatey",
            AppInstallationType.Custom => "custom",
            _ => "winget"
        };
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
