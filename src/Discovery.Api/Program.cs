using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.AspNetCore;
using Discovery.Api;
using Discovery.Api.Cqrs.DependencyInjection;
using Discovery.Api.DependencyInjection;
using Discovery.Api.Filters;
using Discovery.Api.Validators;
using FluentMigrator.Runner;
using Discovery.Api.Middleware;
using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Messaging;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Discovery.Infrastructure.Services.Remote;
using Discovery.Infrastructure.Services.Remote.Audit;
using Discovery.Infrastructure.Services.Remote.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
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
builder.Services.AddSingleton<SpecialCommandPayloadValidator>();
builder.Services.AddSingleton<DashboardEventContractNormalizer>();
builder.Services.AddSingleton<IRemoteDebugSessionManager, RemoteDebugSessionManager>();
builder.Services.AddScoped<IAgentTransferService, AgentTransferService>();
builder.Services.AddSingleton(TimeProvider.System);

// Static catalog of report datasets (singleton — content is fixed)
builder.Services.AddSingleton<IReportDatasetCatalogProvider, ReportDatasetCatalogProvider>();

// Remote access (acesso remoto nativo)
builder.Services.AddScoped<IRemoteSessionManager, RemoteSessionManager>();
builder.Services.AddScoped<IRemoteSessionRepository, RemoteSessionRepository>();
builder.Services.AddScoped<IRemoteSessionAuditRepository, RemoteSessionAuditRepository>();
builder.Services.AddScoped<IRemoteRecordingService, RemoteRecordingService>();
builder.Services.AddScoped<RemoteSessionAuditService>();
builder.Services.AddScoped<WebrtcTurnCredentialIssuer>();
builder.Services.AddScoped<RemoteSessionJwtIssuer>();
builder.Services.AddScoped<RemoteSessionDispatcher>();
builder.Services.AddHostedService<RemoteSessionExpirationService>();
builder.Services.AddHostedService<RecordingAssemblerService>();
builder.Services.AddSingleton<Discovery.Infrastructure.Services.Remote.SessionMetricsStore>();
builder.Services.AddHostedService<Discovery.Infrastructure.Services.Remote.AdaptiveQualityService>();
builder.Services.Configure<RemoteAccessOptions>(builder.Configuration.GetSection("RemoteAccess"));

// Scoped scope context (cache de escopo intra-request para queries filtradas)
builder.Services.AddScoped<Discovery.Core.Interfaces.Auth.IScopeContext, Discovery.Infrastructure.Services.ScopeContext>();

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
// Parser de manifests YAML do winget-pkgs (classe concreta, sem interface — não é pego pelo auto-scan)
builder.Services.AddSingleton<WingetManifestParser>();
// Sync de catálogo em background (singleton: mantém estado de jobs/último resultado entre requests)
builder.Services.AddSingleton<AppCatalogBackgroundSyncService>();

