using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

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
    /// Copia o conteúdo do objeto para o stream de destino SEM bufferizar o
    /// objeto inteiro em memória. Use para arquivos grandes (ex.: instaladores
    /// de centenas de MB/GB): o DownloadAsync carrega tudo em MemoryStream e
    /// causa OOM no processo da API.
    /// </summary>
    Task DownloadToAsync(string objectKey, Stream destination, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Lista os objetos do bucket (opcionalmente por prefixo) devolvendo chave,
    /// tamanho e data da última modificação — base da manutenção/sanitização do
    /// object storage (contagem de objetos órfãos e tamanho ocupado).
    /// Nunca lança por bucket vazio; para no teto de <paramref name="maxObjects"/>.
    /// </summary>
    Task<ObjectStorageListing> ListObjectsAsync(
        string? prefix,
        int maxObjects,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(ListNotSupportedMessage);

    /// <summary>Mensagem padrão para providers sem suporte a listagem.</summary>
    private const string ListNotSupportedMessage =
        "O storage configurado nao suporta listagem de objetos " +
        "(requer um provider S3-compatível: RustFS/MinIO/S3 ou o provider local).";

    // ── Multipart Upload (arquivos grandes: envio DIRETO do cliente ao storage) ──
    //
    // Fluxo usado pelos instaladores da loja custom (1-10 GiB): o navegador NÃO
    // envia o arquivo para a API — a API cria o multipart, pré-assina cada parte
    // (blocos de 10 MiB) e o cliente faz PUT direto no RustFS/S3, que é o único
    // caminho que passa pelo Cloudflare sem 413 e com retomada real (as partes
    // ficam no storage até o complete/abort).

    // Implementações que NÃO são S3-compatíveis (provider local e doubles de
    // teste) herdam o padrão abaixo: falha explícita em vez de silenciosa.

    /// <summary>Cria o multipart upload e devolve o uploadId do storage.</summary>
    Task<string> CreateMultipartUploadAsync(
        string objectKey,
        string contentType,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>
    /// URL pré-assinada de UMA parte (PUT ?partNumber&amp;uploadId). Assinada
    /// localmente via SigV4 porque o SDK MinIO 7.0.0 não pré-assina partes.
    /// </summary>
    Task<string> GetPresignedUploadPartUrlAsync(
        string objectKey,
        string uploadId,
        int partNumber,
        int ttlMinutes,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>Finaliza o multipart upload com os ETags das partes recebidas.</summary>
    Task CompleteMultipartUploadAsync(
        string objectKey,
        string uploadId,
        IReadOnlyList<MultipartUploadPart> parts,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>Aborta o multipart upload (o storage descarta as partes).</summary>
    Task AbortMultipartUploadAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>
    /// Lista as partes já recebidas pelo storage — fonte de verdade para RETOMAR
    /// um envio interrompido (inclusive depois de restart da API).
    /// </summary>
    Task<IReadOnlyList<MultipartUploadPart>> ListMultipartUploadPartsAsync(
        string objectKey,
        string uploadId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(MultipartNotSupportedMessage);

    /// <summary>Mensagem padrão do multipart em providers sem suporte a presign.</summary>
    private const string MultipartNotSupportedMessage =
        "O storage configurado nao suporta envio multipart com URLs pre-assinadas " +
        "(requer um provider S3-compatível: RustFS/MinIO/S3).";
}
