namespace Discovery.Core.Interfaces;

public interface IRedisService
{
    bool IsConnected { get; }

    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value, int expirySeconds = 3600);

    Task DeleteAsync(string key);

    Task DeleteByPrefixAsync(string prefix);

    Task PublishAsync(string channel, string message);

    Task SubscribeAsync(string channel, Action<string, string> handler);
}