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
}

/// <summary>Conteúdo de um asset baixado do storage.</summary>
public sealed class CustomPackageAssetContent
{
    public Stream Content { get; init; } = Stream.Null;
    public string ContentType { get; init; } = "application/octet-stream";
    public string FileName { get; init; } = string.Empty;
}
