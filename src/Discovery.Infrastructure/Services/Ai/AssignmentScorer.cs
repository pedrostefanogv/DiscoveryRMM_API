using Discovery.Core.DTOs;

namespace Discovery.Infrastructure.Services.Ai;

/// <summary>
/// Entrada do scorer por atendente: perfil cadastrado no departamento + métricas
/// históricas + afinidade textual com chamados semelhantes já resolvidos.
/// </summary>
public sealed record TechnicianCandidateInput(
    Guid UserId,
    string? UserName,
    IReadOnlyList<string> SkillTags,
    int SkillLevel,
    int? MaxOpenTickets,
    decimal Weight,
    bool AcceptsAiAssignment,
    TechnicianMetricsDto Metrics,
    double Affinity,
    string? BestAffinityTicketTitle);

/// <summary>Sinais do chamado usados na pontuação determinística.</summary>
public sealed record TicketScoringContext(
    int Difficulty,
    IReadOnlyList<string> Tags,
    string? Category,
    double? DepartmentMedianResolutionMinutes);

/// <summary>
/// Score determinístico dos candidatos da triagem por IA. É uma função pura
/// (sem I/O) para poder ser testada isoladamente e para servir de rede de
/// segurança quando a resposta do modelo é inválida ou pouco confiável.
///
/// score = skill + afinidade + performance + carga + csat + sla_quality
///         (pesos configuráveis, normalizados para somar 1)
///         * peso do gestor - penalidade de capacidade
/// </summary>
public static class AssignmentScorer
{
    /// <summary>Pesos padrão usados quando o departamento não configurou nada.</summary>
    public static AiAssignmentWeightsDto DefaultWeights { get; } = new();

    public static AssignmentCandidateDto Score(
        TechnicianCandidateInput candidate,
        TicketScoringContext ticket,
        AiAssignmentWeightsDto weights,
        int departmentMaxOpen)
    {
        var normalized = Normalize(weights);
        var metrics = candidate.Metrics;

        var skill = SkillScore(candidate.SkillTags, ticket, candidate.SkillLevel);
        var affinity = Clamp(candidate.Affinity);
        var performance = PerformanceScore(candidate, ticket, metrics);
        var load = departmentMaxOpen > 0 ? 1 - Clamp((double)metrics.OpenNow / departmentMaxOpen) : 1;
        var csat = metrics.CsatRatedCount >= 3 && metrics.CsatAverage.HasValue
            ? Clamp(metrics.CsatAverage.Value / 5.0)
            : 0.5;
        var sla = 1 - Clamp(metrics.SlaBreachRate);

        var baseScore =
            normalized.Skill * skill +
            normalized.Affinity * affinity +
            normalized.Performance * performance +
            normalized.Load * load +
            normalized.Csat * csat +
            normalized.SlaQuality * sla;

        var weightFactor = Math.Clamp((double)candidate.Weight, 0.1, 3.0);
        var overCapacity = candidate.MaxOpenTickets.HasValue
            && metrics.OpenNow >= candidate.MaxOpenTickets.Value;

        var score = baseScore * weightFactor;
        if (overCapacity) score -= 0.15;

        return new AssignmentCandidateDto(
            candidate.UserId,
            candidate.UserName,
            Clamp(score),
            skill,
            affinity,
            performance,
            load,
            csat,
            sla,
            overCapacity,
            metrics.OpenNow,
            candidate.BestAffinityTicketTitle);
    }

    /// <summary>Ordenação estável: score desc, menos abertos, e UserId como desempate final.</summary>
    public static IReadOnlyList<AssignmentCandidateDto> Rank(IEnumerable<AssignmentCandidateDto> candidates)
        => candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.OpenNow)
            .ThenBy(c => c.UserId)
            .ToList();

    public static AiAssignmentWeightsDto Normalize(AiAssignmentWeightsDto weights)
    {
        var total = weights.Skill + weights.Affinity + weights.Performance
                    + weights.Load + weights.Csat + weights.SlaQuality;
        if (total <= 0) return DefaultWeights;

        return new AiAssignmentWeightsDto(
            weights.Skill / total,
            weights.Affinity / total,
            weights.Performance / total,
            weights.Load / total,
            weights.Csat / total,
            weights.SlaQuality / total);
    }

    private static double SkillScore(
        IReadOnlyList<string> candidateTags, TicketScoringContext ticket, int skillLevel)
    {
        var levelFactor = 0.6 + 0.1 * (Math.Clamp(skillLevel, 1, 5) - 1);

        var ticketTags = ticket.Tags
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        if (ticketTags.Count == 0 || candidateTags.Count == 0)
            return 0.3 * levelFactor;

        var best = 0.1;
        foreach (var tag in ticketTags)
        {
            foreach (var skill in candidateTags)
            {
                var normalized = skill.Trim().ToLowerInvariant();
                if (normalized.Length == 0) continue;

                if (normalized == tag)
                {
                    best = Math.Max(best, 1.0);
                }
                else if (normalized.Contains(tag) || tag.Contains(normalized))
                {
                    best = Math.Max(best, 0.5);
                }
            }
        }

        return best * levelFactor;
    }

    private static double PerformanceScore(
        TechnicianCandidateInput candidate, TicketScoringContext ticket, TechnicianMetricsDto metrics)
    {
        var median = ticket.DepartmentMedianResolutionMinutes;
        var average = metrics.AvgResolutionMinutes;

        // 1.0 = na mediana ou mais rápido; 0 = 2x mais lento que a mediana.
        var speed = median is > 0 && average is > 0
            ? Clamp(1 - (average.Value - median.Value) / (2 * median.Value))
            : 0.5;

        var reopen = 1 - Clamp(metrics.ReopenRate);

        var difficultyFit = metrics.DifficultyAverage.HasValue
            ? 1 - Clamp(Math.Abs(metrics.DifficultyAverage.Value - ticket.Difficulty) / 4.0)
            : 0.6;

        return 0.60 * speed + 0.25 * reopen + 0.15 * difficultyFit;
    }

    private static double Clamp(double value, double min = 0, double max = 1)
        => double.IsNaN(value) ? min : Math.Clamp(value, min, max);
}
