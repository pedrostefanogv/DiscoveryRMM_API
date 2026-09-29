using Discovery.Core.DTOs;
using Discovery.Core.Enums;
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

    // Fonte de verdade = notificationMode. Ausente, deriva do booleano antigo
    // RequiresApproval (true = Prompt, false = Silent).
    [TestCase(false, null, AutomationNotificationMode.Silent)]
    [TestCase(true, null, AutomationNotificationMode.Prompt)]
    [TestCase(false, AutomationNotificationMode.Silent, AutomationNotificationMode.Silent)]
    [TestCase(false, AutomationNotificationMode.Prompt, AutomationNotificationMode.Prompt)]
    [TestCase(false, AutomationNotificationMode.Toast, AutomationNotificationMode.Toast)]
    [TestCase(true, AutomationNotificationMode.Toast, AutomationNotificationMode.Toast)]
    public void ResolveNotificationMode_ExplicitWinsAndLegacyFallsBack(
        bool requiresApproval,
        AutomationNotificationMode? explicitMode,
        AutomationNotificationMode expected)
    {
        var request = new CreateAutomationTaskRequest
        {
            RequiresApproval = requiresApproval,
            NotificationMode = explicitMode
        };

        Assert.That(AutomationTaskService.ResolveNotificationMode(request), Is.EqualTo(expected));
    }
}
