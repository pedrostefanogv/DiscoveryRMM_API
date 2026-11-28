using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Discovery.Tests;

/// <summary>
/// Regressão do incidente de 28/11/2026: "Chat IA" e "Base de Conhecimento" tinham
/// DUAS fontes de verdade — a coluna resolvida por escopo (o que o console mostra em
/// "Funcionalidades do Sistema") e o campo de mesmo nome dentro de
/// AIIntegrationSettingsJson (o que o gate do chat lia). O card aparecia LIGADO e a
/// API respondia 400 "Chat IA está desabilitado para este escopo".
///
/// Contrato: a coluna é a fonte única; o JSON carrega só provedor/modelos.
/// </summary>
public class AiChatScopeFlagSingleSourceTests
{
    [Test]
    public async Task Coluna_de_ChatAI_ligada_vence_o_json_desatualizado()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db, chatAiColumn: true, kbColumn: false,
            aiJson: """{"enabled":true,"chatAIEnabled":false,"knowledgeBaseEnabled":false}""");

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.AIIntegration, Is.Not.Null);
            Assert.That(resolved.AIIntegration!.Enabled, Is.True, "a integração segue ligada pelo JSON");
            Assert.That(resolved.AIIntegration.ChatAIEnabled, Is.True,
                "o kill switch da coluna é a fonte única (o JSON false barrava o chat)");
            Assert.That(resolved.ChatAIEnabled, Is.True);
        });
    }

    [Test]
    public async Task Coluna_desligada_vence_json_ligado()
    {
        await using var db = CreateDb();
        var siteId = await SeedAsync(db, chatAiColumn: false, kbColumn: true,
            aiJson: """{"enabled":true,"chatAIEnabled":true,"knowledgeBaseEnabled":true}""");

        var resolved = await BuildResolver(db).ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.AIIntegration!.ChatAIEnabled, Is.False, "desligar pela coluna precisa vencer");
            Assert.That(resolved.AIIntegration.KnowledgeBaseEnabled, Is.True, "a KB segue a coluna (ligada)");
        });
    }

    private static async Task<Guid> SeedAsync(DiscoveryDbContext db, bool chatAiColumn, bool kbColumn, string aiJson)
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = IdGenerator.NewId(),
            ChatAIEnabled = chatAiColumn,
            KnowledgeBaseEnabled = kbColumn,
            AIIntegrationSettingsJson = aiJson,
        });
        db.Clients.Add(new Client { Id = clientId, Name = "Cliente teste" });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Site teste" });
        await db.SaveChangesAsync();
        return siteId;
    }

    private static DiscoveryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"ai-flag-single-source-{Guid.NewGuid():N}")
            .Options;
        return new ReducedConfigDbContext(options);
    }

    /// <summary>
    /// Modelo reduzido: o contexto completo traz entidades com pgvector
    /// (KnowledgeArticleChunk.Embedding) e o InMemory não monta o modelo.
    /// Mesma poda usada por ConfigurationServiceInheritanceTests.
    /// </summary>
    private sealed class ReducedConfigDbContext(DbContextOptions<DiscoveryDbContext> options)
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
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                    .Where(type => type.IsClass && type.Namespace is not null &&
                            type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                    .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<ServerConfiguration>(entity => entity.HasKey(config => config.Id));
            modelBuilder.Entity<ClientConfiguration>(entity => entity.HasKey(config => config.Id));
            modelBuilder.Entity<SiteConfiguration>(entity => entity.HasKey(config => config.Id));
            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
        }
    }

    private static ConfigurationResolver BuildResolver(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationResolver(
            new ServerConfigurationRepository(db, redis, new ConfigurationBuilder().Build()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new SiteRepository(db),
            redis,
            new NoopSecretProtector());
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

    private sealed class NoopSecretProtector : ISecretProtector
    {
        public bool IsEnabled => false;
        public bool IsProtected(string? value) => false;
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
        public string UnprotectOrSelf(string? value) => value ?? string.Empty;
    }
}
