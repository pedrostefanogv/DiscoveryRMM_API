namespace Discovery.Core.Entities;

/// <summary>
/// Presença de artifact por agente. Upsert-only por (ArtifactId, AgentId).
/// Expirado via job periódico (TTL 2h configurável).
/// ArtifactId referencia AppPackage.Id (origem: Winget/Chocolatey/Custom).
/// </summary>
public class P2pArtifactPresence
{
    /// <summary>ID do AppPackage correspondente ao artifact.</summary>
    public Guid ArtifactId { get; set; }

    public Guid AgentId { get; set; }
    public Guid SiteId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>Nome legível do arquivo. Máx 260 chars. Sem path separators.</summary>
    public string? ArtifactName { get; set; }

    /// <summary>True quando ArtifactId é sintético (prefixo "name:").</summary>
    public bool IdIsSynthetic { get; set; }

    /// <summary>Último momento em que algum agente reportou ter este artifact.</summary>
    public DateTime LastSeenAt { get; set; }
}
