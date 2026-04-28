using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.AspNetCore;
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
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using NATS.Client.Core;
using Scalar.AspNetCore;
using StackExchange.Redis;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using Discovery.Core.Entities.Identity;

var hasMaintenanceMode = TryParseMaintenanceOptions(args, out var maintenanceOptions, out var parseError);
if (hasMaintenanceMode && !string.IsNullOrWhiteSpace(parseError))
{
    Console.Error.WriteLine(parseError);
    Environment.ExitCode = 2;
    return;
}

if (hasMaintenanceMode && maintenanceOptions.ShowHelp)
{
    PrintMaintenanceHelp();
    return;
}

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
builder.Services.AddSingleton<ISyncPingDispatchQueue, SyncPingDispatchBackgroundService>();
builder.Services.AddHostedService(sp => (SyncPingDispatchBackgroundService)sp.GetRequiredService<ISyncPingDispatchQueue>());

// Multi-implementation services (explicitly registered)
builder.Services.AddScoped<IReportRenderer, XlsxReportRenderer>();
builder.Services.AddScoped<IReportRenderer, CsvReportRenderer>();

// Implemented in Discovery.Api (outside Discovery.Infrastructure auto-scan)
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IAgentCommandDispatcher, AgentCommandDispatcher>();
builder.Services.AddScoped<ISyncInvalidationPublisher, SyncInvalidationPublisher>();
builder.Services.AddScoped<MeshCentralIdentitySyncTriggerService>();
builder.Services.AddSingleton<IRemoteDebugSessionManager, RemoteDebugSessionManager>();
builder.Services.AddSingleton<IRemoteDebugLogRelay, RemoteDebugLogRelayService>();

// Agent Alerts (PSADT)
builder.Services.AddScoped<AlertDispatchService>();
builder.Services.AddHostedService<AlertSchedulerBackgroundService>();

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

var enableKnowledgeEmbeddingBackgroundService = builder.Configuration.GetValue<bool?>("BackgroundJobs:KnowledgeEmbeddingEnabled") ?? true;
var enableKnowledgeEmbeddingQueueBackgroundService = builder.Configuration.GetValue<bool?>("BackgroundJobs:KnowledgeEmbeddingQueueEnabled") ?? false;
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

// AI Chat & MCP (auto-registered via AddDiscoveryAutoRegisteredServices)
// Only the LLM provider is explicitly registered as singleton.
builder.Services.AddSingleton<ILlmProvider, OpenAiProvider>();

// Knowledge Base (auto-registered via AddDiscoveryAutoRegisteredServices)
if (enableKnowledgeEmbeddingBackgroundService)
{
    builder.Services.AddHostedService<KnowledgeEmbeddingBackgroundService>();
}

if (enableKnowledgeEmbeddingQueueBackgroundService)
{
    builder.Services.AddHostedService<KnowledgeEmbeddingQueueBackgroundService>();
}

builder.Services.Configure<MeshCentralOptions>(
    builder.Configuration.GetSection("MeshCentral"));
builder.Services.Configure<AutoTicketOptions>(
    builder.Configuration.GetSection(AutoTicketOptions.SectionName));
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
// Quando auth callout está habilitado no servidor NATS, a API precisa conectar
// com o usuário configurado em auth_users (ex: "auth") para bypassar o próprio callout.
var natsUrl = builder.Configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";
var natsAuthUser = builder.Configuration.GetValue<string>("Nats:AuthUser");
var natsAuthPassword = builder.Configuration.GetValue<string>("Nats:AuthPassword");

