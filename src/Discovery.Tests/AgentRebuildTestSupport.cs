using Discovery.Core.Cqrs.AgentUpdates.Queries;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Apoio comum para construir um <see cref="AgentRebuildCoordinator"/> em testes
/// que só precisam satisfazer a dependência do controller (sem exercitar o rebuild).
/// </summary>
internal static class AgentRebuildTestSupport
{
    public static AgentRebuildCoordinator CreateCoordinator() =>
        new(new NoopRebuildExecutor(), new NoopApplicationLifetime(), NullLogger<AgentRebuildCoordinator>.Instance);

    private sealed class NoopRebuildExecutor : IAgentRebuildExecutor
    {
        public Task<AgentBuildDto> ExecuteAsync(string actor, CancellationToken cancellationToken)
            => throw new NotSupportedException("Rebuild não deve ser executado neste teste.");
    }

    private sealed class NoopApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
