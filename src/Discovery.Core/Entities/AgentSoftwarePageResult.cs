namespace Discovery.Core.Entities;

/// <summary>
/// Resultado de paginação por offset para o inventário de software de um agente.
/// </summary>
public class AgentSoftwarePageResult
{
    public IReadOnlyList<AgentInstalledSoftware> Items { get; set; } = [];
    public int TotalCount { get; set; }
}
