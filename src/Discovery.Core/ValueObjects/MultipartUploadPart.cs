namespace Discovery.Core.ValueObjects;

/// <summary>
/// Parte de um upload MULTIPART S3/RustFS: número da parte + ETag devolvido pelo
/// storage ao receber a parte (necessário para finalizar/retomar o upload).
/// </summary>
public sealed record MultipartUploadPart(int PartNumber, string ETag, long SizeBytes = 0);