builder.Services.AddSingleton(_ =>
{
    var opts = new NatsOpts { Url = natsUrl };

    if (!string.IsNullOrWhiteSpace(natsAuthUser) && !string.IsNullOrWhiteSpace(natsAuthPassword))
        opts = opts with { AuthOpts = new NatsAuthOpts { Username = natsAuthUser, Password = natsAuthPassword } };

    return new NatsConnection(opts);
});
builder.Services.AddHostedService<NatsBackgroundService>();
builder.Services.AddHostedService<NatsSignalRBridge>();
builder.Services.AddHostedService<RemoteDebugNatsBridgeService>();
builder.Services.AddHostedService<RemoteDebugSessionCleanupService>();
builder.Services.AddSingleton<IAiChatJobQueue, AiChatJobBackgroundService>();
builder.Services.AddHostedService(sp => (AiChatJobBackgroundService)sp.GetRequiredService<IAiChatJobQueue>());
builder.Services.AddSingleton<INatsAuthCalloutReloadSignal, NatsAuthCalloutReloadSignal>();
builder.Services.AddHostedService<NatsAuthCalloutBackgroundService>();

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
    options.ClientName = "discovery-api";
    return ConnectionMultiplexer.Connect(options);
});
builder.Services.AddSingleton<IRedisService, RedisService>();
builder.Services.AddHostedService<LogPurgeBackgroundService>();
builder.Services.AddHostedService<P2pMaintenanceBackgroundService>();
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
    var maintenanceExitCode = await ExecuteMaintenanceAsync(app.Services, maintenanceOptions);
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

// Configure the HTTP request pipeline
var openApiEnabled = builder.Configuration.GetValue("OpenApi:Enabled", app.Environment.IsDevelopment());
var scalarEnabled = openApiEnabled && builder.Configuration.GetValue("OpenApi:Scalar:Enabled", true);
if (openApiEnabled)
{
    app.MapOpenApi().AllowAnonymous();
    app.MapGet("/api/openapi/{**rest}", (string? rest) =>
    {
        var suffix = string.IsNullOrWhiteSpace(rest) ? "v1.json" : rest.TrimStart('/');
        return Results.Redirect($"/openapi/{suffix}");
    }).AllowAnonymous();
}

