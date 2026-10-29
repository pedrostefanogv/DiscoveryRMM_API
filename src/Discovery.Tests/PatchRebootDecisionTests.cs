using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class PatchRebootDecisionTests
{
    [Test]
    public void NoPendingRebootMeansNoAction()
    {
        var (action, reason) = PatchRebootDecision.Evaluate(
            PatchRebootPolicy.MaintenanceWindow, rebootAllowed: true, rebootRequired: false, inMaintenanceWindow: true);

        Assert.That(action, Is.EqualTo(PatchRebootAction.None));
        Assert.That(reason, Does.Contain("sem reinício"));
    }

    [Test]
    public void NeverPolicyNeverActs()
    {
        var (action, _) = PatchRebootDecision.Evaluate(
            PatchRebootPolicy.Never, rebootAllowed: true, rebootRequired: true, inMaintenanceWindow: true);

        Assert.That(action, Is.EqualTo(PatchRebootAction.None),
            "D02: o produto nunca reinicia automaticamente; Never não pode gerar ação");
    }

    [Test]
    public void NotifyPolicyNotifiesOperator()
    {
        var (action, reason) = PatchRebootDecision.Evaluate(
            PatchRebootPolicy.Notify, rebootAllowed: false, rebootRequired: true, inMaintenanceWindow: false);

        Assert.That(action, Is.EqualTo(PatchRebootAction.NotifyOperator));
        Assert.That(reason, Does.Contain("Notify"));
    }

    [Test]
    public void MaintenanceWindowWithoutRebootAllowedOnlyNotifies()
    {
        var (action, reason) = PatchRebootDecision.Evaluate(
            PatchRebootPolicy.MaintenanceWindow, rebootAllowed: false, rebootRequired: true, inMaintenanceWindow: true);

        Assert.That(action, Is.EqualTo(PatchRebootAction.NotifyOperator));
        Assert.That(reason, Does.Contain("RebootAllowed"));
    }

    [Test]
    public void MaintenanceWindowWithRebootAllowedHintsWindow()
    {
        var inside = PatchRebootDecision.Evaluate(
            PatchRebootPolicy.MaintenanceWindow, rebootAllowed: true, rebootRequired: true, inMaintenanceWindow: true);
        var outside = PatchRebootDecision.Evaluate(
            PatchRebootPolicy.MaintenanceWindow, rebootAllowed: true, rebootRequired: true, inMaintenanceWindow: false);

        Assert.That(inside.Action, Is.EqualTo(PatchRebootAction.NotifyWithWindowHint));
        Assert.That(inside.Reason, Does.Contain("janela aberta"));
        Assert.That(outside.Action, Is.EqualTo(PatchRebootAction.NotifyWithWindowHint));
        Assert.That(outside.Reason, Does.Contain("fora da janela"));
    }

    [Test]
    public void DecisionNeverRequestsAutomaticRestart()
    {
        foreach (var policy in Enum.GetValues<PatchRebootPolicy>())
        {
            foreach (var allowed in new[] { true, false })
            {
                var (action, _) = PatchRebootDecision.Evaluate(policy, allowed, true, true);
                Assert.That(Enum.GetNames<PatchRebootAction>(), Does.Not.Contain("Restart"),
                    "D02: nenhuma ação de reboot automático pode existir no enum");
                Assert.That(action, Is.AnyOf(
                        PatchRebootAction.None,
                        PatchRebootAction.NotifyOperator,
                        PatchRebootAction.NotifyWithWindowHint),
                    "toda decisão precisa ser um sinal operacional, nunca um reboot");
            }
        }
    }
}
