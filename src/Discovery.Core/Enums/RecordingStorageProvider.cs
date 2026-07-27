namespace Discovery.Core.Enums;

/// <summary>
/// Provedor de storage para gravação de sessões remotas.
/// </summary>
public enum RecordingStorageProvider
{
    Local = 0,
    S3 = 1,
    AzureBlob = 2
}
