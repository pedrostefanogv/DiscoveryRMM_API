using Discovery.Core.Cqrs.AgentUpdates.Commands;
using Discovery.Core.Cqrs.AgentUpdates.Queries;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Estado de uma execução de rebuild do agente.
/// </summary>
public enum AgentRebuildState
{
    Idle = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>
/// Fotografia imutável do rebuild atual/último. Consumida pelo endpoint de status
/// e pela espera opcional do endpoint síncrono.
/// </summary>
public sealed record AgentRebuildSnapshot(
    Guid? RunId,
    AgentRebuildState State,
    string? Actor,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    AgentBuildDto? Build)
{
    public bool IsRunning => State == AgentRebuildState.Running;

    /// <summary>Segundos decorridos (até o fim, ou até agora se ainda rodando).</summary>
    public double? ElapsedSeconds => StartedAt is null
        ? null
        : Math.Round(((FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt.Value).TotalSeconds, 1, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Executa o rebuild propriamente dito. Seam para teste: o coordinator cuida do
/// ciclo de vida/estado, o executor do trabalho pesado.
/// </summary>
public interface IAgentRebuildExecutor
{
    Task<AgentBuildDto> ExecuteAsync(string actor, CancellationToken cancellationToken);
}

/// <summary>
/// Executor padrão: resolve o IMediator num scope próprio e usa o token do
/// coordinator (nunca o CancellationToken do request HTTP, que morre quando o
/// cliente desconecta).
/// </summary>
public sealed class MediatorAgentRebuildExecutor(IServiceProvider serviceProvider) : IAgentRebuildExecutor
{
    public async Task<AgentBuildDto> ExecuteAsync(string actor, CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new RebuildAgentCommand(actor), cancellationToken);
        return result.Match(
            build => build,
            errors => throw new InvalidOperationException(
                string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"))));
    }
}

/// <summary>
/// Orquestra o rebuild do agente em background e guarda o estado por processo.
///
/// Motivação (incidente 2026-11-27): o rebuild era síncrono no request HTTP e o
/// script de update usava --max-time 300 com 5 retentativas. Quando o build
/// passava de 300s (disco saturado pelo git gc do sync do winget), o
/// cancelamento do request matava a árvore do build (Kill(entireProcessTree)) e
/// a retentativa recomeçava do zero — o update ficava "agarrado" por ~25 min e
/// terminava sem publicar o build.
///
/// Contrato:
/// - no máximo 1 rebuild simultâneo por processo;
/// - disparo concorrente adere ao build em andamento em vez de reiniciá-lo;
/// - o build só é cancelado no shutdown da aplicação, nunca porque o cliente
///   HTTP que disparou desconectou.
/// </summary>
public sealed class AgentRebuildCoordinator
{
    /// <summary>Intervalo de polling da espera in-process (não é I/O; é leitura de estado).</summary>
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly IAgentRebuildExecutor _executor;
    private readonly ILogger<AgentRebuildCoordinator> _logger;

    private AgentRebuildSnapshot _current = new(null, AgentRebuildState.Idle, null, null, null, null, null);
    private CancellationTokenSource? _runCts;

    public AgentRebuildCoordinator(
        IAgentRebuildExecutor executor,
        IHostApplicationLifetime applicationLifetime,
        ILogger<AgentRebuildCoordinator> logger)
    {
        _executor = executor;
        _logger = logger;

        // Shutdown da aplicação é a ÚNICA razão legítima para cancelar o build.
        applicationLifetime.ApplicationStopping.Register(() =>
        {
            CancellationTokenSource? cts;
            lock (_sync)
                cts = _runCts;

            if (cts is not null && !cts.IsCancellationRequested)
            {
                _logger.LogWarning("Shutdown da aplicacao: cancelando o rebuild do agente em andamento.");
                try { cts.Cancel(); } catch { /* já finalizado */ }
            }
        });
    }

    public AgentRebuildSnapshot GetStatus()
    {
        lock (_sync)
            return _current;
    }

    public bool IsRunning
    {
        get { lock (_sync) return _current.IsRunning; }
    }

    /// <summary>
    /// Inicia um rebuild se não houver nenhum em andamento. Quando já existe um,
    /// devolve o runId corrente com Started=false — idempotente de propósito:
    /// a retentativa/re-execução do script não reinicia o build.
    /// </summary>
    public (bool Started, AgentRebuildSnapshot Status) TryStart(string actor)
    {
        CancellationTokenSource cts;
        Guid runId;

        lock (_sync)
        {
            if (_current.IsRunning)
                return (false, _current);

            runId = Guid.NewGuid();
            cts = new CancellationTokenSource();
            _runCts = cts;
            _current = new AgentRebuildSnapshot(runId, AgentRebuildState.Running, actor, DateTimeOffset.UtcNow, null, null, null);
        }

        _logger.LogInformation("Agent rebuild enfileirado em background. RunId={RunId}, Actor={Actor}", runId, actor);
        _ = Task.Run(() => RunAsync(runId, actor, cts));
        return (true, GetStatus());
    }

    /// <summary>
    /// Aguarda a conclusão de um run específico. O token cancela apenas a ESPERA
    /// (ex.: request HTTP desconectou), nunca o build em background.
    /// </summary>
    public async Task<AgentRebuildSnapshot> WaitForCompletionAsync(Guid runId, CancellationToken waitToken)
    {
        while (true)
        {
            var snapshot = GetStatus();
            if (snapshot.RunId != runId || !snapshot.IsRunning)
                return snapshot;

            await Task.Delay(WaitPollInterval, waitToken);
        }
    }

    private async Task RunAsync(Guid runId, string actor, CancellationTokenSource cts)
    {
        try
        {
            var build = await _executor.ExecuteAsync(actor, cts.Token);
            Complete(runId, AgentRebuildState.Succeeded, build, null);

            _logger.LogInformation(
                "Agent rebuild concluido. RunId={RunId}, BuildId={BuildId}, Version={Version}, File={FileName}",
                runId, build.Id, build.Version, build.FileName);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Complete(runId, AgentRebuildState.Failed, null, "Rebuild cancelado (shutdown da aplicacao).");
            _logger.LogWarning("Agent rebuild cancelado. RunId={RunId}", runId);
        }
        catch (Exception ex)
        {
            Complete(runId, AgentRebuildState.Failed, null, ex.Message);
            _logger.LogError(ex, "Agent rebuild falhou. RunId={RunId}", runId);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_runCts, cts))
                    _runCts = null;
            }

            cts.Dispose();
        }
    }

    private void Complete(Guid runId, AgentRebuildState state, AgentBuildDto? build, string? error)
    {
        lock (_sync)
        {
            // Conclusão de um run que já não é o corrente não sobrescreve o estado.
            if (_current.RunId != runId)
                return;

            _current = _current with
            {
                State = state,
                FinishedAt = DateTimeOffset.UtcNow,
                Error = error,
                Build = build,
            };
        }
    }
}
