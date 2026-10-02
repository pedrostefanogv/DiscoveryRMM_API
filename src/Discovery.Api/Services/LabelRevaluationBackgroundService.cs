using System.Collections.Concurrent;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Discovery.Api.Services;

/// <summary>
/// Executa a reavaliacao de labels agendada pelo sync de inventario, em ESCOPO PROPRIO,
/// com debounce e coalescencia por agente.
///
/// <para>
/// Desabilitado por padrao: a reconciliacao periodica incremental ja cobre o caso, e
/// ligar o gatilho deve ser uma decisao explicita (custo x latencia). Quando ligado,
/// o flush roda a cada <c>AgentLabeling:InventoryDebounceSeconds</c> e avalia apenas
/// agentes que sincronizaram nesse intervalo.
/// </para>
/// </summary>
public sealed class LabelRevaluationBackgroundService : BackgroundService, ILabelRevaluationQueue
{
    public const string EnabledConfigKey = "AgentLabeling:TriggerOnInventorySync";
    public const string DebounceConfigKey = "AgentLabeling:InventoryDebounceSeconds";

    private const int FlushBatchSize = 500;

    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LabelRevaluationBackgroundService> _logger;

    public LabelRevaluationBackgroundService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<LabelRevaluationBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;

        IsEnabled = configuration.GetValue(EnabledConfigKey, false);
        var seconds = Math.Clamp(configuration.GetValue(DebounceConfigKey, 15), 5, 300);
        DebounceInterval = TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Se o gatilho esta habilitado (configuracao).</summary>
    public bool IsEnabled { get; }

    /// <summary>Intervalo de debounce efetivo.</summary>
    public TimeSpan DebounceInterval { get; }

    /// <summary>Quantos agentes aguardam o proximo flush (diagnostico/testes).</summary>
    public int PendingCount => _pending.Count;

    public void Schedule(Guid agentId)
    {
        if (!IsEnabled || agentId == Guid.Empty)
            return;

        // Coalescencia: varios syncs do mesmo agente no intervalo debounce contam como um.
        _pending[agentId] = 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug(
                "Reavaliacao de labels por sync de inventario desabilitada ({Key}=false).", EnabledConfigKey);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DebounceInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var agentIds = Drain();
            if (agentIds.Count == 0)
                continue;

            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IAgentAutoLabelingService>();

                if (!await service.HasEnabledRulesAsync(stoppingToken))
                    continue;

                foreach (var chunk in agentIds.Chunk(FlushBatchSize))
                    await service.EvaluateAgentsAsync(chunk, "inventory-sync", "system", stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Best-effort: a reconciliacao periodica tambem corrige o estado.
                _logger.LogWarning(ex, "Falha ao reavaliar labels apos sincronizacao de inventario.");
            }
        }
    }

    private List<Guid> Drain()
    {
        var ids = _pending.Keys.ToList();
        foreach (var id in ids)
            _pending.TryRemove(id, out _);

        return ids;
    }
}
