using System.Reflection;
using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs.AgentAuth.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Discovery.Migrations.Migrations;
using FluentMigrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Cofre TPM de credenciais de conexão (tpmRecoveryEnabled).
///
/// Contrato: global no servidor com default FALSE (opt-in, zero mudança de
/// comportamento no rollout), sobrescrevível por cliente e site (NULL = herda) e
/// resolvido na precedência site ?? client ?? server — igual ao P2PFilesEnabled.
/// Cobre default, catálogo, persistência por PATCH, cascata no payload do agent,
/// bloqueio por lockedFieldsJson e o ResolvedConfiguration/Inheritance.
/// </summary>
public class TpmRecoveryEnabledTests
{
    // ── (a) Default global = false (opt-in) ───────────────────────

    [Test]
    public void Default_do_servidor_e_false()
    {
        Assert.That(new ServerConfiguration().TpmRecoveryEnabled, Is.False,
            "o cofre TPM é opt-in: o default global precisa nascer desligado");
    }

    [Test]
    public void Cliente_e_site_iniciam_herdando()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new ClientConfiguration().TpmRecoveryEnabled, Is.Null);
            Assert.That(new SiteConfiguration().TpmRecoveryEnabled, Is.Null);
        });
    }

    // ── (d) Catálogo de campos gerenciados ────────────────────────

    [Test]
    public void Campo_esta_no_catalogo_de_campos_gerenciados()
    {
        // Sem entrar no ManagedFields o nome nao e reconhecido no lockedFieldsJson
        // nem aparece em locks/heranca.
        Assert.That(ConfigurationFieldCatalog.ManagedFields, Does.Contain("TpmRecoveryEnabled"));
        Assert.That(
            ConfigurationFieldCatalog.NormalizeFieldName(nameof(ServerConfiguration.TpmRecoveryEnabled)),
            Is.EqualTo("TpmRecoveryEnabled"));
    }

    // ── Persistência via PATCH ────────────────────────────────────

    [Test]
    public async Task Patch_do_servidor_grava_tpm_recovery_enabled()
    {
        // Contexto novo por chamada: PatchServerAsync faz _db.Update(config) e o
        // segundo PATCH sobre o MESMO contexto re-anexaria a entidade ja rastreada
        // (IdentityMap conflict) — artefato de harness, nao do campo.
        await using (var db = CreateDb())
        {
            var service = BuildService(db);
            await service.PatchServerAsync(new Dictionary<string, object> { ["TpmRecoveryEnabled"] = true });

            var saved = await db.ServerConfigurations.AsNoTracking().SingleAsync();
            Assert.That(saved.TpmRecoveryEnabled, Is.True);
        }

        await using (var db = CreateDb())
        {
            var service = BuildService(db);
            await service.PatchServerAsync(new Dictionary<string, object> { ["TpmRecoveryEnabled"] = false });

            var reopened = await db.ServerConfigurations.AsNoTracking().SingleAsync();
            Assert.That(reopened.TpmRecoveryEnabled, Is.False);
        }
    }

    [Test]
    public async Task Patch_de_cliente_grava_e_reset_volta_a_herdar()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["TpmRecoveryEnabled"] = true });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.TpmRecoveryEnabled, Is.True, "PATCH de cliente precisa persistir o override");

        await service.ResetClientPropertyAsync(clientId, nameof(ClientConfiguration.TpmRecoveryEnabled));

        var reset = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(reset.TpmRecoveryEnabled, Is.Null, "reset precisa voltar a herdar (null)");
    }

    [Test]
    public async Task Patch_de_site_grava_o_override()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchSiteAsync(siteId, new Dictionary<string, object> { ["TpmRecoveryEnabled"] = true });

        var saved = await db.SiteConfigurations.AsNoTracking().SingleAsync(s => s.SiteId == siteId);
        Assert.That(saved.TpmRecoveryEnabled, Is.True);
    }

    // ── (c) Campo bloqueado via lockedFieldsJson ──────────────────

    [Test]
    public async Task Campo_bloqueado_no_servidor_nao_pode_ser_sobrescrito_mas_aceita_null()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            LockedFieldsJson = "[\"TpmRecoveryEnabled\"]",
        });
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            TpmRecoveryEnabled = true,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PatchClientAsync(
            clientId, new Dictionary<string, object> { ["TpmRecoveryEnabled"] = false }));

        // null = volta a herdar: permitido mesmo com o campo bloqueado no servidor.
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["TpmRecoveryEnabled"] = null });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.TpmRecoveryEnabled, Is.Null);
    }

    [Test]
    public async Task Campo_bloqueado_no_servidor_nao_pode_ser_sobrescrito_no_site()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            LockedFieldsJson = "[\"TpmRecoveryEnabled\"]",
        });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
            TpmRecoveryEnabled = true,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PatchSiteAsync(
            siteId, new Dictionary<string, object> { ["TpmRecoveryEnabled"] = false }));
    }

    // ── (b)/(e) Herança + payload do agent ────────────────────────

    [Test]
    public async Task Payload_do_agent_usa_site_depois_cliente_depois_servidor()
    {
        await using var db = CreateDb();
        var (agentId, _) = await SeedAsync(db, serverTpm: true, clientTpm: true, siteTpm: false);

        var payload = await BuildPayloadAsync(db, agentId);

        Assert.Multiple(() =>
        {
            Assert.That(payload.ContainsKey("tpmRecoveryEnabled"), Is.True, "a chave precisa existir sempre");
            Assert.That(payload["tpmRecoveryEnabled"], Is.EqualTo(false), "site vence cliente e servidor");
        });

        // Site herda do cliente
        db.SiteConfigurations.RemoveRange(db.SiteConfigurations);
        await db.SaveChangesAsync();
        var fromClient = await BuildPayloadAsync(db, agentId);
        Assert.That(fromClient["tpmRecoveryEnabled"], Is.EqualTo(true));

        // Sem cliente, herda do servidor
        db.ClientConfigurations.RemoveRange(db.ClientConfigurations);
        await db.SaveChangesAsync();
        var fromServer = await BuildPayloadAsync(db, agentId);
        Assert.That(fromServer["tpmRecoveryEnabled"], Is.EqualTo(true));
    }

    [Test]
    public async Task Payload_do_agent_site_ligado_vence_cliente_desligado()
    {
        await using var db = CreateDb();
        var (agentId, _) = await SeedAsync(db, serverTpm: false, clientTpm: false, siteTpm: true);

        var payload = await BuildPayloadAsync(db, agentId);

        Assert.That(payload["tpmRecoveryEnabled"], Is.EqualTo(true));
    }

    [Test]
    public async Task Payload_do_agent_sem_override_em_nenhum_nivel_fica_desligado()
    {
        // Default global false: sem override o payload envia false e o agent nem
        // toca no TPM (comportamento atual preservado).
        await using var db = CreateDb();
        var (agentId, _) = await SeedAsync(db, serverTpm: false, clientTpm: null, siteTpm: null);

        var payload = await BuildPayloadAsync(db, agentId);

        Assert.Multiple(() =>
        {
            Assert.That(payload.ContainsKey("tpmRecoveryEnabled"), Is.True);
            Assert.That(payload["tpmRecoveryEnabled"], Is.EqualTo(false));
        });
    }

    [Test]
    public void Go_le_a_chave_exatamente_tpm_recovery_enabled()
    {
        // Guarda de contrato com o agent: a chave do payload e camelCase e o nome
        // C# e exatamente TpmRecoveryEnabled (o Go usa a tag json:"tpmRecoveryEnabled").
        Assert.That(
            JsonNamingPolicy.CamelCase.ConvertName(nameof(ServerConfiguration.TpmRecoveryEnabled)),
            Is.EqualTo("tpmRecoveryEnabled"));
    }

    // ── ResolvedConfiguration + Inheritance ───────────────────────

    [Test]
    public async Task Resolucao_usa_site_depois_cliente_depois_servidor()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverTpm: true, clientTpm: true, siteTpm: false);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.TpmRecoveryEnabled, Is.False);
            Assert.That(resolved.Inheritance["TpmRecoveryEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Site));
        });
    }

    [Test]
    public async Task Resolucao_herda_do_cliente_quando_o_site_e_null()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverTpm: true, clientTpm: true, siteTpm: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.TpmRecoveryEnabled, Is.True);
            Assert.That(resolved.Inheritance["TpmRecoveryEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Client));
        });
    }

    [Test]
    public async Task Resolucao_cai_para_o_servidor_quando_cliente_e_site_sao_null()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverTpm: true, clientTpm: null, siteTpm: null);

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.TpmRecoveryEnabled, Is.True);
            Assert.That(resolved.Inheritance["TpmRecoveryEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Global));
        });
    }

    [Test]
    public async Task Resolucao_marca_bloqueado_quando_o_servidor_trava_o_campo()
    {
        await using var db = CreateDb();
        var (_, siteId) = await SeedAsync(db, serverTpm: true, clientTpm: false, siteTpm: false);
        var server = await db.ServerConfigurations.SingleAsync();
        server.LockedFieldsJson = "[\"TpmRecoveryEnabled\"]";
        await db.SaveChangesAsync();

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.TpmRecoveryEnabled, Is.False);
            Assert.That(resolved.Inheritance["TpmRecoveryEnabled"], Is.EqualTo((int)ConfigurationPriorityType.Block));
            Assert.That(resolved.BlockedFields, Does.Contain("TpmRecoveryEnabled"));
        });
    }

    [Test]
    public void Resolved_configuration_tem_o_campo_declarado()
    {
        var resolved = new ResolvedConfiguration();
        resolved.TpmRecoveryEnabled = true;

        Assert.That(resolved.TpmRecoveryEnabled, Is.True);
        Assert.That(typeof(ResolvedConfiguration).GetProperty("TpmRecoveryEnabled"), Is.Not.Null);
    }

    // ── Migration M300 (descoberta pelo runner + colunas nos 3 níveis) ──

    [Test]
    public void Migration_M300_e_descoberta_e_tem_versao_maior_que_a_M299()
    {
        var type = typeof(M300_AddTpmRecoveryEnabled);
        var attribute = type.GetCustomAttribute<MigrationAttribute>();

        Assert.Multiple(() =>
        {
            Assert.That(attribute, Is.Not.Null, "Sem [Migration] o runner ignora o arquivo.");
            Assert.That(typeof(Migration).IsAssignableFrom(type), Is.True);
            Assert.That(attribute!.Version, Is.GreaterThan(20261125_299L),
                "Versao menor que a migration anterior NUNCA e aplicada pelo FluentMigrator.");
        });
    }

    [Test]
    public void Migration_M300_declara_a_coluna_nos_tres_niveis()
    {
        // Leitura textual de proposito: prova que a migration cobre exatamente os
        // tres niveis da heranca (Server -> Client -> Site) e tem Down().
        var path = ResolveMigrationSource("M300_AddTpmRecoveryEnabled.cs");
        Assert.That(path, Is.Not.Null, "Nao foi possivel localizar o fonte da migration.");

        var source = File.ReadAllText(path!);

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("server_configurations"));
            Assert.That(source, Does.Contain("client_configurations"));
            Assert.That(source, Does.Contain("site_configurations"));
            Assert.That(source, Does.Contain("tpm_recovery_enabled"));
            Assert.That(source, Does.Contain("public override void Down()"));
        });
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static string? ResolveMigrationSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "Discovery.Migrations", "Migrations", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<(Guid AgentId, Guid SiteId)> SeedAsync(
        DiscoveryDbContext db,
        bool serverTpm,
        bool? clientTpm,
        bool? siteTpm)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            TpmRecoveryEnabled = serverTpm,
        });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site Teste" });
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            TpmRecoveryEnabled = clientTpm,
        });
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = clientId,
            TpmRecoveryEnabled = siteTpm,
        });
        db.Agents.Add(new Agent
        {
            Id = agentId,
            SiteId = siteId,
            Hostname = "qa-tpm-recovery",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (agentId, siteId);
    }

    /// <summary>
    /// Roda o handler real do endpoint /me/configuration e devolve o dicionario
    /// efetivo (mesmo harness de repositorios/resolver dos demais testes de config).
    /// </summary>
    private static async Task<Dictionary<string, object?>> BuildPayloadAsync(DiscoveryDbContext db, Guid agentId)
    {
        var redis = new NoopRedisService();
        var serverRepo = new ServerConfigurationRepository(db, redis, EmptyConfiguration());
        var service = new ConfigurationService(
            serverRepo,
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            new NoopAuditService(),
            new NoopResolver(),
            new NoopSecretProtector(),
            redis,
            new NoopNatsValidator());

        var handler = new GetAgentConfigurationHandler(
            new AgentRepository(db),
            new SiteRepository(db),
            service,
            new NoopTlsProbe(),
            new NoopMcpPolicyRepository(),
            new PatchRepository(db),
            NullLogger<GetAgentConfigurationHandler>.Instance);

        var result = await handler.Handle(new GetAgentConfigurationQuery(agentId), CancellationToken.None);
        Assert.That(result.IsSuccess, Is.True, "o handler do payload precisa resolver o agent semeado");

        var payload = (Dictionary<string, object?>)result.Value!;
        return payload;
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"tpm-recovery-enabled-{Guid.NewGuid():N}")
            .Options;
        return new ReducedDbContext(options);
    }

    /// <summary>Config vazia: o repositorio so usa IConfiguration para semear defaults na 1a linha.</summary>
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

    /// <summary>
    /// Contexto reduzido: o modelo de producao mapeia vetores do pgvector, que o
    /// provider InMemory nao suporta (mesmo padrao de UpdatesTabEnabledTests).
    /// </summary>
    private sealed class ReducedDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(ServerConfiguration),
                typeof(ClientConfiguration),
                typeof(SiteConfiguration),
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(PatchPolicy),
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

    private sealed class NoopTlsProbe : IAgentTlsCertificateProbe
    {
        public Task<string?> GetExpectedTlsCertHashAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetExpectedNatsTlsCertHashAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetExpectedTlsCertHashAsync(string probeUrl, string cacheKey, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public void InvalidateCache(string cacheKey) { }
    }

    private sealed class NoopMcpPolicyRepository : IMcpToolPolicyRepository
    {
        public Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolPolicy>>([]);

        public Task<McpToolPolicy?> GetPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<McpToolPolicy?>(null);

        public Task<IReadOnlyList<McpToolPolicy>> GetScopePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolPolicy>>([]);

        public Task<McpToolPolicy> UpsertAsync(McpToolPolicy policy, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteScopePolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task EnsureGlobalPoliciesAsync(
            string source,
            IEnumerable<(string Name, string? Description)> tools,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> CountLowerScopeOverridesAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, int>> GetLowerScopeOverrideCountsAsync(
            IReadOnlyCollection<string> toolNames, Guid? clientId, Guid? siteId, Guid? agentId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<McpToolPolicy?> GetAncestorLockedPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<McpToolPolicy?>(null);
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
            string url,
            string? user,
            string? password,
            CancellationToken cancellationToken)
            => Task.FromResult<(bool, string[])>( (true, []) );
    }
}
