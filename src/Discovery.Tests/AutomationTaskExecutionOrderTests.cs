using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Defaults e faixa da ordem de execução das tarefas (0–99). O mapa tem de
/// bater com o backfill da migração M222 e com os defaults que o site mostra.
/// </summary>
public class AutomationTaskExecutionOrderTests
{
    [TestCase(AutomationTaskActionType.InstallPackage, 10)]
    [TestCase(AutomationTaskActionType.UpdateOrInstallPackage, 20)]
    [TestCase(AutomationTaskActionType.UpdatePackage, 20)]
    [TestCase(AutomationTaskActionType.RemovePackage, 30)]
    [TestCase(AutomationTaskActionType.RunScript, 40)]
    [TestCase(AutomationTaskActionType.CustomCommand, 50)]
    public void DefaultFor_MatchesActionTypeMap(AutomationTaskActionType actionType, int expected)
        => Assert.That(AutomationTaskExecutionOrder.DefaultFor(actionType), Is.EqualTo(expected));

    [TestCase(0)]
    [TestCase(10)]
    [TestCase(99)]
    public void ResolveForCreate_UsesRequestedValue(int requested)
    {
        var value = AutomationTaskExecutionOrder.ResolveForCreate(requested, AutomationTaskActionType.InstallPackage);
        Assert.That(value, Is.EqualTo(requested));
    }

    [Test]
    public void ResolveForCreate_NullFallsBackToActionTypeDefault()
    {
        var value = AutomationTaskExecutionOrder.ResolveForCreate(null, AutomationTaskActionType.RemovePackage);
        Assert.That(value, Is.EqualTo(30));
    }

    [TestCase(-1)]
    [TestCase(100)]
    public void ResolveForCreate_RejectsOutOfRange(int requested)
    {
        Assert.Throws<System.InvalidOperationException>(() =>
            AutomationTaskExecutionOrder.ResolveForCreate(requested, AutomationTaskActionType.RunScript));
    }

    [Test]
    public void Range_IsZeroToNinetyNine()
    {
        Assert.That(AutomationTaskExecutionOrder.Min, Is.EqualTo(0));
        Assert.That(AutomationTaskExecutionOrder.Max, Is.EqualTo(99));
    }
}
