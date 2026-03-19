using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.AspNetCore;
using Meduza.Api.Filters;
using Meduza.Api.Validators;
using FluentMigrator.Runner;
using Meduza.Api.Hubs;
using Meduza.Api.Middleware;
using Meduza.Api.Services;
using Meduza.Api.DependencyInjection;
using Meduza.Core.Configuration;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Auth;
using Meduza.Core.Interfaces.Identity;
using Meduza.Core.Interfaces.Security;
using Meduza.Infrastructure.Data;
using Meduza.Infrastructure.Messaging;
using Meduza.Infrastructure.Repositories;
using Meduza.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using NATS.Client.Core;
using Scalar.AspNetCore;
using StackExchange.Redis;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var agentHostProfile = ResolveActiveAgentProfile(builder.Configuration);
var agentInstallerTarget = ResolveAgentPackageSetting(builder.Configuration, agentHostProfile, "InstallerTargetPlatform") ?? "windows/amd64";
if (!string.Equals(agentInstallerTarget, "windows/amd64", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException($"AgentPackage installer target must be windows/amd64. Resolved value: {agentInstallerTarget}");
}

ValidateRequiredAgentPackageSetting(builder.Configuration, agentHostProfile, "DiscoveryProjectPath");
ValidateRequiredAgentPackageSetting(builder.Configuration, agentHostProfile, "BinaryPath");
ValidateRequiredAgentPackageSetting(builder.Configuration, agentHostProfile, "PublicApiServer");

var databaseProvider = builder.Configuration.GetValue<string>("Database:Provider") ?? "Postgres";
var isSqlite = databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);

if (isSqlite)
{
    throw new InvalidOperationException("SQLite is no longer supported during the EF Core migration. Configure Database:Provider=Postgres.");
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<MeduzaDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure();
        npgsqlOptions.UseVector();
    }));

// Auto-registered DI services (repositories + domain services)
// Most services in Meduza follow a 1:1 interface-to-implementation pattern.
// The helper below scans Meduza.Infrastructure and registers any interface that has exactly one concrete implementation.
var autoRegisteredServices = builder.Services.AddMeduzaAutoRegisteredServices();

// Special registrations (singleton/hosted services, multi-implementation patterns)
builder.Services.AddSingleton<ISyncPingDispatchQueue, SyncPingDispatchBackgroundService>();
builder.Services.AddHostedService(sp => (SyncPingDispatchBackgroundService)sp.GetRequiredService<ISyncPingDispatchQueue>());

// Multi-implementation services (explicitly registered)
builder.Services.AddScoped<IReportRenderer, XlsxReportRenderer>();
builder.Services.AddScoped<IReportRenderer, CsvReportRenderer>();

// Implemented in Meduza.Api (outside Meduza.Infrastructure auto-scan)
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ISyncInvalidationPublisher, SyncInvalidationPublisher>();
builder.Services.AddScoped<MeshCentralIdentitySyncTriggerService>();

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

var enableKnowledgeEmbeddingBackgroundService = builder.Configuration.GetValue<bool?>("BackgroundJobs:KnowledgeEmbeddingEnabled") ?? true;
var enableSlaMonitoringBackgroundService = builder.Configuration.GetValue<bool?>("BackgroundJobs:SlaMonitoringEnabled") ?? true;
var enableReportGenerationBackgroundService = builder.Configuration.GetValue<bool?>("BackgroundJobs:ReportGenerationEnabled") ?? true;
var enableAgentLabelingReconciliationBackgroundService = builder.Configuration.GetValue<bool?>("BackgroundJobs:AgentLabelingReconciliationEnabled") ?? true;
var enableMeshCentralIdentityReconciliationBackgroundService = builder.Configuration.GetValue<bool?>("BackgroundJobs:MeshCentralIdentityReconciliationEnabled") ?? true;
var enableMeshCentralGroupPolicyReconciliationBackgroundService = builder.Configuration.GetValue<bool?>("BackgroundJobs:MeshCentralGroupPolicyReconciliationEnabled") ?? true;
var isDevelopment = builder.Environment.IsDevelopment();
var allowedCorsOrigins = builder.Configuration.GetSection("Security:Cors:AllowedOrigins").Get<string[]>() ?? [];
var allowWildcardCorsInDevelopment = builder.Configuration.GetValue("Security:Cors:AllowWildcardInDevelopment", true);

if (!isDevelopment && allowedCorsOrigins.Length == 0)
{
    Console.WriteLine("WARNING: Security:Cors:AllowedOrigins is empty in non-development environment. Cross-origin requests will be blocked.");
}

