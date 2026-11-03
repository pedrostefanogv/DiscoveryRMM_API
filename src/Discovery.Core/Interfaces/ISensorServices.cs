using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>CRUD + resolução de escopo das definições de sensor.</summary>
public interface ISensorDefinitionService
{
    Task<IReadOnlyList<SensorDefinitionDto>> ListAsync(
        AppApprovalScopeType? scopeType,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<SensorDefinitionDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SensorDefinitionDto> CreateAsync(SensorUpsertRequest request, string? actor, CancellationToken cancellationToken = default);

    Task<SensorDefinitionDto?> UpdateAsync(Guid id, SensorUpsertRequest request, string? actor, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, string? actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sensores aplicáveis ao agente (Global + Cliente + Site + Agente, filtro por
    /// tags, sem scripts inativos). Ponto único usado pelo sync e pela leitura.
    /// </summary>
    Task<IReadOnlyList<SensorDefinition>> ResolveApplicableAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>Sync de configuração de sensores com o agente (fingerprint próprio).</summary>
public interface ISensorSyncService
{
    Task<AgentSensorSyncResponse> SyncAsync(Guid agentId, AgentSensorSyncRequest request, CancellationToken cancellationToken = default);

    /// <summary>Fingerprint vigente (mesma chave usada no sync).</summary>
    Task<string> GetFingerprintAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>Intervalo default resolvido por site > cliente > servidor.</summary>
    Task<int> GetDefaultIntervalSecondsAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>Ingestão de amostras: revalida, grava estado/histórico e decide alertas.</summary>
public interface ISensorIngestionService
{
    Task<AgentSensorResultResponse> IngestAsync(AgentSensorResultBatch batch, CancellationToken cancellationToken = default);
}

/// <summary>Leitura agregada para a aba "Sensores" do detalhe do agente.</summary>
public interface ISensorReadService
{
    Task<AgentSensorsPayloadDto?> GetForAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>Agregados da frota (contadores por status/tipo + agentes com mais falhas).</summary>
    Task<SensorFleetSummaryDto> GetFleetSummaryAsync(Guid? clientId, Guid? siteId, CancellationToken cancellationToken = default);
}

/// <summary>Séries temporais (gráficos) e timeline de eventos de um sensor.</summary>
public interface ISensorSeriesService
{
    Task<SensorSeriesDto?> GetSeriesAsync(
        Guid agentId,
        Guid sensorId,
        string? targetKey,
        DateTime from,
        DateTime to,
        IReadOnlyList<string>? channels,
        CancellationToken cancellationToken = default);

    Task<SensorEventPageDto> GetEventsAsync(
        Guid agentId,
        Guid sensorId,
        string? targetKey,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Agregação horária das amostras (retenção longa dos gráficos).</summary>
public interface ISensorRollupService
{
    /// <summary>Recalcula a hora informada. Idempotente (upsert).</summary>
    Task<int> RollupAsync(DateTime utcHourStart, CancellationToken cancellationToken = default);
}
