using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Despacha um AgentAlertDefinition para os agentes do escopo configurado.
/// </summary>
public interface IAlertDispatchService
{
    /// <summary>
    /// Despacha o alerta para todos os agents do escopo configurado.
    /// </summary>
    Task DispatchAsync(AgentAlertDefinition alert, CancellationToken cancellationToken = default);
}
