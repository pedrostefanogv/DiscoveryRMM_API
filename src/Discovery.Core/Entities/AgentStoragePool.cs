namespace Discovery.Core.Entities;

/// <summary>
/// Storage pool do Windows (Storage Spaces) ou volume equivalente reportado
/// junto ao inventário de Storage/RDS. Inclui o pool "Primordial", que indica
/// que o host tem discos ainda não agregados a um pool de usuário.
/// </summary>
public class AgentStoragePool
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Saúde reportada (ex.: "Healthy", "Warning", "Unhealthy").</summary>
    public string? HealthStatus { get; set; }

    /// <summary>Estado operacional bruto do provedor de storage.</summary>
    public string? OperationalStatus { get; set; }

    public long? SizeGb { get; set; }
    public long? AllocatedGb { get; set; }
    public int? DiskCount { get; set; }

    /// <summary>true para o pool primordial (não é um pool de usuário).</summary>
    public bool IsPrimordial { get; set; }

    public DateTime CollectedAt { get; set; }
}
