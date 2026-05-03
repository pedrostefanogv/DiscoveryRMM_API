using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.AspNetCore;
using Discovery.Api;
using Discovery.Api.Filters;
using Discovery.Api.Validators;
using FluentMigrator.Runner;
using Discovery.Api.Hubs;
using Discovery.Api.Middleware;
using Discovery.Api.Services;
using Discovery.Api.DependencyInjection;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Scalar.AspNetCore;

var hasMaintenanceMode = MaintenanceMode.TryParse(args, out var maintenanceOptions, out var parseError);
if (hasMaintenanceMode && !string.IsNullOrWhiteSpace(parseError))
{
    Console.Error.WriteLine(parseError);
    Environment.ExitCode = 2;
    return;
}

if (hasMaintenanceMode && maintenanceOptions.ShowHelp)
{
    MaintenanceMode.PrintHelp();
    return;
}

var builder = WebApplication.CreateBuilder(args);

var agentHostProfile = AgentPackageStartup.ResolveActiveProfile(builder.Configuration);
var agentInstallerTarget = AgentPackageStartup.ResolveSetting(builder.Configuration, agentHostProfile, "InstallerTargetPlatform") ?? "windows/amd64";
if (!string.Equals(agentInstallerTarget, "windows/amd64", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException($"AgentPackage installer target must be windows/amd64. Resolved value: {agentInstallerTarget}");
}

if (!hasMaintenanceMode)
{
    AgentPackageStartup.ValidateRequired(builder.Configuration, agentHostProfile, "DiscoveryProjectPath");
    AgentPackageStartup.ValidateRequired(builder.Configuration, agentHostProfile, "BinaryPath");
    AgentPackageStartup.ValidateRequired(builder.Configuration, agentHostProfile, "PublicApiServer");
}

var databaseProvider = builder.Configuration.GetValue<string>("Database:Provider") ?? "Postgres";
var isSqlite = databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);

if (isSqlite)
{
    throw new InvalidOperationException("SQLite is no longer supported during the EF Core migration. Configure Database:Provider=Postgres.");
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<DiscoveryDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure();
        npgsqlOptions.UseVector();
    }));

// Auto-registered DI services (repositories + domain services)
// Most services in Discovery follow a 1:1 interface-to-implementation pattern.
// The helper below scans Discovery.Infrastructure and registers any interface that has exactly one concrete implementation.
var autoRegisteredServices = builder.Services.AddDiscoveryAutoRegisteredServices();

// Special registrations (singleton/hosted services, multi-implementation patterns)

// Multi-implementation services (explicitly registered)
builder.Services.AddScoped<IReportRenderer, XlsxReportRenderer>();
builder.Services.AddScoped<IReportRenderer, CsvReportRenderer>();
builder.Services.AddScoped<IReportRenderer, MarkdownReportRenderer>();

// Implemented in Discovery.Api (outside Discovery.Infrastructure auto-scan)
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAgentCommandDispatcher, AgentCommandDispatcher>();
builder.Services.AddScoped<ISyncInvalidationPublisher, SyncInvalidationPublisher>();
builder.Services.AddScoped<MeshCentralIdentitySyncTriggerService>();
builder.Services.AddSingleton<IRemoteDebugSessionManager, RemoteDebugSessionManager>();
builder.Services.AddSingleton<IRemoteDebugLogRelay, RemoteDebugLogRelayService>();

// PDF rendering using Playwright.NET (embedded, no external service required, zero vulnerabilities)
if (builder.Configuration.GetValue<bool>("Reporting:EnablePdf"))
{
    builder.Services.AddScoped<IReportRenderer, PlaywrightPdfReportRenderer>();
}

// Factory resolve IObjectStorageService dynamically based on ServerConfiguration
builder.Services.AddScoped<IObjectStorageService>(sp =>
    sp.GetRequiredService<IObjectStorageProviderFactory>().CreateObjectStorageService());

