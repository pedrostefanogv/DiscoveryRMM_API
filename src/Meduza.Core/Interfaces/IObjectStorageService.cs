using Meduza.Core.ValueObjects;

namespace Meduza.Core.Interfaces;

/// <summary>
/// Serviço genérico de Object Storage S3-compatível.
/// Abstrai o fornecedor específico (AWS S3, Cloudflare R2, MinIO, etc.)
/// e fornece operações básicas de upload, download e presigned URLs.
/// 
/// Uso em múltiplos contextos: relatórios, tickets, notas, conhecimento, etc.
/// </summary>
public interface IObjectStorageService
{
    /// <summary>
    /// Fazer upload de um objeto no storage.
    /// </summary>
    /// <param name="objectKey">Chave única do objeto (ex: clients/{clientId}/reports/{reportId}/{filename})</param>
    /// <param name="content">Stream com conteúdo do arquivo</param>
    /// <param name="contentType">Tipo MIME (ex: application/pdf, text/csv)</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>StorageObject com metadados do upload (key, bucket, size, checksum)</returns>
    Task<StorageObject> UploadAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fazer download de um objeto como stream.
    /// </summary>
    /// <param name="objectKey">Chave do objeto</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>Stream com conteúdo do objeto</returns>
    Task<Stream> DownloadAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verificar se um objeto existe no storage.
    /// </summary>
    Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletar um objeto do storage.
    /// </summary>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletar todos os objetos com um prefixo específico.
    /// Útil para limpeza em massa (ex: arquivos expirados por prefixo de tipo).
    /// </summary>
    /// <param name="prefix">Prefixo dos objetos (ex: clients/{clientId}/reports/)</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    Task DeleteByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gerar URL pré-assinada privada para download.
    /// URL é válida por um tempo limitado (ex: 24 horas) e não requer autenticação adicional.
    /// </summary>
    /// <param name="objectKey">Chave do objeto</param>
    /// <param name="ttlHours">Tempo de validade em horas</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>URL pré-assinada privada</returns>
    Task<string> GetPresignedDownloadUrlAsync(
        string objectKey,
        int ttlHours,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gerar URL pré-assinada privada para upload direto no storage.
    /// </summary>
    /// <param name="objectKey">Chave do objeto</param>
    /// <param name="ttlMinutes">Tempo de validade em minutos</param>
    /// <param name="contentType">Tipo MIME esperado no upload</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    /// <returns>URL pré-assinada para upload</returns>
    Task<string> GetPresignedUploadUrlAsync(
        string objectKey,
        int ttlMinutes,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Obter metadados de um objeto sem fazer download do conteúdo.
    /// </summary>
    /// <param name="objectKey">Chave do objeto</param>
    /// <param name="cancellationToken">Token de cancelamento</param>
    Task<StorageObject?> GetMetadataAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Testar conectividade com o storage e verificar acesso ao bucket.
    /// Não lança exceção — retorna resultado estruturado mesmo em caso de falha.
    /// </summary>
    Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);
}
