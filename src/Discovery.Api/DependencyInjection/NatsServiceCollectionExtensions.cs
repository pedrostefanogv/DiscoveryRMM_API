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
        services.AddSingleton(_ =>
        {
            // URL + credenciais + TLS vem do MESMO factory usado pelo health check
            // (antes cada um montava o seu e o health check ficava sem credenciais).
            var opts = NatsOptionsFactory.CreateBase(configuration) with
            {
                // Reconexao: max 10 tentativas com backoff exponencial (1s -> 2s -> 4s -> ... ate 60s).
                ReconnectWaitMin = TimeSpan.FromSeconds(1),
                ReconnectWaitMax = TimeSpan.FromSeconds(60),
                MaxReconnectRetry = 10,
                // Ping interval para detectar conexoes mortas rapidamente.
                PingInterval = TimeSpan.FromMinutes(2),
            };

            return new NatsConnection(opts);
        });

        services.Configure<NatsFanoutStreamOptions>(
            configuration.GetSection(NatsFanoutStreamOptions.SectionName));

        services.Configure<NatsSupportChatStreamOptions>(
            configuration.GetSection(NatsSupportChatStreamOptions.SectionName));

        services.Configure<TicketChatOptions>(
            configuration.GetSection(TicketChatOptions.SectionName));

        services.Configure<NatsGlobalPongOptions>(
            configuration.GetSection(NatsGlobalPongOptions.SectionName));

        services.Configure<NatsCommandRedeliveryOptions>(
            configuration.GetSection(NatsCommandRedeliveryOptions.SectionName));

        services.AddHostedService<NatsBackgroundService>();
        services.AddHostedService<NatsFanoutStreamBootstrapService>();
        // Stream durável do chat de suporte (replay para o agent reconectado).
        services.AddHostedService<NatsSupportChatStreamBootstrapService>();
        // Reentrega de comandos não confirmados (agente estava offline no envio).
        services.AddHostedService<PendingCommandRedeliveryService>();
        services.AddHostedService<RemoteDebugSessionCleanupService>();

        services.AddSingleton<IAiChatJobQueue, AiChatJobBackgroundService>();
        services.AddHostedService(sp => (AiChatJobBackgroundService)sp.GetRequiredService<IAiChatJobQueue>());

        services.AddSingleton<ILabelReprocessQueue, LabelReprocessBackgroundService>();
        services.AddHostedService(sp => (LabelReprocessBackgroundService)sp.GetRequiredService<ILabelReprocessQueue>());

        // Gatilho de reavaliacao por sync de inventario (desabilitado por padrao).
        services.AddSingleton<ILabelRevaluationQueue, LabelRevaluationBackgroundService>();
        services.AddHostedService(sp => (LabelRevaluationBackgroundService)sp.GetRequiredService<ILabelRevaluationQueue>());

        services.AddSingleton<INatsAuthCalloutReloadSignal, NatsAuthCalloutReloadSignal>();
        services.AddHostedService<NatsAuthCalloutBackgroundService>();

        return services;
    }
}
