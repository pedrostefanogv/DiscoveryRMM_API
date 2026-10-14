namespace Discovery.Core.Interfaces;

public interface IRedisService
{
    bool IsConnected { get; }

    /// <summary>
    /// Latência de um PING no Redis, em milissegundos. Retorna null quando o
    /// backend não implementa/está indisponível. Implementação padrão devolve
    /// null para não quebrar fakes de teste existentes.
    /// </summary>
    Task<int?> PingAsync(CancellationToken ct = default) => Task.FromResult<int?>(null);

    Task<string?> GetAsync(string key);

    Task<long> IncrementAsync(string key);

    /// <summary>INCRBY key amount — incrementa o valor em uma quantidade arbitrária.</summary>
    Task<long> IncrementByAsync(string key, long amount);

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