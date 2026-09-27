using Discovery.Core.DTOs;
using Discovery.Infrastructure.Services.Ai;

namespace Discovery.Tests;

/// <summary>
/// Score determinístico da triagem por IA: cada dimensão, penalidade de
/// capacidade, peso do gestor e ordenação estável.
/// </summary>
public class AiAssignmentScorerTests
{
    private static readonly IReadOnlyList<string> TicketTags = ["rede", "impressora"];

    private static TicketScoringContext Context(double? median = 120, int difficulty = 3)
        => new(difficulty, TicketTags, "Infra", median);

    private static TechnicianMetricsDto Metrics(
        Guid userId,
        int openNow = 0,
        double? avgResolution = 100,
        double? csat = null,
        int csatCount = 0,
        double slaBreach = 0,
        double reopen = 0,
        double? difficulty = null)
        => new(userId, 90, 10, 8, openNow, 30, avgResolution, avgResolution, slaBreach, reopen,
            csat, csatCount, difficulty, [], [], DateTime.UtcNow);

    private static TechnicianCandidateInput Candidate(
        Guid userId,
        IReadOnlyList<string>? skills = null,
        int level = 3,
        int? maxOpen = null,
        decimal weight = 1.0m,
        double affinity = 0,
        TechnicianMetricsDto? metrics = null)
        => new(userId, userId.ToString(), skills ?? [], level, maxOpen, weight, true,
            metrics ?? Metrics(userId), affinity, null);

    [Test]
    public void ExactSkillMatch_BeatsUnrelatedCandidate()
    {
        var matched = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), ["rede"]), Context(), AssignmentScorer.DefaultWeights, 10);
        var unrelated = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), ["excel"]), Context(), AssignmentScorer.DefaultWeights, 10);

        Assert.That(matched.SkillScore, Is.GreaterThan(unrelated.SkillScore));
        Assert.That(matched.Score, Is.GreaterThan(unrelated.Score));
    }

    [Test]
    public void SkillLevel_RaisesSkillScore()
    {
        var senior = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), ["rede"], level: 5), Context(), AssignmentScorer.DefaultWeights, 10);
        var junior = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), ["rede"], level: 1), Context(), AssignmentScorer.DefaultWeights, 10);

        Assert.That(senior.SkillScore, Is.GreaterThan(junior.SkillScore));
    }

    [Test]
    public void LowerLoad_Wins_WhenEverythingElseIsEqual()
    {
        var idle = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), metrics: Metrics(Guid.NewGuid(), openNow: 1)),
            Context(), AssignmentScorer.DefaultWeights, 10);
        var busy = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), metrics: Metrics(Guid.NewGuid(), openNow: 9)),
            Context(), AssignmentScorer.DefaultWeights, 10);

        Assert.That(idle.LoadScore, Is.GreaterThan(busy.LoadScore));
        Assert.That(idle.Score, Is.GreaterThan(busy.Score));
    }

    [Test]
    public void OverCapacity_AppliesPenalty()
    {
        var userId = Guid.NewGuid();
        var under = AssignmentScorer.Score(
            Candidate(userId, metrics: Metrics(userId, openNow: 4)), Context(), AssignmentScorer.DefaultWeights, 10);
        var over = AssignmentScorer.Score(
            Candidate(userId, maxOpen: 5, metrics: Metrics(userId, openNow: 5)), Context(), AssignmentScorer.DefaultWeights, 10);

        Assert.That(over.OverCapacity, Is.True);
        Assert.That(under.OverCapacity, Is.False);
        Assert.That(under.Score, Is.GreaterThan(over.Score));
    }

    [Test]
    public void ManagerWeight_MultipliesScore_AndIsClamped()
    {
        var heavy = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), weight: 2.0m), Context(), AssignmentScorer.DefaultWeights, 10);
        var normal = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), weight: 1.0m), Context(), AssignmentScorer.DefaultWeights, 10);

        Assert.That(heavy.Score, Is.GreaterThan(normal.Score));
        Assert.That(heavy.Score, Is.LessThanOrEqualTo(1.0));
    }

    [Test]
    public void Affinity_IsClampedToUnitRange()
    {
        var candidate = AssignmentScorer.Score(
            Candidate(Guid.NewGuid(), affinity: 7.5), Context(), AssignmentScorer.DefaultWeights, 10);

        Assert.That(candidate.AffinityScore, Is.EqualTo(1.0));
    }

    [Test]
    public void Csal_IsNeutral_WhenThereAreTooFewRatings()
    {
        var userId = Guid.NewGuid();
        var few = AssignmentScorer.Score(
            Candidate(userId, metrics: Metrics(userId, csat: 5, csatCount: 1)),
            Context(), AssignmentScorer.DefaultWeights, 10);
        var many = AssignmentScorer.Score(
            Candidate(userId, metrics: Metrics(userId, csat: 5, csatCount: 10)),
            Context(), AssignmentScorer.DefaultWeights, 10);

        Assert.That(few.CsatScore, Is.EqualTo(0.5));
        Assert.That(many.CsatScore, Is.EqualTo(1.0));
    }

    [Test]
    public void Normalize_RescalesWeightsToSumOne()
    {
        var normalized = AssignmentScorer.Normalize(new AiAssignmentWeightsDto(2, 2, 2, 2, 1, 1));

        Assert.That(normalized.Skill, Is.EqualTo(0.2).Within(1e-9));
        Assert.That(normalized.SlaQuality, Is.EqualTo(0.1).Within(1e-9));
    }

    [Test]
    public void Rank_IsStable_OnTies()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var candidateA = AssignmentScorer.Score(
            Candidate(second, ["rede"]), Context(), AssignmentScorer.DefaultWeights, 10);
        var candidateB = AssignmentScorer.Score(
            Candidate(first, ["rede"]), Context(), AssignmentScorer.DefaultWeights, 10);

        var ranked = AssignmentScorer.Rank([candidateA, candidateB]);

        Assert.That(ranked[0].UserId, Is.EqualTo(first));
    }
}
