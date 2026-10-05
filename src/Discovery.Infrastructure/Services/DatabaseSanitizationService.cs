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
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação da sanitização do banco. Cada verificação é isolada (uma falha
/// não aborta as demais) e idempotente (só toca registros inconsistentes).
/// Em dry-run nada é persistido nem auditado.
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

    public async Task<SanitizationReport> RunAsync(
        bool dryRun, IReadOnlyList<string>? checks, CancellationToken ct = default)
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
                results.Add(new SanitizationCheckResult(key, TitleOf(key), 0, 0, 0, ex.Message, []));
            }
        }

        // Ordem: limpar referências órfãs antes de re-resolver o que depende delas.
        await Run(DatabaseSanitizationChecks.OrphanWorkflowProfile, () => FixOrphanWorkflowProfilesAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.InvalidWorkflowState, () => FixInvalidWorkflowStatesAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.OrphanDepartment, () => FixOrphanDepartmentsAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.MissingWorkflowProfile, () => FixMissingWorkflowProfilesAsync(dryRun, ct));
        await Run(DatabaseSanitizationChecks.StateConsistency, () => InspectStateConsistencyAsync(ct));

        return new SanitizationReport(
            startedAt,
            DateTime.UtcNow,
            dryRun,
            results.Sum(r => r.Scanned),
            results.Sum(r => r.Fixed),
            results);
    }

    private static string TitleOf(string key) =>
        DatabaseSanitizationChecks.All.FirstOrDefault(c => c.Key == key)?.Title ?? key;

    // ── V4: perfil de workflow inexistente ──────────────────────────────────
    private async Task<SanitizationCheckResult> FixOrphanWorkflowProfilesAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.OrphanWorkflowProfile;
        var candidates = await db.Tickets
            .AsNoTracking()
            .Where(t => t.DeletedAt == null
                && t.WorkflowProfileId != null
                && !db.WorkflowProfiles.Any(p => p.Id == t.WorkflowProfileId))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.WorkflowProfileId })
            .Take(MaxPerCheck)
            .ToListAsync(ct);

        var samples = candidates
            .Take(MaxSamples)
            .Select(t => $"{t.Id} → perfil {t.WorkflowProfileId} inexistente")
            .ToList();

        if (dryRun || candidates.Count == 0)
            return new SanitizationCheckResult(key, TitleOf(key), candidates.Count, 0, 0, null, samples);

        var ids = candidates.Select(t => t.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var oldByTicket = candidates.ToDictionary(c => c.Id, c => c.WorkflowProfileId);

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

        return new SanitizationCheckResult(key, TitleOf(key), candidates.Count, tickets.Count, 0, null, samples);
    }

    // ── V1: chamado sem estado / estado inválido (causa dos "sem estado") ────
    private async Task<SanitizationCheckResult> FixInvalidWorkflowStatesAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.InvalidWorkflowState;
        var candidates = await db.Tickets
            .AsNoTracking()
            .Where(t => t.DeletedAt == null
                && (t.WorkflowStateId == Guid.Empty
                    || !db.WorkflowStates.Any(s => s.Id == t.WorkflowStateId
                        && (s.ClientId == null || s.ClientId == t.ClientId))))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.ClientId, t.WorkflowStateId })
            .Take(MaxPerCheck)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new SanitizationCheckResult(key, TitleOf(key), 0, 0, 0, null, []);

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
            return new SanitizationCheckResult(key, TitleOf(key), candidates.Count, 0, skipped, null, samples.Take(MaxSamples).ToList());

        var ids = repairable.Select(r => r.TicketId).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var byTicket = repairable.ToDictionary(r => r.TicketId);

        var now = DateTime.UtcNow;
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

        samples.AddRange(repairable.Take(MaxSamples).Select(r => $"{r.TicketId} → {FormatState(r.OldStateId)} ⇒ {r.NewStateName}"));
        var vanished = repairable.Count - tickets.Count; // removidos por outro processo
        return new SanitizationCheckResult(key, TitleOf(key), candidates.Count, tickets.Count, skipped + Math.Max(0, vanished), null, samples.Take(MaxSamples).ToList());
    }

    // ── V3: departamento inexistente ────────────────────────────────────────
    private async Task<SanitizationCheckResult> FixOrphanDepartmentsAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.OrphanDepartment;
        var candidates = await db.Tickets
            .AsNoTracking()
            .Where(t => t.DeletedAt == null
                && t.DepartmentId != null
                && !db.Departments.Any(d => d.Id == t.DepartmentId))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.DepartmentId })
            .Take(MaxPerCheck)
            .ToListAsync(ct);

        var samples = candidates
            .Take(MaxSamples)
            .Select(t => $"{t.Id} → departamento {t.DepartmentId} inexistente")
            .ToList();

        if (dryRun || candidates.Count == 0)
            return new SanitizationCheckResult(key, TitleOf(key), candidates.Count, 0, 0, null, samples);

        var ids = candidates.Select(t => t.Id).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var oldByTicket = candidates.ToDictionary(c => c.Id, c => c.DepartmentId);

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

        return new SanitizationCheckResult(key, TitleOf(key), candidates.Count, tickets.Count, 0, null, samples);
    }

    // ── V2: chamado aberto sem perfil de workflow (SLA do departamento) ─────
    private async Task<SanitizationCheckResult> FixMissingWorkflowProfilesAsync(bool dryRun, CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.MissingWorkflowProfile;
        var candidates = await db.Tickets
            .AsNoTracking()
            .Where(t => t.DeletedAt == null
                && t.ClosedAt == null
                && t.DepartmentId != null
                && t.WorkflowProfileId == null)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.DepartmentId })
            .Take(MaxPerCheck)
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return new SanitizationCheckResult(key, TitleOf(key), 0, 0, 0, null, []);

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
            return new SanitizationCheckResult(key, TitleOf(key), candidates.Count, 0, skipped, null, samples.Take(MaxSamples).ToList());
        }

        var ids = repairable.Select(r => r.TicketId).ToList();
        var tickets = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var byTicket = repairable.ToDictionary(r => r.TicketId);
        var now = DateTime.UtcNow;

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

        samples.AddRange(repairable.Take(MaxSamples).Select(r => $"{r.TicketId} → perfil {r.ProfileName} (SLA recalculado)"));
        var vanished = repairable.Count - tickets.Count; // removidos por outro processo
        return new SanitizationCheckResult(key, TitleOf(key), candidates.Count, tickets.Count, skipped + Math.Max(0, vanished), null, samples.Take(MaxSamples).ToList());
    }

    // ── V5: coerência estado × fechamento (somente relatório) ───────────────
    private async Task<SanitizationCheckResult> InspectStateConsistencyAsync(CancellationToken ct)
    {
        var key = DatabaseSanitizationChecks.StateConsistency;

        var closedWithOpenState = await (
            from t in db.Tickets.AsNoTracking()
            join s in db.WorkflowStates.AsNoTracking() on t.WorkflowStateId equals s.Id
            where t.DeletedAt == null && t.ClosedAt != null && !s.IsFinal
            select t.Id).Take(MaxPerCheck).ToListAsync(ct);

        var openWithFinalState = await (
            from t in db.Tickets.AsNoTracking()
            join s in db.WorkflowStates.AsNoTracking() on t.WorkflowStateId equals s.Id
            where t.DeletedAt == null && t.ClosedAt == null && s.IsFinal
            select t.Id).Take(MaxPerCheck).ToListAsync(ct);

        var samples = closedWithOpenState.Select(id => $"{id} → fechado em estado não-final")
            .Concat(openWithFinalState.Select(id => $"{id} → aberto em estado final"))
            .Take(MaxSamples)
            .ToList();

        return new SanitizationCheckResult(
            key, TitleOf(key),
            closedWithOpenState.Count + openWithFinalState.Count,
            0, 0, null, samples);
    }

    private static string FormatState(Guid stateId) =>
        stateId == Guid.Empty ? "sem estado" : stateId.ToString();
}