builder.Services.AddHttpClient("AiChat", client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Generic HttpClient for other services
builder.Services.AddHttpClient();
builder.Services.AddDiscoveryOpenTelemetry(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<IAgentTlsCertificateProbe, AgentTlsCertificateProbe>();
var isDevelopment = builder.Environment.IsDevelopment();

var backgroundServicesConfig = BackgroundServicesCollectionExtensions.ReadBackgroundServicesConfig(builder.Configuration, isDevelopment);

// AI Chat & MCP
builder.Services.AddSingleton<ILlmProvider, OpenAiProvider>();
builder.Services.AddScoped<IMcpToolExecutor, McpToolExecutor>();
builder.Services.AddScoped<IAiCostControlService, AiCostControlService>();
// Sub-services internos do chat IA
builder.Services.AddScoped<AiChatSettingsResolver>();
builder.Services.AddScoped<AiChatSystemPromptBuilder>();
builder.Services.AddScoped<AiChatToolOrchestrator>();
builder.Services.AddScoped<AiChatQuickReply>();
builder.Services.AddScoped<AiChatStreamingOrchestrator>();

// Register built-in MCP tool handlers after DI is built (handled at first use via McpToolExecutor constructor)


builder.Services.AddDiscoveryBackgroundServices(backgroundServicesConfig);

builder.Services.Configure<AutoTicketOptions>(
    builder.Configuration.GetSection(AutoTicketOptions.SectionName));
builder.Services.Configure<SecretEncryptionOptions>(
    builder.Configuration.GetSection(SecretEncryptionOptions.SectionName));

// P2p Discovery options (mantido para outras opções)
builder.Services.Configure<P2pOptions>(
    builder.Configuration.GetSection(P2pOptions.SectionName));

// IMemoryCache (para ConfigurationResolver)
builder.Services.AddMemoryCache();

// Configuração de logging automático
builder.Services.Configure<AutomaticLoggingOptions>(
    builder.Configuration.GetSection("AutomaticLogging"));

// Configuração de reporting
builder.Services.Configure<ReportingOptions>(
    builder.Configuration.GetSection("Reporting"));

builder.Services.Configure<RealtimeContractOptions>(
    builder.Configuration.GetSection(RealtimeContractOptions.SectionName));

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
        // Serializa todas as propriedades em camelCase (ex: Items → items, NextCursor → nextCursor)
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        // Permite que a API aceite JSON em camelCase ou PascalCase
        opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        opts.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // Previne recursão infinita em navigation properties cíclicas (ex: Article ↔ Chunks)
        opts.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Discovery.Api.Validators.CreateTicketCommandValidator>();

// OpenAPI + Scalar
builder.Services.AddOpenApi();
builder.Services.AddDiscoveryApiVersioning();

// CQRS infrastructure (MediatR, pipeline behaviors)
builder.Services.AddDiscoveryCqrs();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // Default trusted proxies: Cloudflare IPv4/IPv6 ranges + localhost for local nginx dev
    var defaultTrustedProxies = new[]
    {
        // Cloudflare IPv4 ranges (https://www.cloudflare.com/ips/)
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        // Cloudflare IPv6 ranges
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32",
        "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32",
        // Localhost for local nginx reverse proxy
        "127.0.0.1", "::1"
    };

    var trustedProxies = builder.Configuration.GetSection("Security:TrustedProxies").Get<string[]>() ?? [];
    var trustedNetworks = builder.Configuration.GetSection("Security:TrustedProxyNetworks").Get<string[]>() ?? [];

    // Merge defaults with config (config takes precedence if explicitly set)
    var allProxies = trustedProxies.Length > 0 ? trustedProxies : defaultTrustedProxies;
    var allNetworks = trustedNetworks.Length > 0 ? trustedNetworks : defaultTrustedProxies.Where(p => p.Contains('/')).ToArray();

    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();

    foreach (var ip in allProxies.Where(p => !p.Contains('/')).Distinct())
    {
        if (System.Net.IPAddress.TryParse(ip, out var parsed))
            options.KnownProxies.Add(parsed);
    }
    foreach (var cidr in allNetworks.Distinct())
    {
        var parts = cidr.Split('/');
        if (parts.Length == 2
            && System.Net.IPAddress.TryParse(parts[0], out var netAddr)
            && int.TryParse(parts[1], out var prefix)
            && prefix >= 0 && prefix <= 128)
        {
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(netAddr, prefix));
        }
    }
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

// Run migrations on startup (config-gated, defaults to true for backward compatibility)
var runMigrationsOnStartup = builder.Configuration.GetValue("Migrations:RunOnStartup", true);
if (runMigrationsOnStartup)
{
    using (var scope = app.Services.CreateScope())
    {
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }

    app.Logger.LogInformation("Migrations completed successfully at startup.");
}
else
{
    app.Logger.LogInformation("Migrations:RunOnStartup is false; skipping automatic migrations.");
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
app.Run();
