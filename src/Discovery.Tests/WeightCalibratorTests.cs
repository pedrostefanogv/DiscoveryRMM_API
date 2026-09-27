using Discovery.Core.DTOs;
using Discovery.Infrastructure.Services.Ai;

namespace Discovery.Tests;

/// <summary>
/// Recalibração de pesos pela taxa de override, com limites por ciclo e faixa.
/// </summary>
public class WeightCalibratorTests
{
    private static readonly AiAssignmentWeightsDto Current = new(0.25, 0.25, 0.20, 0.15, 0.10, 0.05);

    private static WeightCalibrationSample Sample(bool overridden, double csat, double skillMedian, double chosenSkill = 0.5)
        => new(
            Guid.NewGuid(),
            overridden,
            new Dictionary<string, double>
            {
                ["skill"] = chosenSkill,
                ["affinity"] = 0.5,
                ["performance"] = 0.5,
                ["load"] = 0.5,
                ["csat"] = csat,
                ["slaQuality"] = 0.5
            },
            new Dictionary<string, double>
            {
                ["skill"] = skillMedian,
                ["affinity"] = 0.5,
                ["performance"] = 0.5,
                ["load"] = 0.5,
                ["csat"] = 0.5,
                ["slaQuality"] = 0.5
            });

    [Test]
    public void Calibrate_RequiresMinimumSamples()
    {
        var samples = Enumerable.Range(0, 5).Select(_ => Sample(false, 0.9, 0.5)).ToList();

        var result = WeightCalibrator.Calibrate(Current, samples, 0.10m, 0.05m, 0.50m);

        Assert.That(result.HasProposal, Is.False);
        Assert.That(result.Suggested, Is.EqualTo(Current));
    }

    [Test]
    public void Calibrate_LowersOvervaluedDimension()
    {
        // Overrides concentrados quando o escolhido tinha CSAT acima da mediana
        // => CSAT supervalorizado => peso desce.
        var samples = new List<WeightCalibrationSample>();
        samples.AddRange(Enumerable.Range(0, 12).Select(_ => Sample(true, 0.95, 0.5)));
        samples.AddRange(Enumerable.Range(0, 12).Select(_ => Sample(false, 0.20, 0.5)));

        var result = WeightCalibrator.Calibrate(Current, samples, 0.10m, 0.05m, 0.50m);

        Assert.That(result.HasProposal, Is.True);
        Assert.That(result.Suggested.Csat, Is.LessThan(Current.Csat));
        Assert.That(result.Evidence["csat"].Delta, Is.LessThan(0));
    }

    [Test]
    public void Calibrate_RaisesUndervaluedDimension()
    {
        // Overrides quando o escolhido tinha SKILL abaixo da mediana => skill
        // subvalorizado => peso sobe.
        var samples = new List<WeightCalibrationSample>();
        samples.AddRange(Enumerable.Range(0, 12).Select(_ => Sample(true, 0.5, 0.8, chosenSkill: 0.1)));
        samples.AddRange(Enumerable.Range(0, 12).Select(_ => Sample(false, 0.5, 0.5, chosenSkill: 0.9)));

        var result = WeightCalibrator.Calibrate(Current, samples, 0.10m, 0.05m, 0.50m);

        Assert.That(result.HasProposal, Is.True);
        Assert.That(result.Suggested.Skill, Is.GreaterThan(Current.Skill));
    }

    [Test]
    public void Calibrate_RespectsMaxDeltaPerCycle()
    {
        var samples = new List<WeightCalibrationSample>();
        samples.AddRange(Enumerable.Range(0, 30).Select(_ => Sample(true, 1.0, 0.5)));
        samples.AddRange(Enumerable.Range(0, 30).Select(_ => Sample(false, 0.0, 0.5)));

        var result = WeightCalibrator.Calibrate(Current, samples, 0.05m, 0.05m, 0.50m);

        Assert.That(result.HasProposal, Is.True);
        var delta = Current.Csat - result.Suggested.Csat;
        // O delta observado é alterado pela renormalização, então validamos o teto
        // pela evidência (que guarda o delta bruto aplicado).
        Assert.That(Math.Abs(result.Evidence["csat"].Delta), Is.LessThanOrEqualTo(0.05 + 1e-9));
        Assert.That(delta, Is.GreaterThan(0));
    }

    [Test]
    public void Calibrate_KeepsWeightsWithinBoundsAndSummingOne()
    {
        var samples = new List<WeightCalibrationSample>();
        samples.AddRange(Enumerable.Range(0, 30).Select(_ => Sample(true, 1.0, 0.2, chosenSkill: 0.1)));
        samples.AddRange(Enumerable.Range(0, 30).Select(_ => Sample(false, 0.0, 0.8, chosenSkill: 0.9)));

        var result = WeightCalibrator.Calibrate(Current, samples, 0.10m, 0.05m, 0.50m);
        var suggested = result.Suggested;

        foreach (var value in new[]
                 {
                     suggested.Skill, suggested.Affinity, suggested.Performance,
                     suggested.Load, suggested.Csat, suggested.SlaQuality
                 })
        {
            Assert.That(value, Is.GreaterThanOrEqualTo(0.05 - 1e-9));
            Assert.That(value, Is.LessThanOrEqualTo(0.50 + 1e-9));
        }

        var sum = suggested.Skill + suggested.Affinity + suggested.Performance
                  + suggested.Load + suggested.Csat + suggested.SlaQuality;
        Assert.That(sum, Is.EqualTo(1.0).Within(0.02));
    }

    [Test]
    public void NormalizeWithinBounds_ClampsAndRenormalizes()
    {
        var values = new Dictionary<string, double>
        {
            ["skill"] = 0.90,
            ["affinity"] = 0.02,
            ["performance"] = 0.02,
            ["load"] = 0.02,
            ["csat"] = 0.02,
            ["slaQuality"] = 0.02
        };

        var normalized = WeightCalibrator.NormalizeWithinBounds(values, 0.05, 0.50);

        Assert.That(normalized["skill"], Is.LessThanOrEqualTo(0.50 + 1e-9));
        Assert.That(normalized["affinity"], Is.GreaterThanOrEqualTo(0.05 - 1e-9));
        Assert.That(WeightCalibrator.Dimensions.Sum(d => normalized[d]), Is.EqualTo(1.0).Within(0.02));
    }
}
