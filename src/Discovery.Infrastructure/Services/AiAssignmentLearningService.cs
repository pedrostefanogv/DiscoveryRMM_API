using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Ciclo de aprendizado da triagem por IA. Cada ciclo:
/// 1) extrai competências dos chamados resolvidos de cada membro (janela de 90 dias);
/// 2) recalibra os pesos a partir da taxa de override das decisões aplicadas;
/// 3) aplica automaticamente ou deixa pendente para o gestor, conforme o modo.
///
/// Idempotente por ciclo: a sugestão pendente anterior do mesmo alvo é substituída
/// pela nova, evitando acúmulo de itens sem valor.
/// </summary>
public class AiAssignmentLearningService(
    DiscoveryDbContext db,
    IConfigurationAuditService configurationAudit,
    ILogger<AiAssignmentLearningService> logger) : IAiAssignmentLearningService
{
    private const int SkillWindowDays = 90;
    private const int MaxResolvedTicketsPerMember = 500;
    private const int MinimumWeightSamples = 20;
    private const string StatusPending = "pending";
    private const string StatusApplied = "applied";
    private const string StatusDiscarded = "discarded";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<int> RunCycleAsync(
        Guid? departmentId = null, Guid? actorUserId = null, CancellationToken ct = default)
    {
        // Somente departamentos que usam a triagem por IA: competências e pesos
        // alimentam exclusivamente o score da triagem, então rodar o ciclo nos
        // demais seria trabalho diário sem uso (potencialmente em muitos membros).
        var departments = await db.Departments
            .Where(d => (departmentId == null || d.Id == departmentId.Value)
                        && d.AssignmentStrategy == (int)TicketAssignmentStrategy.AiTriage
                        && (d.AiSkillLearningMode != (int)AiLearningMode.Off
                            || d.AiWeightLearningMode != (int)AiLearningMode.Off))
            .ToListAsync(ct);

        var created = 0;
        foreach (var department in departments)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (department.AiSkillLearningMode != (int)AiLearningMode.Off)
                    created += await RunSkillCycleAsync(department, ct);

                if (department.AiWeightLearningMode != (int)AiLearningMode.Off)
                    created += await RunWeightCycleAsync(department, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ciclo de aprendizado falhou para o departamento {DepartmentId}.", department.Id);
            }
        }

        return created;
    }

    // ── Competências ─────────────────────────────────────────────────────

    private async Task<int> RunSkillCycleAsync(Department department, CancellationToken ct)
    {
        var members = await db.DepartmentMembers
            .Where(m => m.DepartmentId == department.Id && m.IsActive)
            .Join(db.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, u) => m)
            .ToListAsync(ct);

        if (members.Count == 0) return 0;

        var since = DateTime.UtcNow.AddDays(-SkillWindowDays);
        var created = 0;

        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();

            var rows = await db.Tickets.AsNoTracking()
                .Where(t => t.DeletedAt == null && t.AssignedToUserId == member.UserId
                            && t.ClosedAt != null && t.CreatedAt >= since)
                .OrderByDescending(t => t.ClosedAt)
                .Take(MaxResolvedTicketsPerMember)
                .Select(t => new { t.Title, t.Description, t.Category })
                .ToListAsync(ct);

            if (rows.Count == 0) continue;

            var evidence = rows
                .Select(r => new SkillEvidenceTicket(r.Title, r.Description, r.Category))
                .ToList();

            var existingTags = ParseTags(member.SkillTagsJson);
            var (suggested, evidenceJson) = SkillExtractor.Extract(
                evidence, existingTags, department.AiSkillMinEvidence,
                department.AiSkillMaxTags, SkillWindowDays);

            if (suggested.Count == 0) continue;

            // Substitui pendências anteriores do mesmo membro.
            await SupersedeSkillSuggestionsAsync(department.Id, member.UserId, ct);

            var auto = department.AiSkillLearningMode == (int)AiLearningMode.Auto;
            var suggestion = new TechnicianSkillSuggestion
            {
                Id = Guid.NewGuid(),
                DepartmentId = department.Id,
                UserId = member.UserId,
                WindowDays = SkillWindowDays,
                SuggestedTagsJson = JsonSerializer.Serialize(suggested),
                EvidenceJson = evidenceJson,
                Status = auto ? StatusApplied : StatusPending,
                AutoApplied = auto,
                CreatedAt = DateTime.UtcNow
            };

            if (auto)
            {
                var previousTags = member.SkillTagsJson;
                member.SkillTagsJson = MergeTags(member.SkillTagsJson, suggested);
                suggestion.AppliedTagsJson = suggestion.SuggestedTagsJson;
                suggestion.DecidedAt = DateTime.UtcNow;

                // Rastreabilidade: mudança de configuração aplicada pela IA sem humano.
                await configurationAudit.LogChangeAsync(
                    "DepartmentMember", member.Id, "SkillTagsJson",
                    previousTags, member.SkillTagsJson,
                    reason: "ai_learning_auto", changedBy: "ai-assignment-learning");
            }

            db.TechnicianSkillSuggestions.Add(suggestion);
            created++;
        }

        if (created > 0) await db.SaveChangesAsync(ct);
        return created;
    }

    // ── Pesos ────────────────────────────────────────────────────────────

    private async Task<int> RunWeightCycleAsync(Department department, CancellationToken ct)
    {
        if (department.AssignmentStrategy != (int)TicketAssignmentStrategy.AiTriage) return 0;

        var cycleDays = Math.Clamp(department.AiWeightCycleDays, 1, 90);
        var windowEnd = DateTime.UtcNow;
        var windowStart = windowEnd.AddDays(-cycleDays);

        var decisions = await db.TicketAssignmentDecisions.AsNoTracking()
            .Where(d => d.DepartmentId == department.Id && d.Applied
                        && d.ChosenUserId != null && d.CreatedAt >= windowStart)
            .Select(d => new { d.Id, d.ChosenUserId, d.OverriddenAt, d.CandidatesJson })
            .ToListAsync(ct);

        var samples = new List<WeightCalibrationSample>();
        foreach (var decision in decisions)
        {
            var candidates = DeserializeCandidates(decision.CandidatesJson);
            if (candidates.Count == 0 || decision.ChosenUserId is null) continue;

            var chosen = candidates.FirstOrDefault(c => c.UserId == decision.ChosenUserId.Value);
            if (chosen is null) continue;

            var chosenScores = new Dictionary<string, double>
            {
                ["skill"] = chosen.SkillScore,
                ["affinity"] = chosen.AffinityScore,
                ["performance"] = chosen.PerformanceScore,
                ["load"] = chosen.LoadScore,
                ["csat"] = chosen.CsatScore,
                ["slaQuality"] = chosen.SlaScore
            };

            var medians = new Dictionary<string, double>
            {
                ["skill"] = Median(candidates.Select(c => c.SkillScore).ToList()),
                ["affinity"] = Median(candidates.Select(c => c.AffinityScore).ToList()),
                ["performance"] = Median(candidates.Select(c => c.PerformanceScore).ToList()),
                ["load"] = Median(candidates.Select(c => c.LoadScore).ToList()),
                ["csat"] = Median(candidates.Select(c => c.CsatScore).ToList()),
                ["slaQuality"] = Median(candidates.Select(c => c.SlaScore).ToList())
            };

            samples.Add(new WeightCalibrationSample(
                decision.Id, decision.OverriddenAt != null, chosenScores, medians));
        }

        var current = ParseWeights(department.AiAssignmentWeightsJson);
        var result = WeightCalibrator.Calibrate(
            current, samples, department.AiWeightMaxDeltaPerCycle,
            department.AiWeightMin, department.AiWeightMax, MinimumWeightSamples);

        if (!result.HasProposal)
        {
            logger.LogDebug(
                "Sem proposta de pesos para o departamento {DepartmentId}: {Reason}",
                department.Id, result.Reason);
            return 0;
        }

        await SupersedeWeightSuggestionsAsync(department.Id, ct);

        var autoApply = department.AiWeightLearningMode == (int)AiLearningMode.Auto;
        var suggestion = new AiWeightSuggestion
        {
            Id = Guid.NewGuid(),
            DepartmentId = department.Id,
            CycleDays = cycleDays,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            CurrentWeightsJson = JsonSerializer.Serialize(result.Current),
            SuggestedWeightsJson = JsonSerializer.Serialize(result.Suggested),
            EvidenceJson = JsonSerializer.Serialize(result.Evidence),
            Status = autoApply ? StatusApplied : StatusPending,
            AutoApplied = autoApply,
            CreatedAt = DateTime.UtcNow
        };

        if (autoApply)
        {
            var previousWeights = department.AiAssignmentWeightsJson;
            department.AiAssignmentWeightsJson = suggestion.SuggestedWeightsJson;
            suggestion.DecidedAt = DateTime.UtcNow;

            await configurationAudit.LogChangeAsync(
                "Department", department.Id, "AiAssignmentWeightsJson",
                previousWeights, suggestion.SuggestedWeightsJson,
                reason: "ai_learning_auto", changedBy: "ai-assignment-learning");
        }

        db.AiWeightSuggestions.Add(suggestion);
        await db.SaveChangesAsync(ct);
        return 1;
    }

    // ── Consulta e decisão ───────────────────────────────────────────────

    public async Task<DepartmentLearningSuggestionsDto> GetSuggestionsAsync(
        Guid departmentId, CancellationToken ct = default)
    {
        var skills = await db.TechnicianSkillSuggestions.AsNoTracking()
            .Where(s => s.DepartmentId == departmentId && s.Status == StatusPending)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var weights = await db.AiWeightSuggestions.AsNoTracking()
            .Where(s => s.DepartmentId == departmentId && s.Status == StatusPending)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var userIds = skills.Select(s => s.UserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Login })
            .ToListAsync(ct);
        var nameById = names.ToDictionary(
            u => u.Id,
            u => string.IsNullOrWhiteSpace(u.FullName) ? u.Login : u.FullName);

        return new DepartmentLearningSuggestionsDto(
            skills.Select(s => MapSkill(s, nameById.TryGetValue(s.UserId, out var n) ? n : null)).ToList(),
            weights.Select(MapWeight).ToList());
    }

    public async Task<TechnicianSkillSuggestionDto?> ApplySkillSuggestionAsync(
        Guid departmentId, Guid suggestionId, Guid? actorUserId, CancellationToken ct = default)
    {
        var suggestion = await db.TechnicianSkillSuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.DepartmentId == departmentId, ct);
        if (suggestion is null || suggestion.Status != StatusPending) return null;

        var member = await db.DepartmentMembers
            .FirstOrDefaultAsync(m => m.DepartmentId == departmentId && m.UserId == suggestion.UserId, ct);
        if (member is null) return null;

        var tags = ParseTags(suggestion.SuggestedTagsJson);
        member.SkillTagsJson = MergeTags(member.SkillTagsJson, tags);

        suggestion.Status = StatusApplied;
        suggestion.AppliedTagsJson = JsonSerializer.Serialize(tags);
        suggestion.DecidedAt = DateTime.UtcNow;
        suggestion.DecidedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == suggestion.UserId)
            .Select(u => new { u.FullName, u.Login })
            .FirstOrDefaultAsync(ct);
        var name = user is null ? null : (string.IsNullOrWhiteSpace(user.FullName) ? user.Login : user.FullName);
        return MapSkill(suggestion, name);
    }

    public async Task<bool> DiscardSkillSuggestionAsync(
        Guid departmentId, Guid suggestionId, Guid? actorUserId, CancellationToken ct = default)
    {
        var suggestion = await db.TechnicianSkillSuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.DepartmentId == departmentId, ct);
        if (suggestion is null || suggestion.Status != StatusPending) return false;

        suggestion.Status = StatusDiscarded;
        suggestion.DecidedAt = DateTime.UtcNow;
        suggestion.DecidedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<AiWeightSuggestionDto?> ApplyWeightSuggestionAsync(
        Guid departmentId, Guid suggestionId, Guid? actorUserId, CancellationToken ct = default)
    {
        var suggestion = await db.AiWeightSuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.DepartmentId == departmentId, ct);
        if (suggestion is null || suggestion.Status != StatusPending) return null;

        var department = await db.Departments.FirstOrDefaultAsync(d => d.Id == departmentId, ct);
        if (department is null) return null;

        department.AiAssignmentWeightsJson = suggestion.SuggestedWeightsJson;
        suggestion.Status = StatusApplied;
        suggestion.DecidedAt = DateTime.UtcNow;
        suggestion.DecidedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);
        return MapWeight(suggestion);
    }

    public async Task<bool> DiscardWeightSuggestionAsync(
        Guid departmentId, Guid suggestionId, Guid? actorUserId, CancellationToken ct = default)
    {
        var suggestion = await db.AiWeightSuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.DepartmentId == departmentId, ct);
        if (suggestion is null || suggestion.Status != StatusPending) return false;

        suggestion.Status = StatusDiscarded;
        suggestion.DecidedAt = DateTime.UtcNow;
        suggestion.DecidedByUserId = actorUserId;
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task SupersedeSkillSuggestionsAsync(Guid departmentId, Guid userId, CancellationToken ct)
    {
        var pending = await db.TechnicianSkillSuggestions
            .Where(s => s.DepartmentId == departmentId && s.UserId == userId && s.Status == StatusPending)
            .ToListAsync(ct);

        foreach (var item in pending)
        {
            item.Status = StatusDiscarded;
            item.DecidedAt = DateTime.UtcNow;
        }
    }

    private async Task SupersedeWeightSuggestionsAsync(Guid departmentId, CancellationToken ct)
    {
        var pending = await db.AiWeightSuggestions
            .Where(s => s.DepartmentId == departmentId && s.Status == StatusPending)
            .ToListAsync(ct);

        foreach (var item in pending)
        {
            item.Status = StatusDiscarded;
            item.DecidedAt = DateTime.UtcNow;
        }
    }

    internal static IReadOnlyList<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
            return values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string MergeTags(string? existingJson, IReadOnlyList<string> incoming)
    {
        var merged = ParseTags(existingJson)
            .Concat(incoming.Where(v => !string.IsNullOrWhiteSpace(v)))
            .Select(v => v.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
        return JsonSerializer.Serialize(merged);
    }

    private static AiAssignmentWeightsDto ParseWeights(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return AssignmentScorer.DefaultWeights;
        try
        {
            return JsonSerializer.Deserialize<AiAssignmentWeightsDto>(json, JsonOptions)
                   ?? AssignmentScorer.DefaultWeights;
        }
        catch (JsonException)
        {
            return AssignmentScorer.DefaultWeights;
        }
    }

    private static List<AssignmentCandidateDto> DeserializeCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AssignmentCandidateDto>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var ordered = values.OrderBy(v => v).ToList();
        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
    }

    private static TechnicianSkillSuggestionDto MapSkill(TechnicianSkillSuggestion suggestion, string? userName)
        => new(
            suggestion.Id, suggestion.DepartmentId, suggestion.UserId, userName,
            suggestion.WindowDays,
            ParseTags(suggestion.SuggestedTagsJson),
            ParseTags(suggestion.AppliedTagsJson),
            suggestion.EvidenceJson, suggestion.Status, suggestion.AutoApplied,
            suggestion.CreatedAt, suggestion.DecidedAt, suggestion.DecidedByUserId);

    private static AiWeightSuggestionDto MapWeight(AiWeightSuggestion suggestion)
        => new(
            suggestion.Id, suggestion.DepartmentId, suggestion.CycleDays,
            suggestion.WindowStart, suggestion.WindowEnd,
            ParseWeights(suggestion.CurrentWeightsJson),
            ParseWeights(suggestion.SuggestedWeightsJson),
            suggestion.EvidenceJson, suggestion.Status, suggestion.AutoApplied,
            suggestion.CreatedAt, suggestion.DecidedAt, suggestion.DecidedByUserId);
}
