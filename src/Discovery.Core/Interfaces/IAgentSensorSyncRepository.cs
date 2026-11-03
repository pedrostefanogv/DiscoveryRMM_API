namespace Discovery.Core.Interfaces;

/// <summary>
/// Escrita do resultado do sensor-sync (fingerprint + quando) no agent.
/// Interface SEPARADA de IAgentRepository de propósito: o sync de sensores é um
/// endpoint próprio e não deve forçar todo fake de teste do repositório de
/// agentes a implementar mais um membro.
/// </summary>
public interface IAgentSensorSyncRepository
{
    Task SetSensorSyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default);
}
