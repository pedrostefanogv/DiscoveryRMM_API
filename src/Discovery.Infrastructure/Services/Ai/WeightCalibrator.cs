using Discovery.Core.DTOs;

namespace Discovery.Infrastructure.Services.Ai;

/// <summary>Uma decisão aplicada (com ou sem override) e os sub-scores do escolhido vs a mediana do pool.</summary>
public sealed record WeightCalibrationSample(
    Guid DecisionId,
    bool Overridden,
    IReadOnlyDictionary<string, double> ChosenSubScores,
    IReadOnlyDictionary<string, double> PoolMedianSubScores);

/// <summary>Evidência por dimensão, exibida ao gestor junto da proposta.</summary>
public sealed record WeightCalibrationEvidence(
    int Samples,
    int Overrides,
    int SamplesWhenHigh,
    int OverridesWhenHigh,
    int SamplesWhenLow,
    int OverridesWhenLow,
    double Strength,
    double Delta);

public sealed record WeightCalibrationResult(
    bool HasProposal,
    AiAssignmentWeightsDto Current,
    AiAssignmentWeightsDto Suggested,
    IReadOnlyDictionary<string, WeightCalibrationEvidence> Evidence,
    string Reason);

/// <summary>
/// Recalibração dos pesos do score a partir da taxa de override manual. Função
/// pura para ser testável sem banco.
///
/// Racional: se os overrides acontecem mais quando o escolhido tinha score ALTO
/// numa dimensão, essa dimensão está supervalorizada (peso desce); se acontecem
/// mais quando o escolhido tinha score BAIXO, ela está subvalorizada (peso sobe).
///
/// Salvaguardas: amostra mínima, evidência mínima por lado, força mínima de
/// 5 pontos percentuais, delta limitado por ciclo, faixa min/max por dimensão e
/// renormalização para somar 1,0.
/// </summary>
public static class WeightCalibrator
{
    public static readonly string[] Dimensions =
        ["skill", "affinity", "performance", "load", "csat", "slaQuality"];

    public const double MinimumStrength = 0.05;
    public const int MinimumSamplesPerSide = 3;

    public static WeightCalibrationResult Calibrate(
        AiAssignmentWeightsDto current,
        IReadOnlyList<WeightCalibrationSample> samples,
        decimal maxDeltaPerCycle,
        decimal minWeight,
        decimal maxWeight,
        int minimumSamples = 20)
    {
        var evidence = new Dictionary<string, WeightCalibrationEvidence>();
        var values = ToDictionary(current);

        if (samples.Count < minimumSamples)
            return NoProposal(current, evidence,
                "Amostra insuficiente: " + samples.Count + " de " + minimumSamples + " decisões aplicadas no período.");

        var changed = false;
        var maxDelta = (double)maxDeltaPerCycle;

        foreach (var dimension in Dimensions)
        {
            int samplesHigh = 0, overridesHigh = 0, samplesLow = 0, overridesLow = 0;

            foreach (var sample in samples)
            {
                if (!sample.ChosenSubScores.TryGetValue(dimension, out var chosen)) continue;
                if (!sample.PoolMedianSubScores.TryGetValue(dimension, out var median)) continue;

                if (chosen >= median) { samplesHigh++; if (sample.Overridden) overridesHigh++; }
                else { samplesLow++; if (sample.Overridden) overridesLow++; }
            }

            double strength = 0;
            double delta = 0;

            if (samplesHigh >= MinimumSamplesPerSide && samplesLow >= MinimumSamplesPerSide)
            {
                var rateHigh = overridesHigh / (double)samplesHigh;
                var rateLow = overridesLow / (double)samplesLow;
                var difference = rateHigh - rateLow;
                strength = Math.Abs(difference);

                if (strength >= MinimumStrength)
                {
                    // Dimensão supervalorizada => peso desce (direção negativa).
                    delta = -Math.Sign(difference) * Math.Min(maxDelta, strength);
                    values[dimension] = Math.Max(0.0, values[dimension] + delta);
                    changed = true;
                }
            }

            evidence[dimension] = new WeightCalibrationEvidence(
                samplesHigh + samplesLow, overridesHigh + overridesLow,
                samplesHigh, overridesHigh, samplesLow, overridesLow, strength, delta);
        }

        if (!changed)
            return NoProposal(current, evidence, "Sem evidência suficiente para ajustar os pesos neste ciclo.");

        var bounded = NormalizeWithinBounds(values, (double)minWeight, (double)maxWeight);
        var suggested = FromDictionary(bounded);

        return new WeightCalibrationResult(true, current, suggested, evidence, "Proposta gerada a partir da taxa de override.");
    }

    private static WeightCalibrationResult NoProposal(
        AiAssignmentWeightsDto current,
        IReadOnlyDictionary<string, WeightCalibrationEvidence> evidence,
        string reason)
        => new(false, current, current, evidence, reason);

    private static Dictionary<string, double> ToDictionary(AiAssignmentWeightsDto weights) => new()
    {
        ["skill"] = weights.Skill,
        ["affinity"] = weights.Affinity,
        ["performance"] = weights.Performance,
        ["load"] = weights.Load,
        ["csat"] = weights.Csat,
        ["slaQuality"] = weights.SlaQuality
    };

    private static AiAssignmentWeightsDto FromDictionary(IReadOnlyDictionary<string, double> values) => new(
        values["skill"], values["affinity"], values["performance"],
        values["load"], values["csat"], values["slaQuality"]);

    /// <summary>
    /// Aplica a faixa min/max e renormaliza para somar 1,0. O clamping e a
    /// renormalização são iterados: escalar proporcionalmente pode sair da faixa,
    /// e clampar pode desbalancear a soma.
    /// </summary>
    public static Dictionary<string, double> NormalizeWithinBounds(
        Dictionary<string, double> values, double min, double max)
    {
        var result = new Dictionary<string, double>(values);
        if (max < min) (min, max) = (max, min);

        // Escalar proporcionalmente TODAS as dimensões podia ultrapassar a faixa
        // (o clamp seguinte desbalanceava a soma de novo). Aqui o resíduo é
        // distribuído apenas entre as dimensões que ainda têm folga na direção
        // necessária, o que converge respeitando min/max e soma 1,0.
        for (var pass = 0; pass < 16; pass++)
        {
            foreach (var dimension in Dimensions)
                result[dimension] = Math.Clamp(result[dimension], min, max);

            var sum = Dimensions.Sum(d => result[d]);
            var residual = 1.0 - sum;
            if (Math.Abs(residual) <= 1e-9) break;

            var free = Dimensions
                .Where(d => residual > 0 ? result[d] < max - 1e-9 : result[d] > min + 1e-9)
                .ToList();

            if (free.Count == 0) break;

            var totalFree = free.Sum(d => result[d]);
            foreach (var dimension in free)
            {
                var share = totalFree > 1e-9
                    ? result[dimension] / totalFree
                    : 1.0 / free.Count;
                result[dimension] += residual * share;
            }
        }

        foreach (var dimension in Dimensions)
            result[dimension] = Math.Clamp(result[dimension], min, max);

        return result;
    }
}
