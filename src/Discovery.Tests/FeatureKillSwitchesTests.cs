using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Kill switches de FEATURE configuráveis pela UI (CFG-A).
///
/// Antes: 10 chaves viviam só no appsettings.json (grupo A da
/// AUDITORIA_APPSETTINGS_VS_UI). Agora a fonte de verdade é a configuração do
/// servidor, com o appsettings como bootstrap/fallback.
///
/// CONTRATO COBERTO:
///   (a) DEFAULT de cada campo IDÊNTICO ao valor de hoje (zero surpresa);
///   (b) cada campo presente em ConfigurationFieldCatalog.ManagedFields;
///   (c) herança servidor → cliente → site nos 7 herdáveis;
///   (d) os 3 campos de servidor inteiro NÃO têm override em cliente/site;
///   (e) persistência via PATCH em servidor/cliente/site;
///   (f) o consumidor (TicketAccessPolicy) lê o valor RESOLVIDO do banco,
///       mantendo o appsettings como bootstrap.
/// </summary>
public class FeatureKillSwitchesTests
{
    /// <summary>Campos que herdam por cliente/site (7).</summary>
    private static readonly string[] Inheritable =
    [
        "DepartmentAuthShadowMode",
        "PushEnabled",
        "WingetCatalogEnabled",
        "RemoteAccessEnabled",
        "RemoteAccessRecordingEnabled",
        "RemoteAccessRecordingAutoDeleteExpired",
        "AutomaticLoggingEnabled",
    ];

    /// <summary>Campos do servidor INTEIRO (3) — sem coluna em client/site.</summary>
    private static readonly string[] ServerOnly =
    [
        "OpenTelemetryEnabled",
        "MigrationsRunOnStartup",
        "RealtimeContractMode",
    ];

    private static readonly string[] AllFields = [.. Inheritable, .. ServerOnly];

    // ── (a) Defaults idênticos ao de hoje ─────────────────────────

    [Test]
    public void Defaults_do_servidor_sao_identicos_ao_appsettings_de_hoje()
    {
        var server = new ServerConfiguration();

        Assert.Multiple(() =>
        {
            // Todos os 7 herdáveis + migração + logging default TRUE hoje.
            Assert.That(server.DepartmentAuthShadowMode, Is.True, "Tickets:DepartmentAuth:ShadowMode era true");
            Assert.That(server.PushEnabled, Is.True, "Push:Enabled era true");
            Assert.That(server.WingetCatalogEnabled, Is.True, "AppCatalog:Winget:Enabled era true");
            Assert.That(server.RemoteAccessEnabled, Is.True, "RemoteAccess:Enabled era true");
            Assert.That(server.RemoteAccessRecordingEnabled, Is.True, "RemoteAccess:Recording:Enabled era true");
            Assert.That(server.RemoteAccessRecordingAutoDeleteExpired, Is.True, "AutoDeleteExpired era true");
            Assert.That(server.AutomaticLoggingEnabled, Is.True, "AutomaticLogging:Enabled era true");
            Assert.That(server.MigrationsRunOnStartup, Is.True, "Migrations:RunOnStartup era true");
            // Os dois que NÃO eram true hoje.
            Assert.That(server.OpenTelemetryEnabled, Is.False, "OpenTelemetry:Enabled era false");
            Assert.That(server.RealtimeContractMode, Is.EqualTo("auto"), "RealtimeContract:Mode era \"auto\"");
        });
    }

    [Test]
    public void Cliente_e_site_iniciam_herdando_nos_campos_herdaveis()
    {
        var client = new ClientConfiguration();
        var site = new SiteConfiguration();

        Assert.Multiple(() =>
        {
            foreach (var name in Inheritable)
            {
                Assert.That(ReadBool(client, name), Is.Null, $"ClientConfiguration.{name} deveria iniciar null (herda)");
                Assert.That(ReadBool(site, name), Is.Null, $"SiteConfiguration.{name} deveria iniciar null (herda)");
            }
        });
    }

    // ── (b) Catálogo ──────────────────────────────────────────────

