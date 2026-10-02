using Discovery.Api.Controllers;
using Quartz;

namespace Discovery.Tests;

/// <summary>
/// O JobKey do Quartz carrega o sufixo "-trigger" (identidade derivada do
/// trigger em ScheduleJob&lt;T&gt;), enquanto a configuração de processamento
/// em segundo plano expõe o nome base. O endpoint de trigger precisa resolver
/// as duas formas — senão o botão "Rodar agora" devolve 404.
/// </summary>
public class JobsControllerJobKeyTests
{
    private static Func<JobKey, CancellationToken, Task<bool>> Existing(params string[] keys)
    {
        var set = new HashSet<string>(keys, StringComparer.Ordinal);
        return (key, _) => Task.FromResult(set.Contains($"{key.Group}.{key.Name}"));
    }

    [Test]
    public async Task ResolvesBaseName_WhenJobKeyUsesBaseName()
    {
        var key = await JobsController.ResolveJobKeyAsync(
            "tickets", "technician-metrics-refresh",
            Existing("tickets.technician-metrics-refresh"), CancellationToken.None);

        Assert.That(key, Is.Not.Null);
        Assert.That(key!.Name, Is.EqualTo("technician-metrics-refresh"));
        Assert.That(key.Group, Is.EqualTo("tickets"));
    }

    [Test]
    public async Task ResolvesSuffixedKey_WhenOnlyTriggerIdentityExists()
    {
        var key = await JobsController.ResolveJobKeyAsync(
            "tickets", "technician-metrics-refresh",
            Existing("tickets.technician-metrics-refresh-trigger"), CancellationToken.None);

        Assert.That(key, Is.Not.Null);
        Assert.That(key!.Name, Is.EqualTo("technician-metrics-refresh-trigger"));
        Assert.That(key.Group, Is.EqualTo("tickets"));
    }

    [Test]
    public async Task KeepsSuffixedName_WhenCalledWithActualJobKey()
    {
        var key = await JobsController.ResolveJobKeyAsync(
            "tickets", "ai-ticket-assignment-trigger",
            Existing("tickets.ai-ticket-assignment-trigger"), CancellationToken.None);

        Assert.That(key, Is.Not.Null);
        Assert.That(key!.Name, Is.EqualTo("ai-ticket-assignment-trigger"));
    }

    [Test]
    public async Task ReturnsNull_WhenJobDoesNotExistInGroup()
    {
        var key = await JobsController.ResolveJobKeyAsync(
            "tickets", "does-not-exist",
            Existing("tickets.technician-metrics-refresh-trigger"), CancellationToken.None);

        Assert.That(key, Is.Null);
    }

    [Test]
    public async Task DoesNotFallBackToAnotherGroup()
    {
        var key = await JobsController.ResolveJobKeyAsync(
            "tickets", "technician-metrics-refresh",
            Existing("maintenance.technician-metrics-refresh-trigger"), CancellationToken.None);

        Assert.That(key, Is.Null);
    }
}
