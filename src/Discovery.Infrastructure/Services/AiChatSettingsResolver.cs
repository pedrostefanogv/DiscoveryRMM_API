using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

public class AiChatSettingsResolver
{
    private readonly IConfigurationResolver _configurationResolver;
    private readonly IAiCredentialResolver _credentialResolver;
    private readonly IAiChatMessageRepository _messageRepository;
    private const int DefaultMaxHistoryMessages = 20;
    private const int DefaultMaxKbContextTokens = 2000;
    private const int DefaultMaxTokens = 1000;
    private const double DefaultTemperature = 0.7;

    public AiChatSettingsResolver(
        IConfigurationResolver configurationResolver,
        IAiCredentialResolver credentialResolver,
        IAiChatMessageRepository messageRepository)
    {
        _configurationResolver = configurationResolver;
        _credentialResolver = credentialResolver;
        _messageRepository = messageRepository;
    }

    public async Task<AIIntegrationSettings> ResolveAsync(Guid siteId, CancellationToken ct)
    {
        var resolved = await _configurationResolver.ResolveForSiteAsync(siteId);
        ct.ThrowIfCancellationRequested();
        var ai = resolved.AIIntegration ?? new AIIntegrationSettings();
        if (resolved.ClientId.HasValue)
        {
            var credential = await _credentialResolver.ResolveAsync(resolved.ClientId.Value, siteId, ct);
            if (credential is not null)
            {
                if (!string.IsNullOrWhiteSpace(credential.ApiKey)) ai.ApiKey = credential.ApiKey;
                if (!string.IsNullOrWhiteSpace(credential.BaseUrl)) ai.BaseUrl = credential.BaseUrl;
                if (!string.IsNullOrWhiteSpace(credential.EmbeddingBaseUrl)) ai.EmbeddingBaseUrl = credential.EmbeddingBaseUrl;
                if (!string.IsNullOrWhiteSpace(credential.EmbeddingApiKey)) ai.EmbeddingApiKey = credential.EmbeddingApiKey;
                if (!string.IsNullOrWhiteSpace(credential.Provider)) ai.Provider = credential.Provider;
            }
        }
        return ai;
    }

    public async Task<int> CalculateConversationTokens(Guid sessionId, CancellationToken ct)
    {
        var stats = await _messageRepository.GetStatsAsync(sessionId, ct);
        return stats.EstimatedTokens;
    }

    public static int ClampHistoryMessages(AIIntegrationSettings settings)
        => settings.MaxHistoryMessages is >= 1 and <= 50 ? settings.MaxHistoryMessages : DefaultMaxHistoryMessages;

    public static int ClampKbContextTokens(AIIntegrationSettings settings)
        => settings.MaxKbContextTokens is >= 500 and <= 8000 ? settings.MaxKbContextTokens : DefaultMaxKbContextTokens;

    public static int ClampMaxTokens(AIIntegrationSettings settings)
        => settings.MaxTokensPerRequest is >= 100 and <= 8000 ? settings.MaxTokensPerRequest : DefaultMaxTokens;

    public static double ClampTemperature(AIIntegrationSettings settings)
        => settings.Temperature is >= 0 and <= 2 ? settings.Temperature : DefaultTemperature;
}
