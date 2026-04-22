namespace Discovery.Core.ValueObjects;

/// <summary>
/// Resultado de preparação de upload direto em Object Storage.
/// </summary>
public class PresignedUploadRequest
{
    public Guid AttachmentId { get; set; }

    public string ObjectKey { get; set; } = string.Empty;

    public string UploadUrl { get; set; } = string.Empty;

    public string HttpMethod { get; set; } = "PUT";

    public DateTime ExpiresAtUtc { get; set; }
}
