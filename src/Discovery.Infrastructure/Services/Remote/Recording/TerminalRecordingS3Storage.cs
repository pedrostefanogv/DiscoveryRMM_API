using System.Net;
using System.Net.Http.Headers;
using Discovery.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services.Remote.Recording;

/// <summary>
/// Storage S3-compatible (AWS S3, MinIO, R2, Wasabi) do NDJSON da gravação de
/// terminal. Segue o mesmo esquema de URL e as mesmas opções
/// (<c>RemoteAccess:Recording:S3</c>) do <see cref="S3RecordingStorage"/> já
/// existente: path style quando configurado, virtual-host caso contrário.
///
/// A gravação em si continua sempre local (append incremental); este storage só
/// entra no finalize (upload), no download (URL pré-assinada) e na remoção
/// (exclusão individual e retenção).
/// </summary>
public sealed class TerminalRecordingS3Storage
{
    /// <summary>Content-type do NDJSON (um frame JSON por linha).</summary>
    public const string ContentType = "application/x-ndjson";

    /// <summary>
    /// TTL máximo da URL pré-assinada de download, em minutos. O download é
    /// interativo e o link não deve sobreviver na mão de quem o recebeu.
    /// </summary>
    public const int ShortPresignTtlMinutes = 5;

    private readonly RemoteAccessOptions _options;
    private readonly ILogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public TerminalRecordingS3Storage(
        RemoteAccessOptions options,
        ILogger logger,
        IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Chave do objeto no bucket: terminal/{sessionId:N}.ndjson.</summary>
    public static string BuildKey(Guid sessionId) => $"terminal/{sessionId:N}.ndjson";

    /// <summary>URL do objeto no S3 (mesmo padrão do S3RecordingStorage).</summary>
    public static string BuildObjectUrl(RemoteAccessRecordingS3Options s3, string key)
        => s3.UsePathStyle
            ? $"{s3.Endpoint.TrimEnd('/')}/{s3.Bucket}/{key}"
            : $"https://{s3.Bucket}.{s3.Endpoint.TrimEnd('/')}/{key}";

    /// <summary>
    /// URL pré-assinada de download, com TTL curto (mínimo entre
    /// <c>Recording:S3:PresignTtlMinutes</c> e <see cref="ShortPresignTtlMinutes"/>).
    /// Mantém o formato do presign já usado em <see cref="S3RecordingStorage"/>.
    /// </summary>
    public static string PresignUrl(RemoteAccessRecordingS3Options s3, string key)
    {
        var ttlMinutes = s3.PresignTtlMinutes > 0
            ? Math.Min(s3.PresignTtlMinutes, ShortPresignTtlMinutes)
            : ShortPresignTtlMinutes;

        var expiry = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes).ToUnixTimeSeconds();
        return $"{BuildObjectUrl(s3, key)}?X-Amz-Expires={expiry}";
    }

    /// <summary>
    /// Envia o NDJSON (à partir do arquivo local) ao S3, com timeout por
    /// tentativa e até <c>UploadMaxAttempts</c> tentativas. Lê por stream:
    /// o arquivo pode ter dezenas de MB e não precisa ir inteiro para a memória.
    /// Lança quando todas falham: quem chama decide preservar o local e
    /// registrar o erro.
    /// </summary>
    public async Task<string> UploadFileAsync(string key, string fullPath, CancellationToken ct = default)
    {
        var terminal = _options.Recording.Terminal;
        var attempts = Math.Max(1, terminal.UploadMaxAttempts);
        var timeoutSeconds = Math.Clamp(
            terminal.UploadTimeoutSeconds > 0 ? terminal.UploadTimeoutSeconds : 30,
            1,
            600);

        var url = BuildObjectUrl(_options.Recording.S3, key);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                var client = _httpClientFactory.CreateClient("S3Recording");

                await using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                using var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
                content.Headers.ContentLength = stream.Length;

                using var response = await client.PutAsync(url, content, timeout.Token);
                response.EnsureSuccessStatusCode();

                _logger.LogInformation(
                    "NDJSON da gravação de terminal enviado ao S3: {Key} ({Bytes} bytes, tentativa {Attempt})",
                    key, stream.Length, attempt);

                return url;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // Timeout da tentativa (não do chamador): vale retry.
                lastError = new TimeoutException(
                    $"timeout de {timeoutSeconds}s no upload de {key} para o S3 (tentativa {attempt}/{attempts})", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }

            if (attempt < attempts)
            {
                _logger.LogWarning(
                    lastError,
                    "Falha ao enviar o NDJSON da gravação de terminal ao S3 (tentativa {Attempt}/{Attempts}): {Key}",
                    attempt, attempts, key);

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new InvalidOperationException(
            $"Falha ao enviar o NDJSON da gravação de terminal ao S3 após {attempts} tentativa(s): {key}",
            lastError);
    }

    /// <summary>
    /// Remove o objeto. Objeto inexistente (404) não é erro: a retenção e o
    /// purge do agente precisam ser idempotentes.
    /// </summary>
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        var url = BuildObjectUrl(_options.Recording.S3, key);

        var client = _httpClientFactory.CreateClient("S3Recording");
        using var response = await client.DeleteAsync(url, ct);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("NDJSON da gravação de terminal removido do S3: {Key}", key);
            return true;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("NDJSON da gravação de terminal já não existe no S3: {Key}", key);
            return false;
        }

        _logger.LogWarning(
            "Storage S3 respondeu {Status} ao remover o NDJSON da gravação de terminal {Key}",
            (int)response.StatusCode, key);
        return false;
    }
}
