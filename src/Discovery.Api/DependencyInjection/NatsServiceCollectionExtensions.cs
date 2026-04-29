using Discovery.Api.Services;
using Discovery.Core.Interfaces;
using NATS.Client.Core;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registers NATS messaging infrastructure: connection, background services, and bridges.
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
            var opts = new NatsOpts { Url = natsUrl };

            if (!string.IsNullOrWhiteSpace(natsAuthUser) && !string.IsNullOrWhiteSpace(natsAuthPassword))
                opts = opts with { AuthOpts = new NatsAuthOpts { Username = natsAuthUser, Password = natsAuthPassword } };

            return new NatsConnection(opts);
        });

        services.AddHostedService<NatsBackgroundService>();
        services.AddHostedService<NatsSignalRBridge>();
        services.AddHostedService<RemoteDebugNatsBridgeService>();
        services.AddHostedService<RemoteDebugSessionCleanupService>();

        services.AddSingleton<IAiChatJobQueue, AiChatJobBackgroundService>();
        services.AddHostedService(sp => (AiChatJobBackgroundService)sp.GetRequiredService<IAiChatJobQueue>());

        services.AddSingleton<INatsAuthCalloutReloadSignal, NatsAuthCalloutReloadSignal>();
        services.AddHostedService<NatsAuthCalloutBackgroundService>();

        return services;
    }
}
