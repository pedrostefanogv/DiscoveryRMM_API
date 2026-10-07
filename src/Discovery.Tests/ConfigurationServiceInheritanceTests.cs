using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Regressão da herança real (NULL = herda) nos booleanos de cliente/site.
///
/// A M025 normalizou "qualquer null booleano" para FALSE e o ConfigurationService
/// repetia isso em todo create/patch/update, então o cliente/site sempre
/// sobrescrevia o servidor e o "Reset to inherit" gravava false. A M198 converte os
/// FALSE em NULL; estes testes garantem que o código não volte a normalizar.
/// </summary>
public class ConfigurationServiceInheritanceTests
{
    [Test]
    public async Task Patch_de_um_campo_preserva_a_heranca_dos_demais_booleanos()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["ChatAIEnabled"] = true });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.Multiple(() =>
        {
            Assert.That(saved.ChatAIEnabled, Is.True);
            Assert.That(saved.RecoveryEnabled, Is.Null, "deve continuar herdando o servidor");
            Assert.That(saved.DiscoveryEnabled, Is.Null);
            Assert.That(saved.P2PFilesEnabled, Is.Null);
            Assert.That(saved.CloudBootstrapEnabled, Is.Null);
            Assert.That(saved.SupportEnabled, Is.Null);
            Assert.That(saved.KnowledgeBaseEnabled, Is.Null);
            Assert.That(saved.ZeroTouchEnabled, Is.Null);
        });
    }

    [Test]
    public async Task Patch_preserva_false_explicito()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration { Id = IdGenerator.NewId(), ClientId = clientId });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchClientAsync(clientId, new Dictionary<string, object> { ["DiscoveryEnabled"] = false });

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.DiscoveryEnabled, Is.False, "false explícito não pode virar herança");
    }

    [Test]
    public async Task Reset_de_booleano_grava_null_para_herdar()
    {
        await using var db = CreateDb();
        var clientId = Guid.NewGuid();
        db.ClientConfigurations.Add(new ClientConfiguration
        {
            Id = IdGenerator.NewId(),
            ClientId = clientId,
            DiscoveryEnabled = true,
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.ResetClientPropertyAsync(clientId, nameof(ClientConfiguration.DiscoveryEnabled));

        var saved = await db.ClientConfigurations.AsNoTracking().SingleAsync(c => c.ClientId == clientId);
        Assert.That(saved.DiscoveryEnabled, Is.Null, "reset deve voltar para herança, não para false");
    }

    [Test]
    public async Task Patch_de_site_preserva_a_heranca_dos_demais_booleanos()
    {
        await using var db = CreateDb();
        var siteId = Guid.NewGuid();
        db.SiteConfigurations.Add(new SiteConfiguration
        {
            Id = IdGenerator.NewId(),
            SiteId = siteId,
            ClientId = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.PatchSiteAsync(siteId, new Dictionary<string, object> { ["SupportEnabled"] = false });

        var saved = await db.SiteConfigurations.AsNoTracking().SingleAsync(s => s.SiteId == siteId);
        Assert.Multiple(() =>
        {
            Assert.That(saved.SupportEnabled, Is.False);
            Assert.That(saved.DiscoveryEnabled, Is.Null);
            Assert.That(saved.P2PFilesEnabled, Is.Null);
            Assert.That(saved.ChatAIEnabled, Is.Null);
            Assert.That(saved.ZeroTouchEnabled, Is.Null);
        });
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"config-inheritance-{Guid.NewGuid():N}")
            .Options;
        return new ConfigurationInheritanceDbContext(options);
    }

    private static ConfigurationService BuildService(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationService(
            new ServerConfigurationRepository(db, redis),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            null!, // ISiteRepository só é usado quando a config do site ainda não existe
            new NoopAuditService(),
            new NoopResolver(),
            new NoopSecretProtector(),
            redis,
            new NoopNatsValidator());
    }

    /// <summary>
    /// Contexto reduzido: o modelo de produção mapeia o vetor do pgvector, que o
    /// provider InMemory não suporta. Mesmo padrão usado em AgentTrashTests.
    /// </summary>
    private sealed class ConfigurationInheritanceDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Sem esta poda, os DbSet do contexto trazem entidades com pgvector
            // (KnowledgeArticleChunk.Embedding) e o InMemory não consegue montar o modelo.
            var allowed = new HashSet<Type>
            {
                typeof(ServerConfiguration),
                typeof(ClientConfiguration),
                typeof(SiteConfiguration),
                typeof(Client),
                typeof(Site),
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<ServerConfiguration>(entity =>
            {
                entity.ToTable("server_configurations");
                entity.HasKey(config => config.Id);
            });
            modelBuilder.Entity<ClientConfiguration>(entity =>
            {
                entity.ToTable("client_configurations");
                entity.HasKey(config => config.Id);
            });
            modelBuilder.Entity<SiteConfiguration>(entity =>
            {
                entity.ToTable("site_configurations");
                entity.HasKey(config => config.Id);
            });
            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
        }
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
        public Task<AutoUpdateSettings> GetAutoUpdateSettingsAsync(string level, Guid? targetId = null) => throw new NotSupportedException();
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
            => Task.FromResult((true, Array.Empty<string>()));
    }
}
