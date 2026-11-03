using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Services;

namespace Discovery.Tests;

/// <summary>
/// O fingerprint do sensor-sync deve mudar quando algo que o AGENTE usa muda
/// (tipo, alvo, intervalo, canal, extrator, script) e NÃO mudar quando só o
/// limiar muda (o agente não recebe limiares — a decisão é do servidor).
/// </summary>
public class SensorFingerprintTests
{
    private static SensorDefinition Definition() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Name = "Disco C:",
        SensorType = SensorType.DiskSpace,
        Target = "C:",
        IntervalSeconds = 300,
        TimeoutSeconds = 60,
        WarningThreshold = 15,
        CriticalThreshold = 10,
        LastUpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public void ThresholdChange_DoesNotChangeFingerprint()
    {
        var before = SensorFingerprint.Compute([Definition()], new Dictionary<Guid, AutomationScriptDefinition>());
        var definition = Definition();
        definition.WarningThreshold = 20;
        definition.CriticalThreshold = 12;
        var after = SensorFingerprint.Compute([definition], new Dictionary<Guid, AutomationScriptDefinition>());

        Assert.That(after, Is.EqualTo(before),
            "limiar é decisão do servidor; mudar só ele NÃO deve exigir re-sync do agente");
    }

    [Test]
    public void IntervalChange_ChangesFingerprint()
    {
        var before = SensorFingerprint.Compute([Definition()], new Dictionary<Guid, AutomationScriptDefinition>());
        var definition = Definition();
        definition.IntervalSeconds = 600;
        var after = SensorFingerprint.Compute([definition], new Dictionary<Guid, AutomationScriptDefinition>());

        Assert.That(after, Is.Not.EqualTo(before));
    }

    [Test]
    public void ScriptContentChange_ChangesFingerprint()
    {
        var definition = Definition();
        definition.SensorType = SensorType.Script;
        definition.ScriptId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        definition.ChannelsJson = "[{\"key\":\"pending_count\"}]";

        var script = new AutomationScriptDefinition
        {
            Id = definition.ScriptId!.Value,
            Name = "updates",
            Version = "1.0.0",
            Content = "echo 0",
            ScriptType = AutomationScriptType.PowerShell
        };

        var before = SensorFingerprint.Compute([definition], new Dictionary<Guid, AutomationScriptDefinition> { [script.Id] = script });
        script.Content = "echo 1";
        var after = SensorFingerprint.Compute([definition], new Dictionary<Guid, AutomationScriptDefinition> { [script.Id] = script });

        Assert.That(after, Is.Not.EqualTo(before));
    }

    [Test]
    public void Fingerprint_IsStableAcrossCalls()
    {
        var scripts = new Dictionary<Guid, AutomationScriptDefinition>();
        var first = SensorFingerprint.Compute([Definition()], scripts);
        var second = SensorFingerprint.Compute([Definition()], scripts);

        Assert.That(second, Is.EqualTo(first));
        Assert.That(first, Has.Length.EqualTo(64), "SHA-256 em hex minúsculo");
    }
}
