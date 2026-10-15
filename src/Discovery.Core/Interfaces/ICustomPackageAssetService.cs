using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Upload/download de assets (instalador e ícone/capa) de pacotes custom da Loja
/// de Softwares no object storage S3-compatível.
/// </summary>
public interface ICustomPackageAssetService
{
    /// <summary>
    /// Envia um asset para o storage.
    /// </summary>
    /// <param name="kind">"installer" ou "icon".</param>
    /// <param name="fileName">Nome original do arquivo.</param>
    /// <param name="contentType">Tipo MIME.</param>
    /// <param name="content">Stream do arquivo.</param>
    Task<CustomPackageAssetDto> UploadAsync(
        string kind,
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Baixa um asset do storage pela object key.
    /// </summary>
    Task<CustomPackageAssetContent> DownloadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copia o asset do storage para o stream de destino sem bufferizar o
    /// arquivo inteiro em memória. Obrigatório para instaladores grandes
    /// (o DownloadAsync carrega tudo em MemoryStream e derruba a API por OOM).
    /// </summary>
    Task WriteToAsync(
        string objectKey,
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>Remove um asset do storage pela object key.</summary>
    Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    // ── Upload multipart DIRETO no storage (instaladores grandes) ────────────
    //
    // O arquivo NÃO passa pela API: ela cria o multipart, pré-assina cada bloco
    // de 50 MiB e o navegador faz PUT direto no RustFS/S3. Vantagens: passa pelo
    // Cloudflare (blocos pequenos), retomável (o storage guarda as partes até o
    // complete) e sem custo de memória/disco na API.

    // Doubles de teste que só exercitam o caminho antigo herdam o padrão abaixo.

    /// <summary>Cria o multipart no storage e devolve a sessão de upload.</summary>
    Task<MultipartUploadSessionDto> BeginMultipartUploadAsync(
        string kind,
        string fileName,
        string contentType,
        long sizeBytes,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>URL pré-assinada de uma parte (PUT direto no storage).</summary>
    Task<MultipartUploadPartUrlDto> GetUploadPartUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>Partes já recebidas pelo storage — permite retomar/verificar.</summary>
    Task<IReadOnlyList<MultipartUploadPartStateDto>> ListUploadedPartsAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>
    /// Finaliza o multipart com os ETags recebidos e devolve o asset pronto
    /// (mesmo formato do upload único), com SHA-256 calculado pela API.
    /// </summary>
    Task<CustomPackageAssetDto> CompleteMultipartUploadAsync(
        string objectKey,
        string kind,
        string fileName,
        string contentType,
        long sizeBytes,
        string uploadId,
        IReadOnlyList<MultipartUploadPartStateDto> parts,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>Aborta o multipart (o storage descarta as partes enviadas).</summary>
    Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    private const string MultipartNotSupportedMessage =
        "Upload multipart direto no storage nao esta disponivel neste provider.";
}

/// <summary>Conteúdo de um asset baixado do storage.</summary>
public sealed class CustomPackageAssetContent
{
    public Stream Content { get; init; } = Stream.Null;
    public string ContentType { get; init; } = "application/octet-stream";
    public string FileName { get; init; } = string.Empty;
}
