namespace Meduza.Core.Entities;

/// <summary>
/// Presença de artifact por agente. Upsert-only por (ArtifactId, AgentId).
/// Expirado via job periódico (TTL 2h configurável).
/// </summary>
public class P2pArtifactPresence
{
    /// <summary>ID canônico do artifact (ex: "name:myapp-1.2.3.exe"). Máx 512 chars.</summary>
    public string ArtifactId { get; set; } = string.Empty;

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
