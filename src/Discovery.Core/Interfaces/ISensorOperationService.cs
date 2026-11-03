using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>Operações imperativas sobre sensores de um agente (executar agora, resetar, forçar sync).</summary>
public interface ISensorOperationService
{
    /// <summary>Pede ao agente para executar agora (lista vazia = todos).</summary>
    Task<SensorRunResponseDto> RunAsync(Guid agentId, IReadOnlyList<Guid>? sensorIds, CancellationToken cancellationToken = default);

    /// <summary>Reseta o estado (Pending) de um sensor ou de todos os do agente.</summary>
    Task<int> ResetAsync(Guid agentId, Guid? sensorId, CancellationToken cancellationToken = default);

    /// <summary>Pede ao agente um sensor-sync imediato.</summary>
    Task<SensorRunResponseDto> SyncNowAsync(Guid agentId, CancellationToken cancellationToken = default);
}
