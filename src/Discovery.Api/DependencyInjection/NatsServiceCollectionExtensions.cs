using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using NATS.Client.Core;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers NATS messaging infrastructure: connection and background services.
/// </summary>
public static class NatsServiceCollectionExtensions
{
    public static IServiceCollection AddDiscoveryNats(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var natsUrl = configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";
        var natsAuthUser = configuration.GetValue<string>("Nats:AuthUser");
        var natsAuthPassword = configuration.GetValue<string>("Nats:AuthPassword");

        services.AddSingleton(_ =>
        {
            var opts = new NatsOpts
            {
                Url = natsUrl,
                // Reconexao: max 10 tentativas com backoff exponencial (1s -> 2s -> 4s -> ... ate 60s).
                ReconnectWaitMin = TimeSpan.FromSeconds(1),
                ReconnectWaitMax = TimeSpan.FromSeconds(60),
                MaxReconnectRetry = 10,
                // Ping interval para detectar conexoes mortas rapidamente.
                PingInterval = TimeSpan.FromMinutes(2),
            };

            if (!string.IsNullOrWhiteSpace(natsAuthUser) && !string.IsNullOrWhiteSpace(natsAuthPassword))
                opts = opts with { AuthOpts = new NatsAuthOpts { Username = natsAuthUser, Password = natsAuthPassword } };

            // TLS: quando a URL usa prefixo tls:// ou wss://, ativa TLS automaticamente.
            if (natsUrl.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
                || natsUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                opts = opts with { TlsOpts = new NatsTlsOpts() };
            }

            return new NatsConnection(opts);
        });

        services.Configure<NatsFanoutStreamOptions>(
            configuration.GetSection(NatsFanoutStreamOptions.SectionName));

        services.Configure<NatsGlobalPongOptions>(
            configuration.GetSection(NatsGlobalPongOptions.SectionName));

        services.AddHostedService<NatsBackgroundService>();
        services.AddHostedService<NatsFanoutStreamBootstrapService>();
        services.AddHostedService<RemoteDebugSessionCleanupService>();

        services.AddSingleton<IAiChatJobQueue, AiChatJobBackgroundService>();
        services.AddHostedService(sp => (AiChatJobBackgroundService)sp.GetRequiredService<IAiChatJobQueue>());

        services.AddSingleton<INatsAuthCalloutReloadSignal, NatsAuthCalloutReloadSignal>();
        services.AddHostedService<NatsAuthCalloutBackgroundService>();

        return services;
    }
}
