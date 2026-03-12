namespace Meduza.Core.Enums;

/// <summary>
/// Tipo de provedor de armazenamento de objetos.
/// </summary>
public enum ObjectStorageProviderType
{
    /// <summary>Armazenamento em disco local (apenas para desenvolvimento)</summary>
    Local = 0,

    /// <summary>Cloudflare R2 (S3-compatível)</summary>
    CloudflareR2 = 2,

    /// <summary>Oracle S3-compatível</summary>
    OracleS3 = 3,

    /// <summary>MinIO (S3-compatível)</summary>
    MinIO = 4,

    /// <summary>Outro provedor S3-compatível</summary>
    S3Compatible = 5
}