// Special singletons
builder.Services.AddSingleton<ChocolateyApiClient>();
builder.Services.AddSingleton<WingetFeedClient>();

builder.Services.AddHttpClient();
builder.Services.AddDiscoveryOpenTelemetry(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<IAgentTlsCertificateProbe, AgentTlsCertificateProbe>();
var isDevelopment = builder.Environment.IsDevelopment();

var backgroundServicesConfig = BackgroundServicesCollectionExtensions.ReadBackgroundServicesConfig(builder.Configuration, isDevelopment);

// AI Chat & MCP (auto-registered via AddDiscoveryAutoRegisteredServices)
// Only the LLM provider is explicitly registered as singleton.
builder.Services.AddSingleton<ILlmProvider, OpenAiProvider>();

builder.Services.AddDiscoveryBackgroundServices(backgroundServicesConfig);

builder.Services.Configure<MeshCentralOptions>(
    builder.Configuration.GetSection("MeshCentral"));
builder.Services.Configure<AutoTicketOptions>(
    builder.Configuration.GetSection(AutoTicketOptions.SectionName));
builder.Services.Configure<SecretEncryptionOptions>(
    builder.Configuration.GetSection(SecretEncryptionOptions.SectionName));

// P2p Discovery options
builder.Services.Configure<P2pOptions>(
    builder.Configuration.GetSection(P2pOptions.SectionName));

// P2p Discovery service (singleton porque gerencia timers de debounce)
builder.Services.AddSingleton<P2pDiscoveryService>();

// IMemoryCache (para ConfigurationResolver)
builder.Services.AddMemoryCache();

// Configuração de logging automático
builder.Services.Configure<AutomaticLoggingOptions>(
    builder.Configuration.GetSection("AutomaticLogging"));

// Configuração de reporting
builder.Services.Configure<ReportingOptions>(
    builder.Configuration.GetSection("Reporting"));

builder.Services.AddDiscoveryNats(builder.Configuration);
builder.Services.AddDiscoveryRedis(builder.Configuration);

// ── Identity & Auth ───────────────────────────────────────────────────────
// Scoped repos/services above are auto-registered via AddDiscoveryAutoRegisteredServices.
// Explicit registrations below preserve non-default lifetimes.
builder.Services.AddSingleton<IJwtService, JwtService>();
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();

//  Controllers + JSON config
builder.Services.AddControllers(options =>
{
    // Registra LoggingActionFilter globalmente
    options.Filters.Add<LoggingActionFilter>();
    // Proteção global: por padrão toda action exige autenticação de usuário/API token.
    // Endpoints públicos devem declarar [AllowAnonymous].
    options.Filters.Add<RequireUserAuthAttribute>();
})
    .AddJsonOptions(opts =>
    {
        // Permite que a API aceite JSON em camelCase ou PascalCase
        opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        opts.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateAgentRequestValidator>();

// SignalR for dashboard real-time with Redis backplane for multi-instance
var redisConnString = builder.Configuration.GetValue<string>("Redis:Connection") ?? "127.0.0.1:6379";
var redisPassword = builder.Configuration.GetValue<string>("Redis:Password");
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.HandshakeTimeout = TimeSpan.FromSeconds(15);
})
.AddStackExchangeRedis(redisConnString, options =>
{
    options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("SignalR");
    if (!string.IsNullOrWhiteSpace(redisPassword))
        options.Configuration.Password = redisPassword;
});

// Filtro de autorização global do SignalR — equivalente ao default_permissions do NATS
// Agents só podem chamar métodos do próprio escopo; usuários não chamam métodos de agent
builder.Services.AddSingleton<IHubFilter, AgentHubAuthorizationFilter>();

// OpenAPI + Scalar
builder.Services.AddOpenApi();
builder.Services.AddDiscoveryApiVersioning();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddDiscoveryRateLimiting(builder.Configuration);
builder.Services.AddDiscoveryCors(builder.Configuration, isDevelopment);
builder.Services.AddDiscoveryHealthChecks(builder.Configuration);
builder.Services.AddDiscoveryOutputCache(builder.Configuration);
builder.Services.AddDiscoveryQuartz(builder.Configuration);

// FluentMigrator
builder.Services.AddFluentMigratorCore()
    .ConfigureRunner(rb =>
    {
        rb.AddPostgres().WithGlobalConnectionString(connectionString);
        rb.ScanIn(typeof(Discovery.Migrations.Migrations.M001_CreateClients).Assembly).For.Migrations();
    })
    .AddLogging(lb => lb.AddFluentMigratorConsole());

var app = builder.Build();

if (autoRegisteredServices.Count > 0)
{
    app.Logger.LogInformation("DI auto-registration completed with {Count} services.", autoRegisteredServices.Count);
    foreach (var registration in autoRegisteredServices)
    {
        app.Logger.LogDebug(
            "DI auto-registration: {Interface} -> {Implementation}",
            registration.InterfaceType.FullName,
            registration.ImplementationType.FullName);
    }
}

app.Logger.LogInformation(
    "AgentPackage startup config: hostProfile={Profile}, host={Host}, installerTarget={Target}",
    agentHostProfile,
    OperatingSystem.IsWindows() ? "windows" : "linux",
    agentInstallerTarget);

if (hasMaintenanceMode)
{
    // Run migrations first so that recover-admin can bind users to roles/groups
    using (var migrationScope = app.Services.CreateScope())
    {
        var runner = migrationScope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }

    var maintenanceExitCode = await MaintenanceMode.ExecuteAsync(app.Services, maintenanceOptions);
    Environment.ExitCode = maintenanceExitCode;
    return;
}

// Run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    runner.MigrateUp();
}

