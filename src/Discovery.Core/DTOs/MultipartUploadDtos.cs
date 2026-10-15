namespace Discovery.Core.DTOs;

/// <summary>
/// Sessão de upload MULTIPART direto no storage (RustFS/MinIO/S3): o cliente
/// envia cada bloco (50 MiB) direto para uma URL pré-assinada, sem passar o
/// arquivo pela API — é o único caminho que atravessa o Cloudflare sem 413 e
/// que retoma de onde parou.
/// </summary>
public sealed record MultipartUploadSessionDto(
    string UploadId,
    string ObjectKey,
    string Bucket,
    string Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    long PartSizeBytes,
    int PartUrlTtlMinutes);

/// <summary>URL pré-assinada de UMA parte do multipart upload.</summary>
public sealed record MultipartUploadPartUrlDto(
    int PartNumber,
    string Url,
    int ExpiresInSeconds);

/// <summary>Parte já recebida pelo storage (usada para RETOMAR o envio).</summary>
public sealed record MultipartUploadPartStateDto(
    int PartNumber,
    string ETag,
    long SizeBytes);