if (scalarEnabled)
{
    app.MapScalarApiReference().AllowAnonymous();
    app.MapGet("/api/scalar/{**rest}", (string? rest) =>
    {
        var suffix = string.IsNullOrWhiteSpace(rest) ? string.Empty : rest.TrimStart('/');
        var target = string.IsNullOrWhiteSpace(suffix) ? "/scalar/" : $"/scalar/{suffix}";
        return Results.Redirect(target);
    }).AllowAnonymous();
    app.MapGet("/scalar/openapi/{**rest}", (string? rest) =>
    {
        var suffix = string.IsNullOrWhiteSpace(rest) ? "v1.json" : rest.TrimStart('/');
        return Results.Redirect($"/openapi/{suffix}");
    }).AllowAnonymous();
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

app.MapHub<RemoteDebugHub>("/hubs/remote-debug", options =>
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

static bool TryParseMaintenanceOptions(string[] args, out MaintenanceOptions options, out string? error)
{
    options = MaintenanceOptions.Default;
    error = null;

    if (!args.Any(a => string.Equals(a, "--recover-admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(a, "--recover-admin-help", StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    options = options with { RecoverAdmin = true };

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (string.Equals(arg, "--recover-admin-help", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(arg, "--recover-admin", StringComparison.OrdinalIgnoreCase)
                && args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase))))
        {
            options = options with { ShowHelp = true };
            continue;
        }

        if (!string.Equals(arg, "--login", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--password", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--password-stdin", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--create-if-missing", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--no-create-if-missing", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--reset-mfa", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--keep-mfa", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--reactivate", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--no-reactivate", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--recover-admin", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (string.Equals(arg, "--login", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                error = "Missing value for --login.";
                return true;
            }

            options = options with { Login = args[++i] };
            continue;
        }

        if (string.Equals(arg, "--password", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                error = "Missing value for --password.";
                return true;
            }

            options = options with { Password = args[++i] };
            continue;
        }

        if (string.Equals(arg, "--password-stdin", StringComparison.OrdinalIgnoreCase))
        {
            options = options with { PasswordFromStdin = true };
            continue;
        }

        if (string.Equals(arg, "--create-if-missing", StringComparison.OrdinalIgnoreCase))
        {
            options = options with { CreateIfMissing = true };
            continue;
        }

        if (string.Equals(arg, "--no-create-if-missing", StringComparison.OrdinalIgnoreCase))
        {
            options = options with { CreateIfMissing = false };
            continue;
        }

        if (string.Equals(arg, "--reset-mfa", StringComparison.OrdinalIgnoreCase))
        {
            options = options with { ResetMfa = true };
            continue;
        }

        if (string.Equals(arg, "--keep-mfa", StringComparison.OrdinalIgnoreCase))
        {
            options = options with { ResetMfa = false };
            continue;
        }

        if (string.Equals(arg, "--reactivate", StringComparison.OrdinalIgnoreCase))
        {
            options = options with { ReactivateUser = true };
            continue;
        }

        if (string.Equals(arg, "--no-reactivate", StringComparison.OrdinalIgnoreCase))
        {
            options = options with { ReactivateUser = false };
        }
    }

    if (!string.IsNullOrWhiteSpace(options.Password) && options.PasswordFromStdin)
    {
        error = "Use either --password or --password-stdin, not both.";
        return true;
    }

    if (string.IsNullOrWhiteSpace(options.Login))
    {
        error = "Login cannot be empty.";
        return true;
    }

    return true;
}

static async Task<int> ExecuteMaintenanceAsync(IServiceProvider services, MaintenanceOptions options)
{
    if (!options.RecoverAdmin)
        return 0;

    using var scope = services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    var mfaKeys = scope.ServiceProvider.GetRequiredService<IUserMfaKeyRepository>();
    var sessions = scope.ServiceProvider.GetRequiredService<IUserSessionRepository>();
    var passwordService = scope.ServiceProvider.GetRequiredService<IPasswordService>();
    var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

    var password = ResolveRecoveryPassword(options);
    if (password is null)
    {
        Console.Error.WriteLine("Failed to read password from stdin.");
        return 2;
    }

    var (policyValid, policyReason) = passwordService.ValidatePolicy(password);
    if (!policyValid)
    {
        Console.Error.WriteLine($"Password policy failed: {policyReason}");
        return 2;
    }

    var login = options.Login.Trim();
    var user = await users.GetByLoginAsync(login);
    var created = false;

    if (user is null)
    {
        if (!options.CreateIfMissing)
        {
            Console.Error.WriteLine($"User '{login}' not found. Use --create-if-missing to bootstrap it.");
            return 1;
        }

        var salt = passwordService.GenerateSalt();
        var hash = passwordService.HashPassword(password, salt);
        var now = DateTime.UtcNow;

        user = new User
        {
            Id = Guid.NewGuid(),
            Login = login,
            Email = string.Equals(login, "admin", StringComparison.OrdinalIgnoreCase)
                ? "admin@local.discovery"
                : $"{login}@local.discovery",
            FullName = "Administrador Recuperado",
            PasswordHash = hash,
            PasswordSalt = salt,
            IsActive = true,
            MfaRequired = true,
            MfaConfigured = false,
            MustChangePassword = true,
            MustChangeProfile = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        user = await users.CreateAsync(user);
        created = true;
    }

    if (!created)
    {
        var salt = passwordService.GenerateSalt();
        var hash = passwordService.HashPassword(password, salt);

        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.MfaRequired = true;
        user.MustChangePassword = true;
        if (options.ReactivateUser)
            user.IsActive = true;

        if (options.ResetMfa)
            user.MfaConfigured = false;

        user.UpdatedAt = DateTime.UtcNow;
        user = await users.UpdateAsync(user);
    }

    if (options.ResetMfa)
        await mfaKeys.DeactivateAllByUserIdAsync(user.Id);

    await sessions.RevokeAllByUserIdAsync(user.Id);
    await EnsureAdminBindingAsync(db, user.Id);

    Console.WriteLine("Admin recovery completed.");
    Console.WriteLine($"Login: {user.Login}");
    Console.WriteLine($"User created: {(created ? "yes" : "no")}");
    Console.WriteLine($"MFA reset: {(options.ResetMfa ? "yes" : "no")}");
    Console.WriteLine("All active sessions for this user were revoked.");

    if (options.PasswordFromStdin || !string.IsNullOrWhiteSpace(options.Password))
    {
        Console.WriteLine("Password source: provided by operator (not echoed).\n");
    }
    else
    {
        Console.WriteLine($"Temporary password: {password}");
        Console.WriteLine("Store it safely. The user must change password on next login.");
    }

    return 0;
}

static async Task EnsureAdminBindingAsync(DiscoveryDbContext db, Guid userId)
{
    const string adminGroupName = "Administradores";
    const string adminRoleName = "Admin";

    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO user_groups (id, name, description, is_active, created_at, updated_at)
        SELECT gen_random_uuid(), {adminGroupName}, 'Grupo administrativo inicial do sistema', true, NOW(), NOW()
        WHERE NOT EXISTS (
            SELECT 1 FROM user_groups WHERE name = {adminGroupName}
        );
    """);

    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO user_group_memberships (user_id, group_id, joined_at)
        SELECT {userId}, g.id, NOW()
        FROM user_groups g
        WHERE g.name = {adminGroupName}
          AND NOT EXISTS (
              SELECT 1
              FROM user_group_memberships ugm
              WHERE ugm.user_id = {userId}
                AND ugm.group_id = g.id
          );
    """);

    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO user_group_roles (id, group_id, role_id, scope_level, scope_id, assigned_at)
        SELECT gen_random_uuid(), g.id, r.id, 'Global', NULL, NOW()
        FROM user_groups g
        CROSS JOIN roles r
        WHERE g.name = {adminGroupName}
          AND r.name = {adminRoleName}
          AND NOT EXISTS (
              SELECT 1
              FROM user_group_roles ugr
              WHERE ugr.group_id = g.id
                AND ugr.role_id = r.id
                AND ugr.scope_level = 'Global'
                AND ugr.scope_id IS NULL
          );
    """);
}

static string? ResolveRecoveryPassword(MaintenanceOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.Password))
        return options.Password;

    if (options.PasswordFromStdin)
    {
        var line = Console.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    return GenerateTemporaryPassword();
}

static string GenerateTemporaryPassword()
{
    const string uppercase = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    const string lowercase = "abcdefghijkmnopqrstuvwxyz";
    const string digits = "23456789";
    const string specials = "!@#$%*-_";
    var all = uppercase + lowercase + digits + specials;

    var chars = new List<char>
    {
        uppercase[RandomNumberGenerator.GetInt32(uppercase.Length)],
        lowercase[RandomNumberGenerator.GetInt32(lowercase.Length)],
        digits[RandomNumberGenerator.GetInt32(digits.Length)],
        specials[RandomNumberGenerator.GetInt32(specials.Length)]
    };

    for (var i = chars.Count; i < 20; i++)
        chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

    for (var i = chars.Count - 1; i > 0; i--)
    {
        var j = RandomNumberGenerator.GetInt32(i + 1);
        (chars[i], chars[j]) = (chars[j], chars[i]);
    }

    return new string(chars.ToArray());
}

static void PrintMaintenanceHelp()
{
    Console.WriteLine("Discovery API maintenance command: --recover-admin");
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/Discovery.Api -- --recover-admin [options]");
    Console.WriteLine("Options:");
    Console.WriteLine("  --login <value>             Target login (default: admin)");
    Console.WriteLine("  --password <value>          Explicit password (less secure: appears in history)");
    Console.WriteLine("  --password-stdin            Read password from standard input");
    Console.WriteLine("  --create-if-missing         Create the admin account if it does not exist (default)");
    Console.WriteLine("  --no-create-if-missing      Fail if login does not exist");
    Console.WriteLine("  --reset-mfa                 Deactivate MFA keys and require new enrollment (default)");
    Console.WriteLine("  --keep-mfa                  Keep current MFA keys");
    Console.WriteLine("  --reactivate                Re-enable user if inactive (default)");
    Console.WriteLine("  --no-reactivate             Keep current IsActive status");
    Console.WriteLine("  --recover-admin-help        Show this help");
}

file record MaintenanceOptions(
    bool RecoverAdmin,
    bool ShowHelp,
    string Login,
    string? Password,
    bool PasswordFromStdin,
    bool CreateIfMissing,
    bool ResetMfa,
    bool ReactivateUser)
{
    public static MaintenanceOptions Default => new(
        RecoverAdmin: false,
        ShowHelp: false,
        Login: "admin",
        Password: null,
        PasswordFromStdin: false,
        CreateIfMissing: true,
        ResetMfa: true,
        ReactivateUser: true);
}
