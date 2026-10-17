using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Services.Remote.Recording;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Api.DependencyInjection;

/// <summary>
/// Registro da ingestão de gravação de terminal (WP3): coordenador singleton
/// (estado em memória por sessão) + hosted service que assina o NATS.
/// </summary>
public static class RemoteRecordingServiceCollectionExtensions
{
    public static IServiceCollection AddTerminalRecordingIngest(this IServiceCollection services)
    {
        services.AddSingleton<TerminalRecordingCoordinator>();
        services.AddHostedService<TerminalRecordingIngestService>();
        return services;
    }
}
