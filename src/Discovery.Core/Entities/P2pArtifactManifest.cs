namespace Discovery.Core.Entities;

/// <summary>
/// Manifesto canônico de chunks P2P para um artifact.
/// PK = ArtifactId (FK para WingetPackage.Id ou AppPackage.Id).
/// Upsert-only: se sha256 mudar ou generatedAt for mais novo, sobrescreve.
/// </summary>
public class P2pArtifactManifest
{
    /// <summary>Guid do WingetPackage.Id ou AppPackage.Id (PK)</summary>
    public Guid ArtifactId { get; set; }

    /// <summary>ID do cliente para escopo de consulta</summary>
    public Guid ClientId { get; set; }

    /// <summary>P2PChunkManifest completo serializado como JSON</summary>
    public string ManifestJson { get; set; } = string.Empty;

    /// <summary>SHA-256 final do arquivo</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Tamanho total do arquivo em bytes</summary>
    public long TotalSize { get; set; }

    /// <summary>Tamanho de cada chunk em bytes</summary>
    public int ChunkSize { get; set; }

    /// <summary>Número total de chunks</summary>
    public int TotalChunks { get; set; }

    /// <summary>AgentId que gerou o manifesto</summary>
    public Guid GeneratedBy { get; set; }

    /// <summary>Timestamp de geração no agent</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>Timestamp de upsert no servidor</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
