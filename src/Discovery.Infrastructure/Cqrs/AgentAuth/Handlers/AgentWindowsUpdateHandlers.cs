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

            // "partial" = uma das fontes falhou (ex.: QFE): as listas não podem ser
            // tratadas como remoção. Qualquer status != ok com lista vazia preserva
            // o inventário bom.
            if (entries.Count == 0 && !string.Equals(scan.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                await patchRepo.UpdateScanStatusAsync(cmd.AgentId, scan, pendingReboot, ct);
                logger?.LogInformation(
                    "Windows Update scan com falha ({Status}/{Error}) para {AgentId}; inventário preservado.",
                    scan.Status, scan.ErrorCode, cmd.AgentId);
                return Result<VoidResult>.Success(VoidResult.Value);
            }

            await patchRepo.ReplaceInventoryAsync(cmd.AgentId, collectedAt.ToUniversalTime(), scan, pendingReboot, entries, ct);
            await resolver.RecomputeForAgentAsync(cmd.AgentId, ct);

            return Result<VoidResult>.Success(VoidResult.Value);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Falha ao processar inventário de Windows Update para AgentId={AgentId}", cmd.AgentId);
            return Result<VoidResult>.Failure(Error.Internal($"Erro ao processar inventário de Windows Update: {ex.Message}"));
        }
    }
}

/// <summary>Sonda de reinício pendente (endpoint leve, enviado só em mudança).</summary>
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

            var pending = cmd.Pending && reasons.Count > 0;
            await patchRepo.UpdateAgentPendingRebootAsync(cmd.AgentId, pending, reasons, checkedAt, ct);
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
