using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// As flags do force sync eram expostas na UI e descartadas no backend (o
/// payload só levava TaskIds). Estes testes fixam a regra: flag informada manda
/// (inclusive "tudo desmarcado"); sem nenhuma flag, mantém o default histórico.
/// </summary>
[TestFixture]
public class AutomationForceSyncFlagsTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    [Test]
    public void ExplicitFlags_AreRespected()
    {
        var flags = ForceAutomationSyncCommandHandler.ResolveForceSyncFlags(
            new ForceAutomationSyncCommand(AgentId, Policies: true, Inventory: false, Software: true, AppStore: false));

        Assert.That(flags.Policies, Is.True);
        Assert.That(flags.Inventory, Is.False);
        Assert.That(flags.Software, Is.True);
        Assert.That(flags.AppStore, Is.False);
    }

    [Test]
    public void AllFlagsOff_IsNoOp()
    {
        var flags = ForceAutomationSyncCommandHandler.ResolveForceSyncFlags(
            new ForceAutomationSyncCommand(AgentId, Policies: false, Inventory: false, Software: false, AppStore: false));

        Assert.That(flags.Policies, Is.False);
        Assert.That(flags.Inventory, Is.False);
        Assert.That(flags.Software, Is.False);
        Assert.That(flags.AppStore, Is.False);
    }

    [Test]
    public void NoFlags_LegacyDefaultIsPoliciesAndInventory()
    {
        var flags = ForceAutomationSyncCommandHandler.ResolveForceSyncFlags(
            new ForceAutomationSyncCommand(AgentId));

        Assert.That(flags.Policies, Is.True);
        Assert.That(flags.Inventory, Is.True);
        Assert.That(flags.Software, Is.False);
        Assert.That(flags.AppStore, Is.False);
    }

    [Test]
    public void OnlyOneFlagProvided_OtherFlagsStayOff()
    {
        var flags = ForceAutomationSyncCommandHandler.ResolveForceSyncFlags(
            new ForceAutomationSyncCommand(AgentId, AppStore: true));

        Assert.That(flags.Policies, Is.False);
        Assert.That(flags.Inventory, Is.False);
        Assert.That(flags.Software, Is.False);
        Assert.That(flags.AppStore, Is.True);
    }
}
