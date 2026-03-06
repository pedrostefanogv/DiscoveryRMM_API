using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.AspNetCore;
using Meduza.Api.Filters;
using Meduza.Api.Validators;
using FluentMigrator.Runner;
using Meduza.Api.Hubs;
using Meduza.Api.Middleware;
using Meduza.Api.Services;
using Meduza.Core.Configuration;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Meduza.Infrastructure.Messaging;
using Meduza.Infrastructure.Repositories;
using Meduza.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using NATS.Client.Core;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var databaseProvider = builder.Configuration.GetValue<string>("Database:Provider") ?? "Postgres";
var isSqlite = databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);

if (isSqlite)
{
    throw new InvalidOperationException("SQLite is no longer supported during the EF Core migration. Configure Database:Provider=Postgres.");
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<MeduzaDbContext>(options =>
    options.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.EnableRetryOnFailure()));

// Repositories
builder.Services.AddScoped<IClientRepository, ClientRepository>();
builder.Services.AddScoped<ISiteRepository, SiteRepository>();
builder.Services.AddScoped<IAgentRepository, AgentRepository>();
builder.Services.AddScoped<IAgentHardwareRepository, AgentHardwareRepository>();
builder.Services.AddScoped<IAgentSoftwareRepository, AgentSoftwareRepository>();
builder.Services.AddScoped<ICommandRepository, CommandRepository>();
builder.Services.AddScoped<ITicketRepository, TicketRepository>();
builder.Services.AddScoped<IAgentTokenRepository, AgentTokenRepository>();
builder.Services.AddScoped<IDeployTokenRepository, DeployTokenRepository>();
builder.Services.AddScoped<IWorkflowRepository, WorkflowRepository>();
builder.Services.AddScoped<ILogRepository, LogRepository>();
builder.Services.AddScoped<IEntityNoteRepository, EntityNoteRepository>();

// New repositories for tickets enhancement (Departments, WorkflowProfiles, ActivityLogs)
builder.Services.AddScoped<IDepartmentRepository, DepartmentRepository>();
builder.Services.AddScoped<IWorkflowProfileRepository, WorkflowProfileRepository>();
builder.Services.AddScoped<ITicketActivityLogRepository, TicketActivityLogRepository>();

// Configuration repositories
builder.Services.AddScoped<IServerConfigurationRepository, ServerConfigurationRepository>();
builder.Services.AddScoped<IClientConfigurationRepository, ClientConfigurationRepository>();
builder.Services.AddScoped<ISiteConfigurationRepository, SiteConfigurationRepository>();
builder.Services.AddScoped<IConfigurationAuditRepository, ConfigurationAuditRepository>();

// Services
builder.Services.AddScoped<IConfigurationAuditService, ConfigurationAuditService>();
builder.Services.AddScoped<IConfigurationService, ConfigurationService>();
builder.Services.AddScoped<IConfigurationResolver, ConfigurationResolver>();
builder.Services.AddScoped<IAgentAuthService, AgentTokenAuthService>();
builder.Services.AddScoped<IDeployTokenService, DeployTokenService>();
builder.Services.AddScoped<ILoggingService, LoggingService>();

// New services for tickets enhancement (SLA, ActivityLog)
builder.Services.AddScoped<ISlaService, SlaService>();
builder.Services.AddScoped<IActivityLogService, ActivityLogService>();

// IMemoryCache (para ConfigurationResolver)
builder.Services.AddMemoryCache();

// Configuração de logging automático
builder.Services.Configure<AutomaticLoggingOptions>(
    builder.Configuration.GetSection("AutomaticLogging"));

// NATS
var natsUrl = builder.Configuration.GetValue<string>("Nats:Url") ?? "nats://localhost:4222";
builder.Services.AddSingleton(_ => new NatsConnection(new NatsOpts { Url = natsUrl }));
builder.Services.AddScoped<IAgentMessaging, NatsAgentMessaging>();
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
builder.Services.AddHostedService<SlaMonitoringBackgroundService>();

//  Controllers + JSON config
builder.Services.AddControllers(options =>
{
    // Registra LoggingActionFilter globalmente
    options.Filters.Add<LoggingActionFilter>();
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

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    options.AddPolicy("SignalR", policy =>
        policy.AllowAnyMethod().AllowAnyHeader().SetIsOriginAllowed(_ => true).AllowCredentials());
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

// Run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    runner.MigrateUp();
}

// Seed default workflow states
await DatabaseSeeder.SeedAsync(app.Services);

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors("AllowAll");

// Middleware de tratamento global de exceções (deve estar no início)
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Agent token auth middleware (para rotas /api/agent-auth/*)
app.UseAgentAuth();

app.MapControllers();
app.MapHub<AgentHub>("/hubs/agent", options =>
{
    options.AllowStatefulReconnects = true;
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
}).RequireCors("SignalR");

app.Run();
