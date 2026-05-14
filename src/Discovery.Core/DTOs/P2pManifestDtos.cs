namespace Discovery.Core.DTOs;

/// <summary>
/// Request de publicação de manifesto P2P.
/// </summary>
public class P2pManifestRequest
{
    public Guid ArtifactId { get; set; }
    public string ArtifactName { get; set; } = string.Empty;

    /// <summary>Tamanho de cada chunk em bytes (ex: 8.388.608 = 8 MiB)</summary>
    public int ChunkSizeBytes { get; set; }

    /// <summary>Tamanho total do arquivo em bytes</summary>
    public long TotalSize { get; set; }

    /// <summary>SHA-256 final do arquivo</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Lista de chunks que compõem o artifact</summary>
    public List<P2pManifestChunkDto> Chunks { get; set; } = new();
}

public class P2pManifestChunkDto
{
    public int Index { get; set; }
    public long Offset { get; set; }
    public int Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// Response do manifesto P2P (GET).
/// </summary>
public class P2pManifestResponse
{
    public Guid ArtifactId { get; set; }
    public string ArtifactName { get; set; } = string.Empty;
    public P2pManifestDataDto? Manifest { get; set; }
    public string GeneratedAtUtc { get; set; } = string.Empty;
}

public class P2pManifestDataDto
{
    public int ChunkSize { get; set; }
    public long TotalSize { get; set; }
    public int TotalChunks { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public List<P2pManifestChunkDto> Chunks { get; set; } = new();
}
