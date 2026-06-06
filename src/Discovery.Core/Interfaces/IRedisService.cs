namespace Discovery.Core.Interfaces;

public interface IRedisService
{
    bool IsConnected { get; }

    Task<string?> GetAsync(string key);

    Task<long> IncrementAsync(string key);

    Task SetAsync(string key, string value, int expirySeconds = 3600);

    Task<bool> SetExpiryAsync(string key, int expirySeconds);

    Task<int> GetTtlSecondsAsync(string key);

    Task DeleteAsync(string key);

    Task DeleteByPrefixAsync(string prefix);

    Task PublishAsync(string channel, string message);

    Task SubscribeAsync(string channel, Action<string, string> handler);

    /// <summary>Retorna todas as chaves com o prefixo informado. Usa SCAN internamente.</summary>
    Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000);

    /// <summary>
    /// SET key value NX EX expirySeconds.
    /// Retorna true se a chave foi criada (não existia), false se já existia.
    /// Útil para travas distribuídas (distributed locks sem RedLock).
    /// </summary>
    Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds);
}