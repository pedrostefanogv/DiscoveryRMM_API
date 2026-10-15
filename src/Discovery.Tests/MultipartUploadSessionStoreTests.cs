using Discovery.Api.Services;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;

namespace Discovery.Tests;

/// <summary>
/// O estado do upload multipart direto vive no Redis (sessão + índice). O índice
/// é o que permite ao serviço de limpeza abortar multiparts órfãos depois que a
/// sessão expira — se as chaves saírem de sincronia, o storage acumula partes de
/// envios abandonados para sempre.
/// </summary>
public class MultipartUploadSessionStoreTests
{
    [Test]
    public async Task Save_e_Load_preservam_estado_e_usam_as_chaves_esperadas()
    {
        var redis = new InMemoryRedisService();
        var state = new MultipartUploadSessionState
        {
            UploadId = "upload-abc",
            ObjectKey = "app-store/custom/installers/abc123/Setup.exe",
            Kind = "installer",
            FileName = "Setup.exe",
            ContentType = "application/x-msdownload",
            SizeBytes = 2_000_000_000,
            UserId = Guid.NewGuid(),
        };

        await MultipartUploadSessionStore.SaveAsync(redis, state, MultipartUploadSessionStore.SessionTtl);
        await MultipartUploadSessionStore.SaveIndexAsync(redis, state.UploadId, state.ObjectKey);

        var loaded = await MultipartUploadSessionStore.LoadAsync(redis, state.UploadId);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.ObjectKey, Is.EqualTo(state.ObjectKey));
        Assert.That(loaded.UserId, Is.EqualTo(state.UserId));
        Assert.That(loaded.Completed, Is.False);

        // Chaves exatas: é o prefixo que o controller e o janitor usam.
        Assert.That(redis.Keys, Does.Contain("appstore:asset-multipart:upload-abc"));
        Assert.That(redis.Keys, Does.Contain("appstore:asset-multipart-index:upload-abc"));

        // O índice vive MAIS que a sessão (senão o órfão nunca seria limpo).
        Assert.That(
            redis.TtlOf("appstore:asset-multipart-index:upload-abc"),
            Is.GreaterThan(redis.TtlOf("appstore:asset-multipart:upload-abc")));

        var indexEntry = MultipartUploadSessionStore.Deserialize<MultipartUploadIndexEntry>(
            await redis.GetAsync("appstore:asset-multipart-index:upload-abc"));
        Assert.That(indexEntry!.UploadId, Is.EqualTo("upload-abc"));
        Assert.That(indexEntry.ObjectKey, Is.EqualTo(state.ObjectKey));
    }

    [Test]
    public void ExtractUploadIdFromIndexKey_ignora_o_prefixo()
    {
        Assert.That(
            MultipartUploadSessionStore.ExtractUploadIdFromIndexKey("appstore:asset-multipart-index:xyz"),
            Is.EqualTo("xyz"));
    }

    [Test]
    public async Task Concluido_marca_a_sessao_e_permite_repetir_as_partes()
    {
        var redis = new InMemoryRedisService();
        var state = new MultipartUploadSessionState
        {
            UploadId = "upload-abc",
            ObjectKey = "app-store/custom/installers/abc/Setup.exe",
            Completed = true,
            Parts = [new MultipartUploadPartStateDto(1, "etag-1", 10)],
        };

        await MultipartUploadSessionStore.SaveAsync(redis, state, MultipartUploadSessionStore.CompletedSessionTtl);

        var loaded = await MultipartUploadSessionStore.LoadAsync(redis, "upload-abc");
        Assert.That(loaded!.Completed, Is.True);
        Assert.That(loaded.Parts, Has.Count.EqualTo(1));
        Assert.That(loaded.Parts[0].ETag, Is.EqualTo("etag-1"));
    }

    [Test]
    public async Task SaveCompletedIndex_marca_conclusao_e_vive_alem_do_prazo_de_vinculo()
    {
        // O índice do asset CONCLUÍDO é o que permite descartar um objeto publicado
        // que nunca foi vinculado (troca do arquivo antes de salvar): precisa viver
        // mais que o prazo de vínculo (24h), senão o objeto fica órfão para sempre.
        var redis = new InMemoryRedisService();
        await MultipartUploadSessionStore.SaveCompletedIndexAsync(redis, "u1", "app-store/custom/installers/a/Setup.exe");

        var entry = MultipartUploadSessionStore.ReadIndexEntry(
            await redis.GetAsync("appstore:asset-multipart-index:u1"));

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Completed, Is.True);
        Assert.That(entry.CompletedAtUtc, Is.Not.Null);
        Assert.That(
            redis.TtlOf("appstore:asset-multipart-index:u1"),
            Is.GreaterThan((int)MultipartUploadSessionStore.UnlinkedAssetTtl.TotalSeconds));
    }

    [Test]
    public async Task Delete_remove_sessao_e_indice()
    {
        var redis = new InMemoryRedisService();
        await MultipartUploadSessionStore.SaveAsync(redis, new MultipartUploadSessionState { UploadId = "u1" }, TimeSpan.FromHours(1));
        await MultipartUploadSessionStore.SaveIndexAsync(redis, "u1", "key/1");

        await MultipartUploadSessionStore.DeleteAsync(redis, "u1");

        Assert.That(await redis.GetAsync("appstore:asset-multipart:u1"), Is.Null);
        Assert.That(await redis.GetAsync("appstore:asset-multipart-index:u1"), Is.Null);
    }

    [Test]
    public async Task Load_com_json_invalido_devolve_null_sem_explodir()
    {
        var redis = new InMemoryRedisService();
        await redis.SetAsync("appstore:asset-multipart:u1", "{nao-e-json", 60);

        Assert.That(await MultipartUploadSessionStore.LoadAsync(redis, "u1"), Is.Null);
    }

    /// <summary>Redis em memória com TTL por chave (suficiente para o schema).</summary>
    private sealed class InMemoryRedisService : IRedisService
    {
        private readonly Dictionary<string, (string Value, int Ttl)> _values = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => _values.Keys;
        public int TtlOf(string key) => _values.TryGetValue(key, out var entry) ? entry.Ttl : -1;

        public bool IsConnected => true;
        public Task<string?> GetAsync(string key)
            => Task.FromResult(_values.TryGetValue(key, out var entry) ? entry.Value : null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600)
        {
            _values[key] = (value, expirySeconds);
            return Task.CompletedTask;
        }
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(false);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(TtlOf(key));
        public Task DeleteAsync(string key)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
        public Task DeleteByPrefixAsync(string prefix)
        {
            foreach (var key in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                _values.Remove(key);
            return Task.CompletedTask;
        }
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>(
                _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Take(maxResults).ToArray());
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }
}
