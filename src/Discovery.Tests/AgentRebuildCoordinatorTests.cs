using System.Diagnostics;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Cobre o contrato que resolveu o incidente de update "agarrado" (2026-11-27):
/// o rebuild roda em background, disparo concorrente adere ao build em andamento,
/// e desconectar a espera (request HTTP) NÃO cancela o build — só o shutdown da
/// aplicação cancela.
/// </summary>
public class AgentRebuildCoordinatorTests
{
    private sealed class FakeRebuildExecutor : IAgentRebuildExecutor
    {
        private readonly TaskCompletionSource<AgentBuildDto> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;
        public CancellationToken LastToken;

        public Task<AgentBuildDto> ExecuteAsync(string actor, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastToken = cancellationToken;
            return _tcs.Task.WaitAsync(cancellationToken);
        }

        public void Complete(AgentBuildDto build) => _tcs.TrySetResult(build);
        public void Fail(Exception ex) => _tcs.TrySetException(ex);
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
        public void TriggerStopping() => _stopping.Cancel();
    }

    private static AgentBuildDto Build() => new(
        Guid.NewGuid(), "1.3.0", "windows", "amd64",
        "discovery-agent-install.exe", "sha256-fake", DateTime.UtcNow, null);

    private static AgentRebuildCoordinator CreateCoordinator(
        IAgentRebuildExecutor executor, IHostApplicationLifetime? lifetime = null)
        => new(executor, lifetime ?? new FakeApplicationLifetime(), NullLogger<AgentRebuildCoordinator>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail("Condição não satisfeita no tempo esperado.");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task TryStart_ConcurrentTrigger_JoinsRunningBuildInsteadOfRestarting()
    {
        var executor = new FakeRebuildExecutor();
        var coordinator = CreateCoordinator(executor);

        var (firstStarted, first) = coordinator.TryStart("localhost-script");
        Assert.That(firstStarted, Is.True);
        Assert.That(first.State, Is.EqualTo(AgentRebuildState.Running));

        // A retentativa do script de update não pode reiniciar o build.
        var (secondStarted, second) = coordinator.TryStart("localhost-script");
        Assert.That(secondStarted, Is.False, "disparo concorrente deve aderir ao build em andamento");
        Assert.That(second.RunId, Is.EqualTo(first.RunId));
        Assert.That(second.State, Is.EqualTo(AgentRebuildState.Running));

        executor.Complete(Build());

        var final = await coordinator.WaitForCompletionAsync(first.RunId!.Value, CancellationToken.None);
        Assert.That(final.State, Is.EqualTo(AgentRebuildState.Succeeded));
        Assert.That(final.Build, Is.Not.Null);
        Assert.That(executor.Calls, Is.EqualTo(1), "o executor só pode rodar uma vez");

        // Depois de concluído, um novo rebuild pode ser iniciado.
        var (again, _) = coordinator.TryStart("localhost-script");
        Assert.That(again, Is.True, "estado não pode ficar preso em Running");
    }

    [Test]
    public async Task CancellingTheWait_DoesNotCancelTheBuild()
    {
        var executor = new FakeRebuildExecutor();
        var coordinator = CreateCoordinator(executor);

        var (started, snapshot) = coordinator.TryStart("localhost-script");
        Assert.That(started, Is.True);
        await WaitUntilAsync(() => executor.Calls == 1);

        using var waitCts = new CancellationTokenSource();
        var waitTask = coordinator.WaitForCompletionAsync(snapshot.RunId!.Value, waitCts.Token);
        await Task.Delay(30); // garante que a espera entrou no loop
        waitCts.Cancel();

        Assert.That(async () => await waitTask, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(coordinator.GetStatus().IsRunning, Is.True, "cancelar a espera não pode encerrar o build");
        Assert.That(executor.LastToken.IsCancellationRequested, Is.False, "o token do build não é o da espera");

        executor.Complete(Build());
        var final = await coordinator.WaitForCompletionAsync(snapshot.RunId!.Value, CancellationToken.None);
        Assert.That(final.State, Is.EqualTo(AgentRebuildState.Succeeded), "o build sobrevive à desconexão do cliente");
    }

    [Test]
    public async Task Failure_IsExposedInStatus()
    {
        var executor = new FakeRebuildExecutor();
        var coordinator = CreateCoordinator(executor);

        var (started, snapshot) = coordinator.TryStart("localhost-script");
        Assert.That(started, Is.True);
        await WaitUntilAsync(() => executor.Calls == 1);

        executor.Fail(new InvalidOperationException("build quebrou"));

        var final = await coordinator.WaitForCompletionAsync(snapshot.RunId!.Value, CancellationToken.None);
        Assert.That(final.State, Is.EqualTo(AgentRebuildState.Failed));
        Assert.That(final.Error, Does.Contain("build quebrou"));
        Assert.That(final.IsRunning, Is.False);
    }

    [Test]
    public async Task ApplicationStopping_CancelsTheRunningBuild()
    {
        var lifetime = new FakeApplicationLifetime();
        var executor = new FakeRebuildExecutor();
        var coordinator = CreateCoordinator(executor, lifetime);

        var (started, snapshot) = coordinator.TryStart("localhost-script");
        Assert.That(started, Is.True);
        await WaitUntilAsync(() => executor.Calls == 1);

        lifetime.TriggerStopping();

        var final = await coordinator.WaitForCompletionAsync(snapshot.RunId!.Value, CancellationToken.None);
        Assert.That(final.State, Is.EqualTo(AgentRebuildState.Failed));
        Assert.That(final.Error, Does.Contain("cancelado").IgnoreCase);
    }

    [Test]
    public void GetStatus_BeforeAnyRun_IsIdle()
    {
        var coordinator = CreateCoordinator(new FakeRebuildExecutor());

        var status = coordinator.GetStatus();
        Assert.That(status.State, Is.EqualTo(AgentRebuildState.Idle));
        Assert.That(status.RunId, Is.Null);
        Assert.That(status.IsRunning, Is.False);
        Assert.That(coordinator.IsRunning, Is.False);
    }
}
