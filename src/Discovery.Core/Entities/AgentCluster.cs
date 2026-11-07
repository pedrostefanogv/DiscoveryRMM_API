namespace Discovery.Core.Entities;

/// <summary>
/// Cluster de failover detectado neste servidor (Windows Server Failover
/// Clustering). Cada linha é um cluster; nós e recursos vão serializados em JSON
/// porque são listas aninhadas de vida curta, sem consulta relacional própria.
/// </summary>
public class AgentCluster
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Nós do cluster: [{ name, state, vote }].</summary>
    public string? NodesJson { get; set; }

    /// <summary>Recursos do cluster: [{ name, state, group, type }].</summary>
    public string? ResourcesJson { get; set; }

    public string? QuorumType { get; set; }
    public string? QuorumResource { get; set; }

    /// <summary>Quantidade de recursos em estado degradado/offline.</summary>
    public int? DegradedResources { get; set; }

    public DateTime CollectedAt { get; set; }
}
