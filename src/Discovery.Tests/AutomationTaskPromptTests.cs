using Discovery.Infrastructure.Services;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Normalizacao do tempo do prompt de notificacao ao usuario (Welcome PSADT).
/// 0/negativo = default 60s; fora da faixa sofre clamp para 5..3600.
/// </summary>
public class AutomationTaskPromptTests
{
    [TestCase(0, 60)]
    [TestCase(-10, 60)]
    [TestCase(1, 5)]
    [TestCase(4, 5)]
    [TestCase(5, 5)]
    [TestCase(60, 60)]
    [TestCase(120, 120)]
    [TestCase(3600, 3600)]
    [TestCase(3601, 3600)]
    [TestCase(int.MaxValue, 3600)]
    public void NormalizePromptTimeout_ClampsToSupportedRange(int input, int expected)
        => Assert.That(AutomationTaskService.NormalizePromptTimeout(input), Is.EqualTo(expected));

    [Test]
    public void DefaultPromptTimeout_IsSixtySeconds()
        => Assert.That(AutomationTaskService.DefaultPromptTimeoutSeconds, Is.EqualTo(60));
}
