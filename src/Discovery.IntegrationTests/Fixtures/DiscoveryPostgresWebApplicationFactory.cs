using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Discovery.IntegrationTests.Fixtures;

/// <summary>
/// WebApplicationFactory&lt;Program&gt; wired to a real PostgreSQL Testcontainer database.
/// Runs genuine FluentMigrator migrations, Npgsql EF Core DbContext, SchemaDriftInspector,
/// DatabaseSeeder, MediatR TransactionBehavior, and ASP.NET Core auth/RBAC pipeline.
/// Only non-PostgreSQL external daemons (NATS/Redis/Quartz background loops) are decoupled.
/// </summary>
public sealed class DiscoveryPostgresWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _databaseName;
    private readonly Action<IServiceCollection>? _configureTestServices;

    private DiscoveryPostgresWebApplicationFactory(
        string connectionString,
        string databaseName,
        Action<IServiceCollection>? configureTestServices)
    {
        _connectionString = connectionString;
        _databaseName = databaseName;
        _configureTestServices = configureTestServices;
    }

    public static async Task<DiscoveryPostgresWebApplicationFactory> CreateAsync(
        string prefix,
        Action<IServiceCollection>? configureTestServices = null)
    {
        var (connectionString, databaseName) = await PostgresContainerFixture.CreateIsolatedDatabaseAsync(prefix);
        var factory = new DiscoveryPostgresWebApplicationFactory(connectionString, databaseName, configureTestServices);
        // Trigger host build + startup migrations + seeder immediately.
        _ = factory.Services;
        return factory;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.UseSetting("Database:Provider", "Postgres");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
        builder.UseSetting("Migrations:RunOnStartup", "true");
        builder.UseSetting("OutputCache:UseRedis", "false");
        builder.UseSetting("Nats:AuthCallout:Enabled", "false");
        builder.UseSetting("AgentPackage:PublicApiServer", "localhost:5288");
        builder.UseSetting("BackgroundJobs:BackgroundProcessingScheduleSync:Enabled", "false");
        builder.UseSetting("BackgroundJobs:PatchMaintenance:Enabled", "false");
        builder.UseSetting("BackgroundJobs:KnowledgeEmbeddingEnabled", "false");
        builder.UseSetting("BackgroundJobs:TicketAnswerEmbedding:Enabled", "false");
        builder.UseSetting("AppCatalog:Winget:Enabled", "false");
        builder.UseSetting("Push:Enabled", "false");
        builder.UseSetting("OpenTelemetry:Enabled", "false");

        builder.ConfigureServices(services =>
        {
            // Keep ASP.NET Core TestServer/hosting services; strip external daemon loops (Quartz/NATS/Redis).
            var externalHostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                    && (d.ImplementationType?.Namespace is null
                        || !d.ImplementationType.Namespace.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)))
                .ToArray();
            foreach (var d in externalHostedServices)
                services.Remove(d);

            services.RemoveAll<IConnectionMultiplexer>();
            services.AddSingleton<IConnectionMultiplexer>(_ => NullConnectionMultiplexerProxy.Create());

            services.RemoveAll<IRedisService>();
            services.AddSingleton<IRedisService, InMemoryRedisService>();

            services.RemoveAll<IHeartbeatCacheService>();
            services.AddSingleton<IHeartbeatCacheService, NoOpHeartbeatCacheService>();

            services.RemoveAll<IAgentMessaging>();
            services.AddSingleton<IAgentMessaging, NoOpAgentMessaging>();

            services.RemoveAll<INatsTenantStreamProvisioner>();
            services.AddSingleton<INatsTenantStreamProvisioner, NoOpNatsTenantStreamProvisioner>();

            _configureTestServices?.Invoke(services);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await PostgresContainerFixture.DropDatabaseAsync(_databaseName);
    }
}