var generalRateLimitPermit = Math.Max(1, builder.Configuration.GetValue<int?>("Security:RateLimiting:General:PermitLimit") ?? 240);
var generalRateLimitWindowSeconds = Math.Max(1, builder.Configuration.GetValue<int?>("Security:RateLimiting:General:WindowSeconds") ?? 60);
var generalRateLimitQueueLimit = Math.Max(0, builder.Configuration.GetValue<int?>("Security:RateLimiting:General:QueueLimit") ?? 0);

var authRateLimitPermit = Math.Max(1, builder.Configuration.GetValue<int?>("Security:RateLimiting:Auth:PermitLimit") ?? 20);
var authRateLimitWindowSeconds = Math.Max(1, builder.Configuration.GetValue<int?>("Security:RateLimiting:Auth:WindowSeconds") ?? 60);
var authRateLimitQueueLimit = Math.Max(0, builder.Configuration.GetValue<int?>("Security:RateLimiting:Auth:QueueLimit") ?? 0);

var agentRateLimitPermit = Math.Max(1, builder.Configuration.GetValue<int?>("Security:RateLimiting:Agent:PermitLimit") ?? 600);
var agentRateLimitWindowSeconds = Math.Max(1, builder.Configuration.GetValue<int?>("Security:RateLimiting:Agent:WindowSeconds") ?? 60);
var agentRateLimitQueueLimit = Math.Max(0, builder.Configuration.GetValue<int?>("Security:RateLimiting:Agent:QueueLimit") ?? 0);

// AI Chat & MCP (auto-registered via AddMeduzaAutoRegisteredServices)
// Only the LLM provider is explicitly registered as singleton.
builder.Services.AddSingleton<ILlmProvider, OpenAiProvider>();

// Knowledge Base (auto-registered via AddMeduzaAutoRegisteredServices)
if (enableKnowledgeEmbeddingBackgroundService)
{
    builder.Services.AddHostedService<KnowledgeEmbeddingBackgroundService>();
}

builder.Services.Configure<MeshCentralOptions>(
    builder.Configuration.GetSection("MeshCentral"));
builder.Services.Configure<SecretEncryptionOptions>(
    builder.Configuration.GetSection(SecretEncryptionOptions.SectionName));

// IMemoryCache (para ConfigurationResolver)
builder.Services.AddMemoryCache();

// Configuração de logging automático
builder.Services.Configure<AutomaticLoggingOptions>(
    builder.Configuration.GetSection("AutomaticLogging"));

// Configuração de reporting
builder.Services.Configure<ReportingOptions>(
    builder.Configuration.GetSection("Reporting"));

// NATS
var natsUrl = builder.Configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";
builder.Services.AddSingleton(_ => new NatsConnection(new NatsOpts { Url = natsUrl }));
builder.Services.AddHostedService<NatsBackgroundService>();
builder.Services.AddHostedService<NatsSignalRBridge>();

// Redis
var redisConnString = builder.Configuration.GetValue<string>("Redis:Connection") ?? "127.0.0.1:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(redisConnString);
    var redisPassword = builder.Configuration.GetValue<string>("Redis:Password");
    if (!string.IsNullOrWhiteSpace(redisPassword))
    {
        options.Password = redisPassword;
    }

    options.AbortOnConnectFail = false;
    options.ConnectTimeout = 5000;
    options.AsyncTimeout = 5000;
    options.SyncTimeout = 5000;
    options.ConnectRetry = 5;
    options.KeepAlive = 30;
    options.ReconnectRetryPolicy = new ExponentialRetry(5000);
    options.ClientName = "meduza-api";
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddSingleton<IRedisService, RedisService>();
builder.Services.AddHostedService<LogPurgeBackgroundService>();
if (enableSlaMonitoringBackgroundService)
{
    builder.Services.AddHostedService<SlaMonitoringBackgroundService>();
}

if (enableReportGenerationBackgroundService)
{
    builder.Services.AddHostedService<ReportGenerationBackgroundService>();
}

if (!isDevelopment)
{
    builder.Services.AddHostedService<ReportRetentionBackgroundService>();
    builder.Services.AddHostedService<AiChatRetentionBackgroundService>();
    if (enableAgentLabelingReconciliationBackgroundService)
    {
        builder.Services.AddHostedService<AgentLabelingReconciliationBackgroundService>();
    }

    if (enableMeshCentralIdentityReconciliationBackgroundService)
    {
        builder.Services.AddHostedService<MeshCentralIdentityReconciliationBackgroundService>();
    }

    if (enableMeshCentralGroupPolicyReconciliationBackgroundService)
    {
        builder.Services.AddHostedService<MeshCentralGroupPolicyReconciliationBackgroundService>();
    }
}