// Seed default workflow states
await DatabaseSeeder.SeedAsync(app.Services);

// Wire Quartz job execution history listener
await QuartzServiceCollectionExtensions.WireJobListenerAsync(app.Services);

// Configure the HTTP request pipeline
var openApiEnabled = builder.Configuration.GetValue("OpenApi:Enabled", app.Environment.IsDevelopment());
var scalarEnabled = openApiEnabled && builder.Configuration.GetValue("OpenApi:Scalar:Enabled", true);
if (openApiEnabled)
{
    app.MapOpenApi().AllowAnonymous();
}

if (scalarEnabled)
{
    // Scalar API reference pointing to the v1 OpenAPI document with version selector enabled
    app.MapScalarApiReference(options =>
    {
        options.WithOpenApiRoutePattern("/openapi/{documentName}.json");
        options.WithTitle("Discovery RMM API");
        options.WithTheme(ScalarTheme.Purple);
        options.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    }).AllowAnonymous();

    // Legacy convenience redirects
    app.MapGet("/api/scalar", () => Results.Redirect("/scalar/v1", permanent: true)).AllowAnonymous();
    app.MapGet("/scalar", () => Results.Redirect("/scalar/v1", permanent: true)).AllowAnonymous();
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

if (builder.Configuration.GetValue("Security:Https:Enforce", !app.Environment.IsDevelopment()))
{
    app.UseHttpsRedirection();
}

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseRateLimiter();
app.UseOutputCache();
app.UseCors("DefaultApi");
app.UseSecurityHeaders();

// Middleware de tratamento global de exceções (deve estar no início)
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Agent token auth middleware (para rotas /api/agent-auth/*)
app.UseAgentAuth();

// API key e JWT user auth (para todos os demais endpoints)
app.UseApiTokenAuth();
app.UseUserAuth();

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapHub<AgentHub>("/hubs/agent", options =>
{
    options.AllowStatefulReconnects = true;
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
}).RequireCors("SignalR");

app.MapHub<NotificationHub>("/hubs/notifications", options =>
{
    options.AllowStatefulReconnects = true;
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
}).RequireCors("SignalR");

app.MapHub<RemoteDebugHub>("/hubs/remote-debug", options =>
{
    options.AllowStatefulReconnects = true;
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
}).RequireCors("SignalR");

app.Run();
