using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação da sanitização do banco. Cada verificação é isolada (uma falha
/// não aborta as demais) e idempotente (só toca registros inconsistentes).
/// Em dry-run nada é persistido nem auditado.
///
/// <para><c>Examined</c> = registros avaliados; <c>Found</c> = inconsistências
/// encontradas; <c>HasMore</c> = varredura truncada pelo teto por verificação.
/// Correções e auditoria rodam na mesma transação.</para>
/// </summary>
public sealed class DatabaseSanitizationService(
    DiscoveryDbContext db,
    IWorkflowRepository workflowRepo,
    IWorkflowProfileRepository workflowProfileRepo,
    ISlaService slaService,
    IActivityLogService activityLog,
    ILogger<DatabaseSanitizationService> logger) : IDatabaseSanitizationService
{
    /// <summary>Teto de registros processados por verificação (evita varredura sem fim).</summary>
    private const int MaxPerCheck = 500;

    private const int MaxSamples = 20;

    /// <summary>Guarda de concorrência: impede duas sanitizações simultâneas.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public bool IsRunning => Gate.CurrentCount == 0;

    public async Task<SanitizationReport> RunAsync(
        bool dryRun, IReadOnlyList<string>? checks, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct))
            throw new DatabaseSanitizationBusyException();

        try
        {
            return await RunCoreAsync(dryRun, checks, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<SanitizationReport> RunCoreAsync(
        bool dryRun, IReadOnlyList<string>? checks, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var selected = checks is { Count: > 0 }
            ? new HashSet<string>(checks, StringComparer.OrdinalIgnoreCase)
            : null;
        var results = new List<SanitizationCheckResult>();

        async Task Run(string key, Func<Task<SanitizationCheckResult>> check)
        {
            if (selected is not null && !selected.Contains(key))
                return;
            try
            {
                results.Add(await check());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha na verificação de sanitização {CheckKey}.", key);
                results.Add(new SanitizationCheckResult(key, TitleOf(key), 0, 0, 0, 0, false, ex.Message, []));
            }
        }

        // Ordem: limpar referências órfãs/de outro cliente antes de re-resolver o
        // que depende delas (senão o reparo aplicaria perfil do departamento errado).
        await Run(DatabaseSanitizationChecks.OrphanWorkflowProfile, () => FixOrphanWorkflowProfilesAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.ProfileDepartmentMismatch, () => FixProfileDepartmentMismatchAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.InvalidWorkflowState, () => FixInvalidWorkflowStatesAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.OrphanDepartment, () => FixOrphanDepartmentsAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.CrossClientDepartment, () => FixCrossClientDepartmentsAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.MissingWorkflowProfile, () => FixMissingWorkflowProfilesAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.AssigneeOrphan, () => FixOrphanAssigneesAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.SlaInconsistent, () => FixSlaInconsistenciesAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.RatingInvalid, () => FixInvalidRatingsAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.WorkflowTransitionOrphan, () => FixOrphanTransitionsAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.StateConsistency, () => InspectStateConsistencyAsync(ct));
        await Run(DatabaseSanitizationChecks.OrphanClient, () => InspectOrphanClientsAsync(ct));
        await Run(DatabaseSanitizationChecks.MultipleInitialStates, () => InspectMultipleInitialStatesAsync(ct));
        await Run(DatabaseSanitizationChecks.DepartmentWithoutProfile, () => InspectDepartmentsWithoutProfileAsync(ct));
        await Run(DatabaseSanitizationChecks.AgentOrphanSite, () => InspectOrphanAgentSitesAsync(ct));
        await Run(DatabaseSanitizationChecks.TicketWatcherOrphan, () => InspectOrphanWatchersAsync(ct));
        await Run(DatabaseSanitizationChecks.AttachmentOrphan, () => InspectOrphanAttachmentsAsync(ct));

        return new SanitizationReport(
            startedAt,
            DateTime.UtcNow,
            dryRun,
            results.Sum(r => r.Examined),
            results.Sum(r => r.Found),
            results.Sum(r => r.Fixed),
            results.Any(r => r.HasMore),
            results);
    }

    private static string TitleOf(string key) =>
        DatabaseSanitizationChecks.All.FirstOrDefault(c => c.Key == key)?.Title ?? key;

    private static SanitizationCheckResult ReportOnly(
        string key, int examined, List<string> samples, int found) =>
        new(key, TitleOf(key), examined, found, 0, 0, found > MaxPerCheck, null, samples.Take(MaxSamples).ToList());

    // ── V4: perfil de workflow inexistente ──────────────────────────────────
    private async Task<SanitizationCheckResult> FixOrphanWorkflowProfilesAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.OrphanWorkflowProfile;
        var baseQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.WorkflowProfileId != null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => !db.WorkflowProfiles.Any(p => p.Id == t.WorkflowProfileId))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.WorkflowProfileId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples)
            .Select(t => $"{t.Id} → perfil {t.WorkflowProfileId} inexistente").ToList();

        if (dryRun || candidates.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);

        var ids = candidates.Select(t => t.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var oldByTicket = candidates.ToDictionary(c => c.Id, c => c.WorkflowProfileId);

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            ticket.WorkflowProfileId = null;
            ticket.SlaExpiresAt = null;
            ticket.SlaFirstResponseExpiresAt = null;
            ticket.FirstResponseSlaStartedAt = null;
            ticket.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets)
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.StateChanged, null,
                oldByTicket[ticket.Id]?.ToString(), null,
                "Perfil de workflow inexistente removido (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        return new(key, TitleOf(key), examined, candidates.Count, tickets.Count, 0, hasMore, null, samples);
    }

    // ── Perfil cujo departamento não é o do chamado ─────────────────────────
    private async Task<SanitizationCheckResult> FixProfileDepartmentMismatchAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.ProfileDepartmentMismatch;
        var baseQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.WorkflowProfileId != null && t.DepartmentId != null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => db.WorkflowProfiles.Any(p => p.Id == t.WorkflowProfileId && p.DepartmentId != t.DepartmentId))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.WorkflowProfileId, t.DepartmentId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples)
            .Select(t => $"{t.Id} → perfil {t.WorkflowProfileId} não pertence ao departamento {t.DepartmentId}").ToList();

        if (dryRun || candidates.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);

        var ids = candidates.Select(t => t.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var oldByTicket = candidates.ToDictionary(c => c.Id, c => c.WorkflowProfileId);

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            ticket.WorkflowProfileId = null;
            ticket.SlaExpiresAt = null;
            ticket.SlaFirstResponseExpiresAt = null;
            ticket.FirstResponseSlaStartedAt = null;
            ticket.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets)
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.StateChanged, null,
                oldByTicket[ticket.Id]?.ToString(), null,
                "Perfil de workflow de outro departamento removido (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        return new(key, TitleOf(key), examined, candidates.Count, tickets.Count, 0, hasMore, null, samples);
    }

    // ── V1: chamado sem estado / estado inválido (causa dos "sem estado") ────
    private async Task<SanitizationCheckResult> FixInvalidWorkflowStatesAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.InvalidWorkflowState;
        var baseQuery = db.Tickets.AsNoTracking().Where(t => t.DeletedAt == null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => t.WorkflowStateId == Guid.Empty
                || !db.WorkflowStates.Any(s => s.Id == t.WorkflowStateId
                    && (s.ClientId == null || s.ClientId == t.ClientId)))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.ClientId, t.WorkflowStateId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        if (candidates.Count == 0)
            return new(key, TitleOf(key), examined, 0, 0, 0, false, null, []);

        var initialByClient = new Dictionary<Guid, WorkflowState?>();
        async Task<WorkflowState?> ResolveInitialAsync(Guid clientId)
        {
            if (!initialByClient.TryGetValue(clientId, out var state))
            {
                state = await workflowRepo.GetInitialStateAsync(clientId);
                initialByClient[clientId] = state;
            }
            return state;
        }

        var samples = new List<string>();
        var skipped = 0;
        var repairable = new List<(Guid TicketId, Guid OldStateId, Guid NewStateId, string NewStateName, WorkflowState Initial)>();

        foreach (var candidate in candidates)
        {
            var initial = await ResolveInitialAsync(candidate.ClientId);
            if (initial is null)
            {
                skipped++;
                samples.Add($"{candidate.Id} → cliente {candidate.ClientId} sem estado inicial");
                continue;
            }

            if (dryRun)
            {
                samples.Add($"{candidate.Id} → {FormatState(candidate.WorkflowStateId)} ⇒ {initial.Name}");
                continue;
            }

            repairable.Add((candidate.Id, candidate.WorkflowStateId, initial.Id, initial.Name, initial));
        }

        if (dryRun || repairable.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, skipped, hasMore, null, samples.Take(MaxSamples).ToList());

        var ids = repairable.Select(r => r.TicketId).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var byTicket = repairable.ToDictionary(r => r.TicketId);
        var now = DateTime.UtcNow;

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            var repair = byTicket[ticket.Id];
            ticket.WorkflowStateId = repair.NewStateId;
            // O estado inicial pode pausar o SLA: ao reparar, inicia/mantém o hold.
            SlaHold.ApplyStateChange(ticket, oldState: null, repair.Initial, now);
            ticket.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets)
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.StateChanged, null,
                byTicket[ticket.Id].OldStateId.ToString(),
                byTicket[ticket.Id].NewStateId.ToString(),
                "Estado inicial reaplicado (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        samples.AddRange(repairable.Take(MaxSamples).Select(r => $"{r.TicketId} → {FormatState(r.OldStateId)} ⇒ {r.NewStateName}"));
        var vanished = repairable.Count - tickets.Count; // removidos por outro processo
        return new(key, TitleOf(key), examined, candidates.Count, tickets.Count, skipped + Math.Max(0, vanished), hasMore, null, samples.Take(MaxSamples).ToList());
    }

    // ── V3: departamento inexistente ────────────────────────────────────────
    private async Task<SanitizationCheckResult> FixOrphanDepartmentsAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.OrphanDepartment;
        var baseQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.DepartmentId != null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => !db.Departments.Any(d => d.Id == t.DepartmentId))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.DepartmentId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples)
            .Select(t => $"{t.Id} → departamento {t.DepartmentId} inexistente").ToList();

        if (dryRun || candidates.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);

        var ids = candidates.Select(t => t.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var oldByTicket = candidates.ToDictionary(c => c.Id, c => c.DepartmentId);

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            ticket.DepartmentId = null;
            ticket.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets)
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.DepartmentChanged, null,
                oldByTicket[ticket.Id]?.ToString() ?? "none", "none",
                "Departamento inexistente removido (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        return new(key, TitleOf(key), examined, candidates.Count, tickets.Count, 0, hasMore, null, samples);
    }

    // ── Departamento existe, mas é de outro cliente ─────────────────────────
    private async Task<SanitizationCheckResult> FixCrossClientDepartmentsAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.CrossClientDepartment;
        var baseQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.DepartmentId != null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => db.Departments.Any(d => d.Id == t.DepartmentId && d.ClientId != null && d.ClientId != t.ClientId))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.DepartmentId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples)
            .Select(t => $"{t.Id} → departamento {t.DepartmentId} pertence a outro cliente").ToList();

        if (dryRun || candidates.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);

        var ids = candidates.Select(t => t.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var oldByTicket = candidates.ToDictionary(c => c.Id, c => c.DepartmentId);

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            ticket.DepartmentId = null;
            ticket.WorkflowProfileId = null;
            ticket.SlaExpiresAt = null;
            ticket.SlaFirstResponseExpiresAt = null;
            ticket.FirstResponseSlaStartedAt = null;
            ticket.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets)
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.DepartmentChanged, null,
                oldByTicket[ticket.Id]?.ToString() ?? "none", "none",
                "Departamento de outro cliente removido (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        return new(key, TitleOf(key), examined, candidates.Count, tickets.Count, 0, hasMore, null, samples);
    }

    // ── V2: chamado aberto sem perfil de workflow (SLA do departamento) ─────
    private async Task<SanitizationCheckResult> FixMissingWorkflowProfilesAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.MissingWorkflowProfile;
        var baseQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.ClosedAt == null
                && t.DepartmentId != null && t.WorkflowProfileId == null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.DepartmentId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        if (candidates.Count == 0)
            return new(key, TitleOf(key), examined, 0, 0, 0, false, null, []);

        var profileByDepartment = new Dictionary<Guid, WorkflowProfile?>();
        var repairable = new List<(Guid TicketId, Guid ProfileId, string ProfileName)>();
        var skipped = 0;
        var samples = new List<string>();

        foreach (var candidate in candidates)
        {
            var departmentId = candidate.DepartmentId!.Value;
            if (!profileByDepartment.TryGetValue(departmentId, out var profile))
            {
                profile = await workflowProfileRepo.GetDefaultByDepartmentAsync(departmentId);
                profileByDepartment[departmentId] = profile;
            }

            if (profile is null || !profile.IsActive)
            {
                skipped++;
                samples.Add($"{candidate.Id} → departamento {departmentId} sem perfil default ativo");
                continue;
            }

            repairable.Add((candidate.Id, profile.Id, profile.Name));
        }

        if (dryRun || repairable.Count == 0)
        {
            samples.AddRange(repairable.Take(MaxSamples).Select(r => $"{r.TicketId} → perfil {r.ProfileName} (SLA recalculado)"));
            return new(key, TitleOf(key), examined, candidates.Count, 0, skipped, hasMore, null, samples.Take(MaxSamples).ToList());
        }

        var ids = repairable.Select(r => r.TicketId).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var byTicket = repairable.ToDictionary(r => r.TicketId);
        var now = DateTime.UtcNow;

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            var profileId = byTicket[ticket.Id].ProfileId;
            ticket.WorkflowProfileId = profileId;
            try
            {
                ticket.SlaExpiresAt = await slaService.CalculateSlaExpiryAsync(profileId, now);
                ticket.SlaFirstResponseExpiresAt = await slaService.CalculateFirstResponseExpiryAsync(profileId, now);
                if (!ticket.FirstRespondedAt.HasValue)
                    ticket.FirstResponseSlaStartedAt = now;
            }
            catch (InvalidOperationException)
            {
                // Perfil inválido não pode impedir a correção (tolerância igual à do create).
                ticket.SlaExpiresAt = null;
                ticket.SlaFirstResponseExpiresAt = null;
            }
            ticket.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets)
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.StateChanged, null, null,
                byTicket[ticket.Id].ProfileId.ToString(),
                "SLA do departamento aplicado retroativamente (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        samples.AddRange(repairable.Take(MaxSamples).Select(r => $"{r.TicketId} → perfil {r.ProfileName} (SLA recalculado)"));
        var vanished = repairable.Count - tickets.Count; // removidos por outro processo
        return new(key, TitleOf(key), examined, candidates.Count, tickets.Count, skipped + Math.Max(0, vanished), hasMore, null, samples.Take(MaxSamples).ToList());
    }

    // ── Responsável/solicitante inexistente ─────────────────────────────────
    private async Task<SanitizationCheckResult> FixOrphanAssigneesAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.AssigneeOrphan;
        var baseQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && (t.AssignedToUserId != null || t.RequesterUserId != null));
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => (t.AssignedToUserId != null && !db.Users.Any(u => u.Id == t.AssignedToUserId))
                || (t.RequesterUserId != null && !db.Users.Any(u => u.Id == t.RequesterUserId)))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.AssignedToUserId, t.RequesterUserId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples)
            .Select(t => $"{t.Id} → responsável {t.AssignedToUserId?.ToString() ?? "-"} / solicitante {t.RequesterUserId?.ToString() ?? "-"}").ToList();

        if (dryRun || candidates.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);

        var referenced = candidates
            .SelectMany(c => new[] { c.AssignedToUserId, c.RequesterUserId })
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var existing = await db.Users.AsNoTracking()
            .Where(u => referenced.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        var missing = referenced.Except(existing).ToHashSet();

        var ids = candidates.Select(c => c.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var changedIds = new HashSet<Guid>();
        var cleared = new List<string>();

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            if (ticket.AssignedToUserId is { } assigned && missing.Contains(assigned))
            {
                cleared.Add($"{ticket.Id} → responsável {assigned}");
                ticket.AssignedToUserId = null;
                changedIds.Add(ticket.Id);
            }
            if (ticket.RequesterUserId is { } requester && missing.Contains(requester))
            {
                cleared.Add($"{ticket.Id} → solicitante {requester}");
                ticket.RequesterUserId = null;
                changedIds.Add(ticket.Id);
            }
            if (changedIds.Contains(ticket.Id))
                ticket.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets.Where(t => changedIds.Contains(t.Id)))
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.Assigned, null, null, null,
                "Responsável/solicitante inexistente removido (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        return new(key, TitleOf(key), examined, candidates.Count, changedIds.Count, 0, hasMore, null,
            samples.Concat(cleared).Take(MaxSamples).ToList());
    }

    // ── Campos de SLA incoerentes ───────────────────────────────────────────
    private async Task<SanitizationCheckResult> FixSlaInconsistenciesAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.SlaInconsistent;
        var baseQuery = db.Tickets.AsNoTracking().Where(t => t.DeletedAt == null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => t.SlaPausedSeconds < 0
                || (t.SlaHoldStartedAt != null && t.ClosedAt != null)
                || (t.SlaFirstResponseExpiresAt != null && t.FirstResponseSlaStartedAt == null && t.ClosedAt == null)
                || t.UpdatedAt < t.CreatedAt
                || (t.ClosedAt != null && t.ClosedAt < t.CreatedAt))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples).Select(t => $"{t.Id} → SLA incoerente (pausa {t.SlaPausedSeconds}s, hold {(t.SlaHoldStartedAt != null ? "aberto" : "-")})").ToList();

        if (dryRun || candidates.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);

        var ids = candidates.Select(t => t.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var repaired = 0;

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            var changed = false;

            if (ticket.SlaPausedSeconds < 0)
            {
                ticket.SlaPausedSeconds = 0;
                changed = true;
            }

            if (ticket.SlaHoldStartedAt.HasValue && ticket.ClosedAt.HasValue)
            {
                // Chamado fechado não pode manter hold aberto: acumula e encerra.
                ticket.SlaPausedSeconds += SlaHold.ElapsedSeconds(ticket.SlaHoldStartedAt, now);
                ticket.SlaHoldStartedAt = null;
                changed = true;
            }

            if (ticket.SlaFirstResponseExpiresAt.HasValue
                && ticket.FirstResponseSlaStartedAt is null
                && ticket.ClosedAt is null
                && !ticket.FirstRespondedAt.HasValue)
            {
                ticket.FirstResponseSlaStartedAt = ticket.CreatedAt;
                changed = true;
            }

            if (ticket.UpdatedAt < ticket.CreatedAt)
            {
                ticket.UpdatedAt = ticket.CreatedAt;
                changed = true;
            }

            if (ticket.ClosedAt.HasValue && ticket.ClosedAt.Value < ticket.CreatedAt)
            {
                ticket.ClosedAt = ticket.CreatedAt;
                changed = true;
            }

            if (changed) repaired++;
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets)
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.StateChanged, null, null, null,
                "Campos de SLA normalizados (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        return new(key, TitleOf(key), examined, candidates.Count, repaired, 0, hasMore, null, samples);
    }

    // ── Avaliações inválidas ────────────────────────────────────────────────
    private async Task<SanitizationCheckResult> FixInvalidRatingsAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.RatingInvalid;
        var baseQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && (t.Rating != null || t.RatedAt != null || t.RatedBy != null));
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => (t.Rating != null && (t.Rating < 0 || t.Rating > 5))
                || (t.Rating == null && (t.RatedAt != null || t.RatedBy != null)))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples)
            .Select(t => $"{t.Id} → rating {t.Rating?.ToString() ?? "null"} (ratedAt {t.RatedAt?.ToString() ?? "-"})").ToList();

        if (dryRun || candidates.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);

        var ids = candidates.Select(t => t.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var repaired = 0;

        await using var tx = await BeginTransactionAsync(ct);
        foreach (var ticket in tickets)
        {
            if (ticket.Rating is < 0 or > 5)
            {
                ticket.Rating = null;
                ticket.RatedAt = null;
                ticket.RatedBy = null;
                ticket.UpdatedAt = DateTime.UtcNow;
                repaired++;
            }
            else if (ticket.Rating is null && (ticket.RatedAt != null || ticket.RatedBy != null))
            {
                ticket.RatedAt = null;
                ticket.RatedBy = null;
                ticket.UpdatedAt = DateTime.UtcNow;
                repaired++;
            }
        }
        await db.SaveChangesAsync(ct);

        foreach (var ticket in tickets)
            await activityLog.LogActivityAsync(ticket.Id, TicketActivityType.Rated, null, null, null,
                "Avaliação inválida normalizada (sanitização)");
        if (tx is not null) await tx.CommitAsync(ct);

        return new(key, TitleOf(key), examined, candidates.Count, repaired, 0, hasMore, null, samples);
    }

    // ── Transições de workflow órfãs ────────────────────────────────────────
    private async Task<SanitizationCheckResult> FixOrphanTransitionsAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.WorkflowTransitionOrphan;
        var examined = await db.WorkflowTransitions.AsNoTracking().CountAsync(ct);

        var candidates = await db.WorkflowTransitions.AsNoTracking()
            .Where(tr => !db.WorkflowStates.Any(s => s.Id == tr.FromStateId)
                || !db.WorkflowStates.Any(s => s.Id == tr.ToStateId))
            .OrderBy(tr => tr.CreatedAt).ThenBy(tr => tr.Id)
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples)
            .Select(tr => $"{tr.Id} → {tr.FromStateId} ⇒ {tr.ToStateId}").ToList();

        if (dryRun || candidates.Count == 0)
            return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);

        var ids = candidates.Select(tr => tr.Id).ToList();
        var transitions = await db.WorkflowTransitions.Where(tr => ids.Contains(tr.Id)).ToListAsync(ct);

        await using var tx = await BeginTransactionAsync(ct);
        db.WorkflowTransitions.RemoveRange(transitions);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        return new(key, TitleOf(key), examined, candidates.Count, transitions.Count, 0, hasMore, null, samples);
    }

    // ── V5: coerência estado × fechamento (somente relatório) ───────────────
    private async Task<SanitizationCheckResult> InspectStateConsistencyAsync(CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.StateConsistency;
        var examined = await db.Tickets.AsNoTracking().CountAsync(t => t.DeletedAt == null, ct);

        var closedWithOpenState = await (
            from t in db.Tickets.AsNoTracking()
            join s in db.WorkflowStates.AsNoTracking() on t.WorkflowStateId equals s.Id
            where t.DeletedAt == null && t.ClosedAt != null && !s.IsFinal
            orderby t.Id
            select t.Id).Take(MaxPerCheck + 1).ToListAsync(ct);

        var openWithFinalState = await (
            from t in db.Tickets.AsNoTracking()
            join s in db.WorkflowStates.AsNoTracking() on t.WorkflowStateId equals s.Id
            where t.DeletedAt == null && t.ClosedAt == null && s.IsFinal
            orderby t.Id
            select t.Id).Take(MaxPerCheck + 1).ToListAsync(ct);

        var found = closedWithOpenState.Count + openWithFinalState.Count;
        var samples = closedWithOpenState.Take(MaxSamples).Select(id => $"{id} → fechado em estado não-final")
            .Concat(openWithFinalState.Take(MaxSamples).Select(id => $"{id} → aberto em estado final"))
            .Take(MaxSamples)
            .ToList();

        return ReportOnly(key, examined, samples, found);
    }

    // ── Chamado cujo cliente não existe (somente relatório) ─────────────────
    private async Task<SanitizationCheckResult> InspectOrphanClientsAsync(CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.OrphanClient;
        var baseQuery = db.Tickets.AsNoTracking().Where(t => t.DeletedAt == null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(t => !db.Clients.Any(c => c.Id == t.ClientId))
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.ClientId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples).Select(t => $"{t.Id} → cliente {t.ClientId} inexistente").ToList();
        return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);
    }

    // ── Mais de um estado inicial por cliente (somente relatório) ───────────
    private async Task<SanitizationCheckResult> InspectMultipleInitialStatesAsync(CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.MultipleInitialStates;
        var examined = await db.WorkflowStates.AsNoTracking().CountAsync(ct);

        var groups = await db.WorkflowStates.AsNoTracking()
            .Where(s => s.IsInitial)
            .GroupBy(s => s.ClientId)
            .Where(g => g.Count() > 1)
            .Select(g => new { ClientId = g.Key, Count = g.Count() })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = groups.Count > MaxPerCheck;
        if (hasMore) groups.RemoveRange(MaxPerCheck, groups.Count - MaxPerCheck);

        var samples = groups.Take(MaxSamples)
            .Select(g => $"cliente {g.ClientId?.ToString() ?? "global"} → {g.Count} estados iniciais").ToList();
        return new(key, TitleOf(key), examined, groups.Count, 0, 0, hasMore, null, samples);
    }

    // ── Departamento ativo sem perfil ativo (somente relatório) ─────────────
    private async Task<SanitizationCheckResult> InspectDepartmentsWithoutProfileAsync(CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.DepartmentWithoutProfile;
        var baseQuery = db.Departments.AsNoTracking().Where(d => d.IsActive);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(d => !db.WorkflowProfiles.Any(p => p.DepartmentId == d.Id && p.IsActive))
            .OrderBy(d => d.Name).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Name })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples).Select(d => $"{d.Id} → {d.Name} sem perfil ativo").ToList();
        return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);
    }

    // ── Agente vinculado a site inexistente (somente relatório) ─────────────
    private async Task<SanitizationCheckResult> InspectOrphanAgentSitesAsync(CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.AgentOrphanSite;
        var baseQuery = db.Agents.AsNoTracking().Where(a => a.DeletedAt == null);
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(a => !db.Sites.Any(s => s.Id == a.SiteId))
            .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.SiteId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples).Select(a => $"{a.Id} → site {a.SiteId} inexistente").ToList();
        return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);
    }

    // ── Seguidores de chamado órfãos (somente relatório) ────────────────────
    private async Task<SanitizationCheckResult> InspectOrphanWatchersAsync(CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.TicketWatcherOrphan;
        var examined = await db.TicketWatchers.AsNoTracking().CountAsync(ct);

        var candidates = await db.TicketWatchers.AsNoTracking()
            .Where(w => !db.Tickets.Any(t => t.Id == w.TicketId) || !db.Users.Any(u => u.Id == w.UserId))
            .OrderBy(w => w.AddedAt).ThenBy(w => w.Id)
            .Select(w => new { w.Id, w.TicketId, w.UserId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples).Select(w => $"{w.Id} → chamado {w.TicketId} / usuário {w.UserId}").ToList();
        return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);
    }

    // ── Anexos com pai inexistente (somente relatório) ──────────────────────
    private async Task<SanitizationCheckResult> InspectOrphanAttachmentsAsync(CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.AttachmentOrphan;
        var baseQuery = db.Attachments.AsNoTracking()
            .Where(a => a.DeletedAt == null && (a.EntityType == "Ticket" || a.EntityType == "TicketComment"));
        var examined = await baseQuery.CountAsync(ct);

        var candidates = await baseQuery
            .Where(a => a.EntityType == "Ticket"
                ? !db.Tickets.Any(t => t.Id == a.EntityId)
                : !db.TicketComments.Any(c => c.Id == a.EntityId))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.EntityType, a.EntityId })
            .Take(MaxPerCheck + 1)
            .ToListAsync(ct);
        var hasMore = candidates.Count > MaxPerCheck;
        if (hasMore) candidates.RemoveRange(MaxPerCheck, candidates.Count - MaxPerCheck);

        var samples = candidates.Take(MaxSamples).Select(a => $"{a.Id} → {a.EntityType} {a.EntityId} inexistente").ToList();
        return new(key, TitleOf(key), examined, candidates.Count, 0, 0, hasMore, null, samples);
    }

    /// <summary>
    /// Inicia transação apenas em provedores relacionais. O provider in-memory
    /// (usado nos testes) trata BeginTransaction como erro
    /// (TransactionIgnoredWarning); em PostgreSQL o comportamento é sempre transacional.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct) =>
        db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

    private static string FormatState(Guid stateId) =>
        stateId == Guid.Empty ? "sem estado" : stateId.ToString();
}
