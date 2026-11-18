using System.Diagnostics;
using System.Net;
using Discovery.Api.Controllers;
using Discovery.Api.DependencyInjection;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using StackExchange.Redis;

namespace Discovery.E2E.Tests.Harness;

public sealed class FakeNatsTenantStreamProvisioner : INatsTenantStreamProvisioner
{
    public Task EnsureGlobalFanoutStreamAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureTenantFanoutStreamAsync(Guid clientId, CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureTenantSupportChatStreamAsync(Guid clientId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MigrateLegacyWildcardStreamsAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// In-process Kestrel (127.0.0.1:0) E2E test host for Discovery.Api using
/// DiagnosticListener("Microsoft.Extensions.Hosting") ("HostBuilding" + "HostBuilt"),
/// shared open SQLite :memory: DB, and in-memory Redis/Heartbeat/NATS fakes.
/// </summary>
public sealed class ApiE2ETestHost : IAsyncDisposable
{
    private static readonly SemaphoreSlim BootGate = new(1, 1);

    private readonly SqliteConnection _sqliteConnection;
    private readonly IReadOnlyDictionary<string, string?> _extraConfig;
    private readonly bool _useFakeAgentMessaging;
    private IHost? _host;
    private Task? _entryPointTask;
    private HttpClient? _httpClient;

    public FakeRedisService FakeRedis { get; } = new();
    public FakeHeartbeatCacheService FakeHeartbeatCache { get; } = new();
    public FakeAgentMessaging FakeAgentMessaging { get; } = new();
    public Uri BaseAddress { get; private set; } = new("http://127.0.0.1");
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started.");
    public HttpClient HttpClient => _httpClient ?? throw new InvalidOperationException("Host not started.");

    public ApiE2ETestHost(
        IReadOnlyDictionary<string, string?>? extraConfig = null,
        bool useFakeAgentMessaging = true)
    {
        _extraConfig = extraConfig ?? new Dictionary<string, string?>();
        _useFakeAgentMessaging = useFakeAgentMessaging;
        _sqliteConnection = new SqliteConnection("DataSource=:memory:");
    }

    public static async Task<ApiE2ETestHost> StartAsync(
        IReadOnlyDictionary<string, string?>? extraConfig = null,
        bool useFakeAgentMessaging = true,
        CancellationToken ct = default)
    {
        var instance = new ApiE2ETestHost(extraConfig, useFakeAgentMessaging);
        await instance.InitializeAsync(ct);
        return instance;
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await _sqliteConnection.OpenAsync(ct);

        var apiContentRoot = FindApiContentRoot();
        var argsList = new List<string>
        {
            "--urls=http://127.0.0.1:0",
            "--environment=Development",
            "--applicationName=Discovery.Api",
            $"--contentRoot={apiContentRoot}",
            "--Database:Provider=Postgres",
            "--ConnectionStrings:DefaultConnection=Host=localhost;Database=e2e",
            "--Migrations:RunOnStartup=false",
            "--Nats:AuthCallout:Enabled=false",
            "--AgentPackage:PublicApiServer=localhost:5288",
            "--BackgroundJobs:BackgroundProcessingScheduleSync:Enabled=false",
            "--BackgroundJobs:PatchMaintenance:Enabled=false",
            "--Push:Enabled=false",
            "--OpenTelemetry:Enabled=false"
        };

        foreach (var kv in _extraConfig)
        {
            if (kv.Value is not null)
                argsList.Add($"--{kv.Key}={kv.Value}");
        }

        var args = argsList.ToArray();
        var hostBuiltTcs = new TaskCompletionSource<IHost>(TaskCreationOptions.RunContinuationsAsynchronously);

        await BootGate.WaitAsync(ct);
        try
        {
            using var listenerSub = DiagnosticListener.AllListeners.Subscribe(
                new HostingDiagnosticObserver(this, hostBuiltTcs));

            var entryPoint = typeof(ClientsController).Assembly.EntryPoint
                ?? throw new InvalidOperationException("Discovery.Api EntryPoint not found.");

            _entryPointTask = Task.Run(() =>
            {
                try
                {
                    var ret = entryPoint.Invoke(null, [args]);
                    if (ret is Task t)
                        t.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    hostBuiltTcs.TrySetException(ex);
                    throw;
                }
            }, CancellationToken.None);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            _host = await hostBuiltTcs.Task.WaitAsync(timeoutCts.Token);

            var lifetime = _host.Services.GetRequiredService<IHostApplicationLifetime>();
            var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var startedReg = lifetime.ApplicationStarted.Register(() => startedTcs.TrySetResult());
            if (!lifetime.ApplicationStarted.IsCancellationRequested)
            {
                await startedTcs.Task.WaitAsync(timeoutCts.Token);
            }

            var server = _host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            var boundAddress = addresses?.FirstOrDefault()
                ?? throw new InvalidOperationException("Kestrel did not bind any listening address.");

            BaseAddress = new Uri(boundAddress);
            _httpClient = new HttpClient
            {
                BaseAddress = BaseAddress,
                Timeout = TimeSpan.FromSeconds(20)
            };
        }
        finally
        {
            BootGate.Release();
        }
    }

    private void ConfigureE2EServices(HostBuilderContext ctx, IServiceCollection services)
    {
        services.RemoveAll<DbContextOptions<DiscoveryDbContext>>();
        services.RemoveAll<DbContextOptions>();
        services.RemoveAll<DiscoveryDbContext>();

        var dbConfigDescriptors = services
            .Where(d => d.ServiceType.IsGenericType
                && d.ServiceType.GetGenericTypeDefinition() == typeof(IDbContextOptionsConfiguration<>)
                && d.ServiceType.GenericTypeArguments[0] == typeof(DiscoveryDbContext))
            .ToArray();
        foreach (var d in dbConfigDescriptors)
            services.Remove(d);

        var dbOptionsBuilder = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseSqlite(_sqliteConnection)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

        services.AddSingleton(dbOptionsBuilder.Options);
        services.AddSingleton<DbContextOptions>(dbOptionsBuilder.Options);
        services.AddScoped<DiscoveryDbContext, SqliteE2EDbContext>();

        // Strip all application/Quartz/NATS/Redis IHostedService registrations while keeping
        // ASP.NET Core's GenericWebHostService so Kestrel actually binds and serves HTTP/WS.
        var nonWebHostServices = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                && (d.ImplementationType?.Namespace is null
                    || !d.ImplementationType.Namespace.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)))
            .ToArray();
        foreach (var d in nonWebHostServices)
            services.Remove(d);

        // Replace Redis / Heartbeat / NATS singletons with thread-safe in-memory fakes.
        services.RemoveAll<IConnectionMultiplexer>();
        services.AddSingleton<IConnectionMultiplexer>(_ => FakeConnectionMultiplexerProxy.Create());

        services.RemoveAll<IRedisService>();
        services.AddSingleton<IRedisService>(FakeRedis);

        services.RemoveAll<IHeartbeatCacheService>();
        services.AddSingleton<IHeartbeatCacheService>(FakeHeartbeatCache);

        if (_useFakeAgentMessaging)
        {
            services.RemoveAll<IAgentMessaging>();
            services.AddSingleton<IAgentMessaging>(FakeAgentMessaging);

            services.RemoveAll<INatsTenantStreamProvisioner>();
            services.AddSingleton<INatsTenantStreamProvisioner, FakeNatsTenantStreamProvisioner>();
        }

        // Guarantee in-memory OutputCache store is wired.
        services.AddDiscoveryOutputCache(ctx.Configuration);
    }

    private void InitializeDatabaseBeforeSeeder(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        db.Database.EnsureCreated();
    }

    public async Task<T> WithDbAsync<T>(Func<DiscoveryDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        return await action(db);
    }

    public async Task WithDbAsync(Func<DiscoveryDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        await action(db);
    }

    public async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    private static string FindApiContentRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Discovery.Api");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Discovery.Api.csproj")))
                return candidate;

            var nested = Path.Combine(dir.FullName, "DiscoveryRMM_API", "src", "Discovery.Api");
            if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, "Discovery.Api.csproj")))
                return nested;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Discovery.Api directory.");
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();

        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore shutdown race.
            }
            _host.Dispose();
        }

        if (_entryPointTask is not null)
        {
            try
            {
                await _entryPointTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore shutdown cancellation.
            }
        }

        await _sqliteConnection.DisposeAsync();
    }

    private sealed class HostingDiagnosticObserver(
        ApiE2ETestHost owner,
        TaskCompletionSource<IHost> hostBuiltTcs) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private IDisposable? _innerSub;

        public void OnCompleted() => _innerSub?.Dispose();
        public void OnError(Exception error) { }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.Extensions.Hosting")
                _innerSub = listener.Subscribe(this);
        }

        public void OnNext(KeyValuePair<string, object?> evt)
        {
            if (evt.Key == "HostBuilding" && evt.Value is IHostBuilder hb)
            {
                hb.UseDefaultServiceProvider(opts =>
                {
                    opts.ValidateScopes = false;
                    opts.ValidateOnBuild = false;
                });
                hb.ConfigureServices(owner.ConfigureE2EServices);
            }
            else if (evt.Key == "HostBuilt" && evt.Value is IHost host)
            {
                try
                {
                    owner.InitializeDatabaseBeforeSeeder(host);
                    hostBuiltTcs.TrySetResult(host);
                }
                catch (Exception ex)
                {
                    hostBuiltTcs.TrySetException(ex);
                    throw;
                }
            }
        }
    }
}