    [Test]
    public void Todos_os_campos_estao_no_catalogo_de_campos_gerenciados()
    {
        // Sem entrar em ManagedFields o campo NÃO aparece na UI nem é reconhecido
        // em locks/herança — que é exatamente o defeito que a auditoria apontou.
        Assert.Multiple(() =>
        {
            foreach (var name in AllFields)
            {
                Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain(name),
                    $"{name} precisa estar em ManagedFields");
            }
        });
    }

    [Test]
    public void O_catalogo_nao_reintroduziu_o_campo_legado_mcp()
    {
        // Guarda do comentário de ConfigurationFieldCatalog: McpToolPolicyJson
        // ficou LEGADO (a política vive em /settings/mcp-tools).
        Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Not.Contain("McpToolPolicyJson"));
    }

    // ── (c)/(d) Herança ───────────────────────────────────────────

    [Test]
    public async Task Resolucao_usa_site_depois_cliente_depois_servidor()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: true, client: true, site: false);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.PushEnabled, Is.False, "site vence cliente e servidor");
            Assert.That(resolved.Inheritance["PushEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Site));
        });
    }

    [Test]
    public async Task Resolucao_herda_do_cliente_quando_o_site_e_null()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: true, client: false, site: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.RemoteAccessEnabled, Is.False);
            Assert.That(resolved.Inheritance["RemoteAccessEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Client));
        });
    }

    [Test]
    public async Task Resolucao_cai_para_o_servidor_quando_cliente_e_site_sao_null()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: false, client: null, site: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.WingetCatalogEnabled, Is.False);
            Assert.That(resolved.Inheritance["WingetCatalogEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Global));
        });
    }

    [Test]
    public async Task Campos_de_servidor_inteiro_nao_herdam_de_cliente_nem_de_site()
    {
        // OpenTelemetryEnabled/MigrationsRunOnStartup/RealtimeContractMode são do
        // processo/schema/contrato global: cliente e site NÃO têm a propriedade.
        Assert.Multiple(() =>
        {
            foreach (var name in ServerOnly)
            {
                Assert.That(
                    typeof(ClientConfiguration).GetProperty(name), Is.Null,
                    $"ClientConfiguration NÃO deve ter {name} (sem override por cliente)");
                Assert.That(
                    typeof(SiteConfiguration).GetProperty(name), Is.Null,
                    $"SiteConfiguration NÃO deve ter {name} (sem override por site)");
            }
        });

        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: false, client: null, site: null);

        // Os 3 campos de servidor inteiro NÃO são tocados por SetAll (que só mexe
        // nos herdáveis): ficam explícitos aqui para provar que o resolver lê o
        // valor do SERVIDOR e que cliente/site não participam.
        var serverRow = await db.ServerConfigurations.SingleAsync();
        serverRow.MigrationsRunOnStartup = false;
        serverRow.OpenTelemetryEnabled = true;
        await db.SaveChangesAsync();

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.OpenTelemetryEnabled, Is.True, "vem do servidor");
            Assert.That(resolved.MigrationsRunOnStartup, Is.False, "vem do servidor");
            Assert.That(resolved.RealtimeContractMode, Is.EqualTo("auto"));
            Assert.That(resolved.Inheritance["OpenTelemetryEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Global));
            Assert.That(resolved.Inheritance["MigrationsRunOnStartup"], Is.EqualTo((int)ConfigurationPriorityType.Global));
            Assert.That(resolved.Inheritance["RealtimeContractMode"], Is.EqualTo((int)ConfigurationPriorityType.Global));
        });
    }

    [Test]
    public async Task RealtimeContractMode_normaliza_valor_desconhecido_para_auto()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: null, client: null, site: null);
        var server = await db.ServerConfigurations.SingleAsync();
        server.RealtimeContractMode = "banana";
        await db.SaveChangesAsync();

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.That(resolved.RealtimeContractMode, Is.EqualTo("auto"),
            "valor inválido não pode derrubar o dashboard em tempo real");
    }

    [Test]
    public void RealtimeContractModes_aceita_apenas_os_tres_valores()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RealtimeContractModes.IsValid("auto"), Is.True);
            Assert.That(RealtimeContractModes.IsValid("STRICT"), Is.True, "case-insensitive");
            Assert.That(RealtimeContractModes.IsValid("legacy"), Is.True);
            Assert.That(RealtimeContractModes.IsValid("banana"), Is.False);
            Assert.That(RealtimeContractModes.IsValid(""), Is.False);
            Assert.That(RealtimeContractModes.Normalize(" strict "), Is.EqualTo("strict"));
            Assert.That(RealtimeContractModes.Normalize(null), Is.EqualTo("auto"));
        });
    }

    [Test]
    public async Task Campo_bloqueado_e_reportado_como_Block()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, server: true, client: false, site: false);
        var server = await db.ServerConfigurations.SingleAsync();
        server.LockedFieldsJson = "[\"PushEnabled\"]";
        await db.SaveChangesAsync();

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.PushEnabled, Is.False, "o valor do site continua resolvido");
            Assert.That(resolved.Inheritance["PushEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Block));
            Assert.That(resolved.BlockedFields, Does.Contain("PushEnabled"));
        });
    }

    // ── (e) Persistência via PATCH ────────────────────────────────

    [Test]
    public async Task Patch_do_servidor_grava_os_campos()
    {
        await using var db = CreateDb();
        var service = BuildService(db);

        await service.PatchServerAsync(new Dictionary<string, object>
        {
            ["PushEnabled"] = false,
            ["MigrationsRunOnStartup"] = false,
            ["RealtimeContractMode"] = "strict",
        });

        var saved = await db.ServerConfigurations.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(saved.PushEnabled, Is.False);
            Assert.That(saved.MigrationsRunOnStartup, Is.False);
            Assert.That(saved.RealtimeContractMode, Is.EqualTo("strict"));
        });
    }

    [Test]
    public async Task Patch_de_cliente_persiste_o_override()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["RemoteAccessEnabled"] = false });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.RemoteAccessEnabled, Is.False,
            "sem a cópia no UpdateAsync o PATCH/PUT era descartado");
    }

    [Test]
    public async Task Patch_de_site_persiste_o_override()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(), SiteId = siteId, ClientId = clientId,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchSiteAsync(siteId, new Dictionary<string, object> { ["AutomaticLoggingEnabled"] = false });

        var saved = await db.SiteConfigurations.AsNoTracking().SingleAsync(s => s.SiteId == siteId);
        Assert.That(saved.AutomaticLoggingEnabled, Is.False);
    }

    // ── (f) O consumidor lê o valor RESOLVIDO ─────────────────────

    [Test]
    public async Task Politica_usa_o_valor_do_BANCO_quando_existe()
    {
        // appsettings diz shadow=true; o banco diz false => a regra fica ATIVA.
        await using var db = CreateDb();
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            DepartmentAuthShadowMode = false,
        });
        await db.SaveChangesAsync();

        var policy = BuildPolicy(db, configShadow: true);

        Assert.That(policy.ShadowModeBootstrap, Is.True, "o appsettings continua sendo o bootstrap");
        var denied = TicketAccessDecision.Deny("fora do time", "TICKET_NOT_IN_YOUR_TEAM");
        policy.ApplyShadow(denied, Guid.NewGuid(), Guid.NewGuid(), "Treat");

        var applied = policy.ApplyShadow(denied, Guid.NewGuid(), Guid.NewGuid(), "Treat");
        Assert.That(applied.Allowed, Is.False, "com o banco em false a negação VALE (regra ativa)");
    }

    [Test]
    public async Task Politica_mantem_shadow_quando_o_banco_diz_true()
    {
        await using var db = CreateDb();
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            DepartmentAuthShadowMode = true,
        });
        await db.SaveChangesAsync();

        var policy = BuildPolicy(db, configShadow: false);

        var denied = TicketAccessDecision.Deny("fora do time", "TICKET_NOT_IN_YOUR_TEAM");
        var applied = policy.ApplyShadow(denied, Guid.NewGuid(), Guid.NewGuid(), "Treat");

        Assert.That(applied.Allowed, Is.True, "banco em true => observação, acesso concedido");
    }

    [Test]
    public async Task Politica_cai_para_o_appsettings_sem_configuracao_no_banco()
    {
        // Nenhuma linha de servidor: mantém o bootstrap — não muda o comportamento
        // atual de quem só usa o appsettings.
        await using var db = CreateDb();

        var policy = BuildPolicy(db, configShadow: false);

        var denied = TicketAccessDecision.Deny("fora do time", "TICKET_NOT_IN_YOUR_TEAM");
        var applied = policy.ApplyShadow(denied, Guid.NewGuid(), Guid.NewGuid(), "Treat");

        Assert.That(applied.Allowed, Is.False, "sem linha no banco vale o appsettings (false = ativo)");
    }

    [Test]
    public void Sem_configuracao_alguma_o_padrao_continua_observacao()
    {
        // Contrato preservado: construir a política sem IConfiguration mantém o
        // shadow LIGADO (default true), como antes do CFG-A.
        var policy = BuildPolicy(null, configShadow: null);

        Assert.That(policy.ShadowMode, Is.True);
        Assert.That(policy.ShadowModeBootstrap, Is.True);
    }

    // ── Defaults no ResolvedConfiguration ─────────────────────────

    [Test]
    public void Resolved_configuration_declara_os_dez_campos()
    {
        Assert.Multiple(() =>
        {
            foreach (var name in AllFields)
            {
                var prop = typeof(ResolvedConfiguration).GetProperty(name);
                Assert.That(prop, Is.Not.Null, $"ResolvedConfiguration precisa de {name}");
            }

            // 9 booleanos + 1 string (o modo do contrato de realtime).
            Assert.That(typeof(ResolvedConfiguration).GetProperty("RealtimeContractMode")!.PropertyType,
                Is.EqualTo(typeof(string)));
            Assert.That(typeof(ResolvedConfiguration).GetProperty("PushEnabled")!.PropertyType,
                Is.EqualTo(typeof(bool)));
        });
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static bool? ReadBool(object target, string propertyName)
        => (bool?)target.GetType().GetProperty(propertyName)!.GetValue(target);

    private static TicketAccessPolicy BuildPolicy(DiscoveryDbContext? db, bool? configShadow)
    {
        IConfiguration? configuration = configShadow is null
            ? null
            : new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tickets:DepartmentAuth:ShadowMode"] = configShadow.Value.ToString(),
            }).Build();

        return new TicketAccessPolicy(
            db!, new DepartmentTeamResolver(db!), new NoopScopeContext(), configuration);
    }

    private static async Task<(Guid AgentId, Guid SiteId)> SeedAsync(
        DiscoveryDbContext db, bool? server, bool? client, bool? site)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var serverConfig = new ServerConfiguration { Id = IdGenerator.NewId() };
        if (server.HasValue)
        {
            SetAll(serverConfig, server.Value);
        }
        db.ServerConfigurations.Add(serverConfig);

        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });

        var clientConfig = new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId };
        if (client.HasValue) SetAll(clientConfig, client.Value);
        db.ClientConfigurations.Add(clientConfig);

        var siteConfig = new SiteConfiguration { Id = IdGenerator.NewId(), SiteId = siteId, ClientId = clientId };
        if (site.HasValue) SetAll(siteConfig, site.Value);
        db.SiteConfigurations.Add(siteConfig);

        db.Agents.Add(new Agent
        {
            Id = agentId, SiteId = siteId, Hostname = "qa-cfg-a",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (agentId, siteId);
    }

    private static void SetAll(object target, bool value)
    {
        foreach (var name in Inheritable)
        {
            target.GetType().GetProperty(name)!.SetValue(target, value);
        }
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"feature-kill-switches-{Guid.NewGuid():N}")
            .Options;
        return new ReducedDbContext(options);
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static ConfigurationService BuildService(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationService(
            new ServerConfigurationRepository(db, redis, EmptyConfiguration()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            new NoopAuditService(),
            new NoopResolver(),
            new NoopSecretProtector(),
            redis,
            new NoopNatsValidator());
    }

    private static ConfigurationResolver BuildResolver(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationResolver(
            new ServerConfigurationRepository(db, redis, EmptyConfiguration()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            redis,
            new NoopSecretProtector());
    }

    private sealed class ReducedDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(ServerConfiguration), typeof(ClientConfiguration), typeof(SiteConfiguration),
                typeof(Client), typeof(Site), typeof(Agent), typeof(Department), typeof(DepartmentMember),
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }
        }
    }

    /// <summary>ScopeContext de teste com acesso global (sem Redis/Db).</summary>
    private sealed class NoopScopeContext : IScopeContext
    {
        public Guid? UserId { get; private set; }
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
        public void SetUserId(Guid userId) => UserId = userId;

        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(new UserScopeAccess
            {
                HasGlobalAccess = true, AllowedClientIds = [], AllowedSiteIds = [],
            });

        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(true);
    }

    private sealed class NoopRedisService : IRedisService
    {
        public bool IsConnected => false;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(0L);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(false);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(-1);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    private sealed class NoopAuditService : IConfigurationAuditService
    {
        public Task LogChangeAsync(string entityType, Guid entityId, string fieldName,
            string? oldValue, string? newValue, string? reason = null, string? changedBy = null, string? ipAddress = null)
            => Task.CompletedTask;
        public Task<IEnumerable<ConfigurationAudit>> GetEntityHistoryAsync(string entityType, Guid entityId, int limit = 100)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);
        public Task<IEnumerable<ConfigurationAudit>> GetRecentChangesAsync(int days = 90, int limit = 1000)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);
        public Task<IEnumerable<ConfigurationAudit>> GetChangesByUserAsync(string username, int limit = 100)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);
        public Task<IEnumerable<ConfigurationAudit>> GetFieldHistoryAsync(string entityType, Guid entityId, string fieldName)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);
        public Task<IEnumerable<ConfigurationAudit>> GetAuditReportAsync(DateTime startDate, DateTime endDate)
            => Task.FromResult<IEnumerable<ConfigurationAudit>>([]);
    }

    private sealed class NoopResolver : IConfigurationResolver
    {
        public Task<ServerConfiguration> GetServerAsync() => throw new NotSupportedException();
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new NotSupportedException();
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new NotSupportedException();
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(Guid? clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => throw new NotSupportedException();
        public void ClearCache() { }
    }

    private sealed class NoopSecretProtector : ISecretProtector
    {
        public bool IsEnabled => false;
        public bool IsProtected(string? value) => false;
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
        public string UnprotectOrSelf(string? value) => value ?? string.Empty;
    }

    private sealed class NoopNatsValidator : INatsConnectionValidator
    {
        public Task<(bool IsValid, string[] Errors)> ValidateConnectionAsync(
            string url, string? user, string? password, CancellationToken cancellationToken)
            => Task.FromResult<(bool, string[])>((true, []));
    }
}
