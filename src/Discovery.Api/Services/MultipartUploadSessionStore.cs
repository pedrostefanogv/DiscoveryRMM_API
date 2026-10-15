using System.Text.Json;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Schema e chaves do estado de upload multipart DIRETO de assets da loja
/// (compartilhados entre o controller e o serviço de limpeza).
///
/// Duas chaves por upload:
/// - <c>appstore:asset-multipart:{uploadId}</c> — sessão (24h; 1h depois de concluída,
///   para a retentativa de um complete cuja resposta se perdeu devolver o asset);
/// - <c>appstore:asset-multipart-index:{uploadId}</c> — índice com a object key,
///   com TTL MAIOR que a sessão: quando a sessão expira sem o upload ter sido
///   finalizado, é esse registro que permite abortar o multipart no storage.
/// </summary>
public static class MultipartUploadSessionStore
{
    public const string SessionKeyPrefix = "appstore:asset-multipart:";
    public const string IndexKeyPrefix = "appstore:asset-multipart-index:";

    /// <summary>Sessão em andamento (o cliente ainda pode retomar).</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(24);

    /// <summary>Sessão concluída: só espera uma retentativa (resposta perdida).</summary>
    public static readonly TimeSpan CompletedSessionTtl = TimeSpan.FromHours(1);

    /// <summary>Índice: sobrevive à sessão para permitir a limpeza do multipart órfão.</summary>
    public static readonly TimeSpan IndexTtl = TimeSpan.FromHours(26);

    /// <summary>
    /// Marcador de "órfão confirmado": escrito na varredura em que a sessão
    /// aparece ausente e consumido na varredura SEGUINTE. Como IRedisService
    /// devolve null tanto para "não existe" quanto para "falha ao ler", exigir
    /// duas varreduras (30 min de intervalo) impede que um erro transitório do
    /// Redis aborte um upload em andamento.
    /// </summary>
    public const string AbortPendingKeyPrefix = "appstore:asset-multipart-abort-pending:";

    public static readonly TimeSpan AbortPendingTtl = TimeSpan.FromHours(2);

    public static string AbortPendingKey(string uploadId) => AbortPendingKeyPrefix + uploadId;

    public static string SessionKey(string uploadId) => SessionKeyPrefix + uploadId;

    public static string IndexKey(string uploadId) => IndexKeyPrefix + uploadId;

    public static string ExtractUploadIdFromIndexKey(string key) =>
        key.StartsWith(IndexKeyPrefix, StringComparison.Ordinal) ? key[IndexKeyPrefix.Length..] : key;

    /// <summary>Alinha o índice ao formato atual (o instante é obrigatório).</summary>
    public static MultipartUploadIndexEntry? ReadIndexEntry(string? json)
    {
        var entry = Deserialize<MultipartUploadIndexEntry>(json);
        return entry is null ? null : entry with { CreatedAtUtc = entry.CreatedAtUtc };
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    public static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task SaveAsync(IRedisService redis, MultipartUploadSessionState state, TimeSpan ttl)
        => await redis.SetAsync(SessionKey(state.UploadId), Serialize(state), (int)ttl.TotalSeconds);

    public static async Task<MultipartUploadSessionState?> LoadAsync(IRedisService redis, string uploadId)
        => string.IsNullOrWhiteSpace(uploadId)
            ? null
            : Deserialize<MultipartUploadSessionState>(await redis.GetAsync(SessionKey(uploadId)));

    /// <summary>
    /// Registra a object key por mais tempo que a sessão (limpeza de órfãos em
    /// andamento).
    /// </summary>
    public static async Task SaveIndexAsync(IRedisService redis, string uploadId, string objectKey)
        => await redis.SetAsync(
            IndexKey(uploadId),
            Serialize(new MultipartUploadIndexEntry(uploadId, objectKey, DateTimeOffset.UtcNow)),
            (int)IndexTtl.TotalSeconds);

    /// <summary>
    /// Índice de um upload JÁ CONCLUÍDO. Fica até o vínculo com o pacote (ou até
    /// <see cref="UnlinkedAssetTtl"/>): é o que permite descartar o objeto quando
    /// o operador troca o arquivo antes de salvar — sem isso cada troca vaza um
    /// objeto de vários GiB no storage.
    /// </summary>
    public static async Task SaveCompletedIndexAsync(IRedisService redis, string uploadId, string objectKey)
        => await redis.SetAsync(
            IndexKey(uploadId),
            Serialize(new MultipartUploadIndexEntry(
                uploadId, objectKey, DateTimeOffset.UtcNow, Completed: true, CompletedAtUtc: DateTimeOffset.UtcNow)),
            (int)CompletedIndexTtl.TotalSeconds);

    /// <summary>Prazo para o asset publicado ser vinculado a um pacote.</summary>
    public static readonly TimeSpan UnlinkedAssetTtl = TimeSpan.FromHours(24);

    /// <summary>Índice de asset concluído: cobre o prazo de vínculo com folga.</summary>
    public static readonly TimeSpan CompletedIndexTtl = TimeSpan.FromHours(48);

    public static async Task DeleteAsync(IRedisService redis, string uploadId)
    {
        await redis.DeleteAsync(SessionKey(uploadId));
        await redis.DeleteAsync(IndexKey(uploadId));
    }
}

/// <summary>Estado persistido de um upload multipart direto.</summary>
public sealed class MultipartUploadSessionState
{
    public string UploadId { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public string Kind { get; set; } = "installer";
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public long PartSizeBytes { get; set; } = 10L * 1024 * 1024;
    public int PartUrlTtlMinutes { get; set; } = 60;

    /// <summary>Multipart já finalizado no storage (ver <see cref="CompletedSessionTtlOnStore"/>).</summary>
    public bool Completed { get; set; }

    /// <summary>Partes usadas no complete — permitem repetir a chamada sem consultar o storage.</summary>
    public List<Discovery.Core.DTOs.MultipartUploadPartStateDto> Parts { get; set; } = [];

    /// <summary>SHA-256 já calculado: a retentativa não relê o arquivo inteiro.</summary>
    public string? Checksum { get; set; }

    /// <summary>Dono do upload: o uploadId sozinho não dá acesso ao envio de terceiros.</summary>
    public Guid? UserId { get; set; }

    private static readonly TimeSpan CompletedSessionTtlOnStore = TimeSpan.FromHours(1);
}

/// <summary>
/// Índice para o serviço de limpeza abortar multiparts órfãos. O instante de
/// criação é obrigatório: sem ele o limpador não consegue distinguir "sessão
/// expirou" de "sessão nunca foi gravada" (Redis indisponível no create) e
/// abortaria um upload que o usuário está enviando AGORA.
/// </summary>
public sealed record MultipartUploadIndexEntry(
    string UploadId,
    string ObjectKey,
    DateTimeOffset CreatedAtUtc,
    bool Completed = false,
    DateTimeOffset? CompletedAtUtc = null);