builder.Services.AddHostedService<AgentPackagePrebuildHostedService>();

// ── Identity & Auth ───────────────────────────────────────────────────────
// Scoped repos/services above are auto-registered via AddMeduzaAutoRegisteredServices.
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

// SignalR for dashboard real-time
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.HandshakeTimeout = TimeSpan.FromSeconds(15);
});

// OpenAPI + Scalar
builder.Services.AddOpenApi();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        if (context.HttpContext.Response.HasStarted)
            return;

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests. Try again later." },
            cancellationToken: token);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var ip = ResolveClientIp(httpContext);
        var path = httpContext.Request.Path;

        if (path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/agent-install", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/mfa", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"auth:{ip}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authRateLimitPermit,
                    Window = TimeSpan.FromSeconds(authRateLimitWindowSeconds),
                    QueueLimit = authRateLimitQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
        }

        if (path.StartsWithSegments("/api/agent-auth", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: $"agent:{ip}",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = agentRateLimitPermit,
                    Window = TimeSpan.FromSeconds(agentRateLimitWindowSeconds),
                    QueueLimit = agentRateLimitQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"general:{ip}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = generalRateLimitPermit,
                Window = TimeSpan.FromSeconds(generalRateLimitWindowSeconds),
                QueueLimit = generalRateLimitQueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
    });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultApi", policy =>
        ApplyApiCorsPolicy(policy, isDevelopment, allowWildcardCorsInDevelopment, allowedCorsOrigins));

    options.AddPolicy("SignalR", policy =>
        ApplySignalRCorsPolicy(policy, isDevelopment, allowWildcardCorsInDevelopment, allowedCorsOrigins));
});

// FluentMigrator
builder.Services.AddFluentMigratorCore()
    .ConfigureRunner(rb =>
    {
        rb.AddPostgres().WithGlobalConnectionString(connectionString);
        rb.ScanIn(typeof(Meduza.Migrations.Migrations.M001_CreateClients).Assembly).For.Migrations();
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

// Run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    runner.MigrateUp();
}

// Seed default workflow states
await DatabaseSeeder.SeedAsync(app.Services);

// Configure the HTTP request pipeline
var openApiEnabled = builder.Configuration.GetValue("OpenApi:Enabled", app.Environment.IsDevelopment());
if (openApiEnabled)
{
    app.MapOpenApi();
    app.MapScalarApiReference();
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

app.UseRateLimiter();
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

app.Run();

static string ResolveActiveAgentProfile(IConfiguration configuration)
{
    var configured = configuration["AgentPackage:ActiveProfile"];
    if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        return OperatingSystem.IsWindows() ? "windows" : "linux";

    return configured.Trim().ToLowerInvariant();
}

static string? ResolveAgentPackageSetting(IConfiguration configuration, string profile, string key)
{
    var profileValue = configuration[$"AgentPackage:Profiles:{profile}:{key}"];
    if (!string.IsNullOrWhiteSpace(profileValue))
        return profileValue;

    return configuration[$"AgentPackage:{key}"];
}

static void ValidateRequiredAgentPackageSetting(IConfiguration configuration, string profile, string key)
{
    var value = ResolveAgentPackageSetting(configuration, profile, key);
    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"Missing AgentPackage setting: {key} (profile: {profile}).");
}

static string ResolveClientIp(HttpContext context)
{
    var cfConnectingIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(cfConnectingIp))
        return cfConnectingIp;

    var xForwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(xForwardedFor))
    {
        var firstIp = xForwardedFor.Split(',')[0].Trim();
        if (!string.IsNullOrWhiteSpace(firstIp))
            return firstIp;
    }

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static void ApplyApiCorsPolicy(
    CorsPolicyBuilder policy,
    bool isDevelopment,
    bool allowWildcardCorsInDevelopment,
    string[] allowedOrigins)
{
    if (isDevelopment && allowWildcardCorsInDevelopment)
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        return;
    }

    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader();
    }
}

static void ApplySignalRCorsPolicy(
    CorsPolicyBuilder policy,
    bool isDevelopment,
    bool allowWildcardCorsInDevelopment,
    string[] allowedOrigins)
{
    if (isDevelopment && allowWildcardCorsInDevelopment)
    {
        policy.AllowAnyMethod().AllowAnyHeader().SetIsOriginAllowed(_ => true).AllowCredentials();
        return;
    }

    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
    }
}
