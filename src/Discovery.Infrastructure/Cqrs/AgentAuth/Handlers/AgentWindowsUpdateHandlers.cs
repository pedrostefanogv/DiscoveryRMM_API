using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentAuth.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

/// <summary>
/// Upload do inventário de Windows Update. Persiste o snapshot + linhas,
/// recalcula aprovações e nunca apaga o inventário quando o scan falha sem
/// lista (proteção contra "scan vazio por erro").
/// </summary>
public sealed class ReportAgentWindowsUpdatesHandler(
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IPatchEventPublisher events,
    ILogger<ReportAgentWindowsUpdatesHandler>? logger = null
) : IRequestHandler<ReportAgentWindowsUpdatesCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(ReportAgentWindowsUpdatesCommand cmd, CancellationToken ct)
    {
        try
        {
            var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
            if (agent is null)
                return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

            var collectedAt = cmd.CollectedAt ?? DateTime.UtcNow;
            var entries = WindowsUpdateInventoryParser.ParseUpdates(cmd.Updates);
            var scan = WindowsUpdateInventoryParser.ParseScan(cmd.Scan);
            var pendingReboot = WindowsUpdateInventoryParser.ParsePendingReboot(cmd.PendingReboot);
            var serviceState = WindowsUpdateInventoryParser.ParseState(cmd.State);

            // "partial" = uma das fontes falhou (ex.: QFE): as listas não podem ser
            // tratadas como remoção. Qualquer status != ok com lista vazia preserva
            // o inventário bom.
            if (entries.Count == 0 && !string.Equals(scan.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                await patchRepo.UpdateScanStatusAsync(cmd.AgentId, scan, pendingReboot, serviceState, ct);
                logger?.LogInformation(
                    "Windows Update scan com falha ({Status}/{Error}) para {AgentId}; inventário preservado. Sonda: service={Service} online={Online} policy={Policy}.",
                    scan.Status, scan.ErrorCode, cmd.AgentId,
                    serviceState?.ServiceState, serviceState?.WuOnline, serviceState?.PolicyManaged);
                return Result<VoidResult>.Success(VoidResult.Value);
            }

            // Upload vazio com status "ok" é ambíguo (WUA pode falhar silencioso ou
            // o serviço estar desabilitado): atualiza metadados/sonda e mantém o
            // inventário anterior — ReplaceInventoryAsync aplica a mesma guarda.
            if (entries.Count == 0)
            {
                logger?.LogInformation(
                    "Windows Update upload sem itens (status={Status}) para {AgentId}; inventário preservado. Sonda: service={Service} online={Online}.",
                    scan.Status, cmd.AgentId, serviceState?.ServiceState, serviceState?.WuOnline);
            }

            await patchRepo.ReplaceInventoryAsync(
                cmd.AgentId, collectedAt.ToUniversalTime(), scan, pendingReboot, entries, serviceState, ct);
            await resolver.RecomputeForAgentAsync(cmd.AgentId, ct);

            var site = await siteRepo.GetByIdAsync(agent.SiteId);
            await events.PublishScanCompletedAsync(
                agent.Id,
                agent.Hostname,
                site?.ClientId,
                agent.SiteId,
                entries.Count(entry => entry.IsPending),
                entries.Count(entry => entry.IsInstalled),
                pendingReboot?.Pending ?? false,
                scan.Status,
                ct);

            return Result<VoidResult>.Success(VoidResult.Value);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Falha ao processar inventário de Windows Update para AgentId={AgentId}", cmd.AgentId);
            return Result<VoidResult>.Failure(Error.Internal($"Erro ao processar inventário de Windows Update: {ex.Message}"));
        }
    }
}

/// <summary>
/// Sonda leve do agente (reinício pendente + estado do serviço Windows Update).
/// Enviada com frequência (minutos), não quando o scan WUA roda.
/// </summary>
public sealed class ReportAgentPendingRebootHandler(
    IPatchRepository patchRepo,
    ILogger<ReportAgentPendingRebootHandler>? logger = null
) : IRequestHandler<ReportAgentPendingRebootCommand, Result<VoidResult>>
{
    private static readonly HashSet<string> AllowedReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows_update", "cbs", "file_rename", "computer_rename", "servicing"
    };

    public async Task<Result<VoidResult>> Handle(ReportAgentPendingRebootCommand cmd, CancellationToken ct)
    {
        try
        {
            var checkedAt = (cmd.CheckedAt ?? DateTime.UtcNow).ToUniversalTime();
            if (checkedAt > DateTime.UtcNow.AddMinutes(10))
                checkedAt = DateTime.UtcNow;

            var reasons = (cmd.Reasons ?? [])
                .Select(reason => reason?.Trim().ToLowerInvariant() ?? string.Empty)
                .Where(reason => reason.Length > 0 && AllowedReasons.Contains(reason))
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToList();

            // Motivos fora da allowlist não derrubam a sonda: o flag `pending`
            // continua valendo e o motivo desconhecido vira "servicing" (rótulo neutro
            // usado pelo agente). Antes, pending=true sem motivo reconhecido era
            // descartado em silêncio e o reinício pendente sumia da UI.
            if (cmd.Pending && reasons.Count == 0)
                reasons.Add("servicing");

            // pending=false é um estado VÁLIDO (reboot feito/limpo): nunca pode
            // ser convertido em true por um motivo residual.
            var pending = cmd.Pending && reasons.Count > 0;

            await patchRepo.UpdateAgentPendingRebootAsync(cmd.AgentId, pending, reasons, checkedAt, ct);

            // Sonda de estado (opcional): mantém a aba explicável entre scans caros.
            var serviceState = WindowsUpdateInventoryParser.ParseState(cmd.State);
            if (serviceState is not null)
                await patchRepo.UpdateServiceStateAsync(cmd.AgentId, serviceState, ct);

            return Result<VoidResult>.Success(VoidResult.Value);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Falha ao processar sonda de reinício do AgentId={AgentId}", cmd.AgentId);
            return Result<VoidResult>.Failure(Error.Internal($"Erro ao processar sonda de reinício: {ex.Message}"));
        }
    }
}

/// <summary>Inventário de updates do próprio agente (GET /me/windows-updates).</summary>
public sealed class GetAgentWindowsUpdatesHandler(IPatchRepository patchRepo)
    : IRequestHandler<GetAgentWindowsUpdatesQuery, Result<object>>
{
    public async Task<Result<object>> Handle(GetAgentWindowsUpdatesQuery query, CancellationToken ct)
    {
        var snapshot = await patchRepo.GetSnapshotAsync(query.AgentId, ct);
        var pending = await patchRepo.GetPendingForAgentAsync(query.AgentId, ct);

        return Result<object>.Success(new
        {
            agentId = query.AgentId,
            snapshot,
            pending = pending.Select(update => new
            {
                updateKey = update.UpdateKey,
                groupKey = update.GroupKey,
                title = update.Title,
                severity = update.Severity,
                classification = update.Classification,
                approvalState = update.ApprovalState.ToString(),
                status = update.Status.ToString(),
                requiresReboot = update.RequiresReboot
            }).ToList()
        });
    }
}
