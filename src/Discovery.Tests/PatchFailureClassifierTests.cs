using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchFailureClassifierTests
{
    [TestCase("disk_space", "preflight disk_space: espaco livre insuficiente", PatchFailureClassifier.DiskSpace)]
    [TestCase("disk_space", null, PatchFailureClassifier.DiskSpace)]
    [TestCase("service_stopped", "servico do Windows Update stopped", PatchFailureClassifier.Permanent)]
    [TestCase("on_battery", "maquina na bateria", PatchFailureClassifier.Transient)]
    [TestCase("wua_unavailable", null, PatchFailureClassifier.Transient)]
    [TestCase("unsupported", null, PatchFailureClassifier.Permanent)]
    [TestCase("install_failed", "0x80070070: not enough disk", PatchFailureClassifier.DiskSpace)]
    [TestCase("install_failed", "0x80073712 component store corrupt", PatchFailureClassifier.StoreCorrupt)]
    [TestCase("install_failed", "0x8024001E pending restart", PatchFailureClassifier.NeedsReboot)]
    [TestCase("install_failed", "update not applicable to this OS", PatchFailureClassifier.Permanent)]
    [TestCase("install_failed", "0x8024402C proxy error", PatchFailureClassifier.Transient)]
    [TestCase("install_failed", "network interrupted", PatchFailureClassifier.Transient)]
    [TestCase("install_failed", "algo estranho", PatchFailureClassifier.Unknown)]
    [TestCase(null, null, PatchFailureClassifier.Unknown)]
    public void ClassifiesFailure(string? code, string? message, string expected)
    {
        Assert.That(PatchFailureClassifier.Classify(code, message), Is.EqualTo(expected));
    }

    [Test]
    public void OnlyTransientGetsBackoff()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        Assert.That(PatchFailureClassifier.NextAttemptAt(PatchFailureClassifier.Transient, 1, now),
            Is.EqualTo(now.AddMinutes(15)), "1a falha: 15 min");
        Assert.That(PatchFailureClassifier.NextAttemptAt(PatchFailureClassifier.Transient, 2, now),
            Is.EqualTo(now.AddHours(1)), "2a falha: 1 h");
        Assert.That(PatchFailureClassifier.NextAttemptAt(PatchFailureClassifier.Transient, 3, now), Is.Null,
            "tentativas esgotadas nao re-tentam");
        Assert.That(PatchFailureClassifier.NextAttemptAt(PatchFailureClassifier.Permanent, 1, now), Is.Null,
            "falha permanente nunca re-tenta");
        Assert.That(PatchFailureClassifier.NextAttemptAt(PatchFailureClassifier.DiskSpace, 1, now), Is.Null,
            "falha de disco exige acao humana");
    }

    [Test]
    public void RetryDueRespectsBackoffAndLimit()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(PatchFailureClassifier.IsRetryDue(PatchFailureClassifier.Transient, 1, now.AddMinutes(-1), now), Is.True);
        Assert.That(PatchFailureClassifier.IsRetryDue(PatchFailureClassifier.Transient, 1, now.AddMinutes(5), now), Is.False);
        Assert.That(PatchFailureClassifier.IsRetryDue(PatchFailureClassifier.Transient, 3, null, now), Is.False,
            "3 falhas esgotam o retry");
        Assert.That(PatchFailureClassifier.IsRetryDue(PatchFailureClassifier.Permanent, 1, null, now), Is.False);
    }
}
