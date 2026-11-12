using System.Reflection;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using Discovery.Migrations.Migrations;
using FluentMigrator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Pgvector.EntityFrameworkCore;

namespace Discovery.Tests;

/// <summary>
/// Rate limit das MCP tools do chat de IA: padrão de produto (20/min), teto
/// GLOBAL herdável (servidor → cliente → site) e o limite EFETIVO de uma tool
/// (o MENOR entre o limite por tool e o teto global).
///
/// Contexto da mudança: o auto-registro gravava 5/min para tools do servidor e
/// 10/min para tools do agente — dois números sem decisão de produto. Agora o
/// padrão é único (20) e o que reduz o consumo por escopo é o teto global.
///
/// Dois contratos convivem de propósito e NÃO se contradizem:
///  • <see cref="McpToolRateLimitCatalog.Normalize"/> — contrato de CONFIGURAÇÃO,
///    fail-safe: qualquer valor fora de 1..600 (0, negativo, null) vira o default
///    20. O rate limit não é desligável por configuração.
///  • <see cref="McpToolRateLimitCatalog.Effective"/> — contrato de RUNTIME,
///    fail-open: teto global &lt;= 0 significa "sem piso aplicável" e deixa o
///    limite da tool valer integralmente (caminho defensivo, inalcançável pela
///    configuração porque a validação barra fora de 1..600).
/// </summary>
[TestFixture]
public class McpToolRateLimitTests
{
    // ── 1. Padrão do produto ────────────────────────────────────────────────

    [Test]
    public void McpToolPolicy_DefaultMaxCallsPerMinute_IsTwenty()
    {
        var policy = new McpToolPolicy();

        Assert.Multiple(() =>
        {
            Assert.That(policy.MaxCallsPerMinute, Is.EqualTo(20),
                "a política nasce com o padrão do produto (era 5 no servidor e 10 no agente)");
            Assert.That(policy.MaxCallsPerMinute, Is.EqualTo(McpToolRateLimitCatalog.DefaultCallsPerMinute),
                "o default da entidade precisa ser a MESMA constante do catálogo (fonte única)");
            Assert.That(policy.TimeoutSeconds, Is.EqualTo(30),
                "o default vizinho de timeout não pode ter mudado junto");
        });
    }

    [Test]
    public void ServerConfiguration_DefaultMcpToolCallsPerMinute_IsTwenty()
    {
        var config = new ServerConfiguration();

        Assert.Multiple(() =>
        {
            Assert.That(config.McpToolCallsPerMinute, Is.EqualTo(McpToolRateLimitCatalog.DefaultCallsPerMinute),
                "o default do servidor precisa bater com o DEFAULT da migration M287 (20)");
            Assert.That(new ClientConfiguration().McpToolCallsPerMinute, Is.Null,
                "cliente nasce NULL = herda o servidor");
            Assert.That(new SiteConfiguration().McpToolCallsPerMinute, Is.Null,
                "site nasce NULL = herda cliente/servidor");
            Assert.That(new ResolvedConfiguration().McpToolCallsPerMinute,
                Is.EqualTo(McpToolRateLimitCatalog.DefaultCallsPerMinute));
        });
    }

    // ── 2. Normalize: contrato de CONFIGURAÇÃO (fail-safe para 20) ──────────

    [Test]
    public void Normalize_FallsBackToDefaultForAnyOutOfRangeValue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(McpToolRateLimitCatalog.Normalize(null), Is.EqualTo(20), "null (ausente) herda o padrão");
            Assert.That(McpToolRateLimitCatalog.Normalize(0), Is.EqualTo(20),
                "0 NÃO desliga o rate limit — cai no padrão (o produto proíbe teto ilimitado)");
            Assert.That(McpToolRateLimitCatalog.Normalize(-3), Is.EqualTo(20), "negativo não faz sentido");
            Assert.That(McpToolRateLimitCatalog.Normalize(601), Is.EqualTo(20), "acima do teto de 600");
            Assert.That(McpToolRateLimitCatalog.Normalize(9999), Is.EqualTo(20), "valor absurdo");
            Assert.That(McpToolRateLimitCatalog.Normalize(int.MinValue), Is.EqualTo(20));
        });
    }

    [Test]
    public void Normalize_PreservesEveryValueInsideTheValidRange()
    {
        Assert.Multiple(() =>
        {
            // Limites da faixa: o contrato é INCLUSIVO nas duas pontas.
            Assert.That(McpToolRateLimitCatalog.Normalize(McpToolRateLimitCatalog.MinCallsPerMinute), Is.EqualTo(1));
            Assert.That(McpToolRateLimitCatalog.Normalize(McpToolRateLimitCatalog.MaxCallsPerMinute), Is.EqualTo(600));
            Assert.That(McpToolRateLimitCatalog.Normalize(7), Is.EqualTo(7));
            Assert.That(McpToolRateLimitCatalog.Normalize(20), Is.EqualTo(20));
        });
    }

    [Test]
    public void Catalog_ConstantsMatchTheMcpToolsControllerCeiling()
    {
        Assert.Multiple(() =>
        {
            Assert.That(McpToolRateLimitCatalog.DefaultCallsPerMinute, Is.EqualTo(20));
            Assert.That(McpToolRateLimitCatalog.MinCallsPerMinute, Is.EqualTo(1));
            // O mesmo teto aplicado pelo McpToolsController ao salvar a política de
            // uma tool individual (Math.Min(valor, 600)) — não podem divergir.
            Assert.That(McpToolRateLimitCatalog.MaxCallsPerMinute, Is.EqualTo(600));
            Assert.That(McpToolRateLimitCatalog.MinCallsPerMinute,
                Is.LessThan(McpToolRateLimitCatalog.MaxCallsPerMinute));
            Assert.That(McpToolRateLimitCatalog.DefaultCallsPerMinute,
                Is.InRange(McpToolRateLimitCatalog.MinCallsPerMinute, McpToolRateLimitCatalog.MaxCallsPerMinute));
        });
    }

    // ── 5. Effective: contrato de RUNTIME (fail-open, teto é PISO) ──────────

    [Test]
    public void Effective_IsTheSmallerOfToolLimitAndGlobalCeiling()
    {
        Assert.Multiple(() =>
        {
            // Teto global MENOR que a tool: o teto vence (é um piso).
            Assert.That(McpToolRateLimitCatalog.Effective(toolMaxCallsPerMinute: 10, globalMaxCallsPerMinute: 5),
                Is.EqualTo(5), "teto global 5 sobre uma tool de 10 => efetivo 5");
            // Teto global MAIOR que a tool: a tool vence (o teto NUNCA aumenta).
            Assert.That(McpToolRateLimitCatalog.Effective(10, 20), Is.EqualTo(10),
                "teto global 20 sobre uma tool de 10 => efetivo 10");
            Assert.That(McpToolRateLimitCatalog.Effective(10, 600), Is.EqualTo(10));
            // Iguais.
            Assert.That(McpToolRateLimitCatalog.Effective(10, 10), Is.EqualTo(10));
            // Default do produto nos dois lados.
            Assert.That(McpToolRateLimitCatalog.Effective(20, 20), Is.EqualTo(20));
        });
    }

    [Test]
    public void Effective_TreatsNonPositiveCeilingAsNoCeiling()
    {
        // Caminho DEFENSIVO do runtime: a configuração nunca produz <= 0 (a
        // validação barra fora de 1..600 e o Normalize cai para 20), mas um
        // resolvedor ausente/legado devolve 0 como sentinela de "sem teto".
        // Aqui o limite da tool vale integralmente — o teto não pode virar um
        // piso artificial onde não existe configuração.
        Assert.Multiple(() =>
        {
            Assert.That(McpToolRateLimitCatalog.Effective(10, 0), Is.EqualTo(10));
            Assert.That(McpToolRateLimitCatalog.Effective(10, -1), Is.EqualTo(10));
            Assert.That(McpToolRateLimitCatalog.Effective(600, 0), Is.EqualTo(600));
        });
    }

    // ── 3. Herança site ?? cliente ?? servidor ──────────────────────────────

    [Test]
    public void ResolveMcpToolCallsPerMinute_AppliesSiteClientServerCascade()
    {
        Assert.Multiple(() =>
        {
            // O nível mais específico presente vence.
            Assert.That(ConfigurationResolver.ResolveMcpToolCallsPerMinute(site: 10, client: 20, server: 30),
                Is.EqualTo(10), "site presente vence cliente e servidor");
            Assert.That(ConfigurationResolver.ResolveMcpToolCallsPerMinute(null, 20, 30),
                Is.EqualTo(20), "sem site, o cliente vence o servidor");
            Assert.That(ConfigurationResolver.ResolveMcpToolCallsPerMinute(null, null, 30),
                Is.EqualTo(30), "sem site nem cliente, vale o servidor");
        });
    }

    [Test]
    public void ResolveMcpToolCallsPerMinute_SanitizesEveryLevel()
    {
        Assert.Multiple(() =>
        {
            // Um override inválido no nível mais específico NÃO pode vazar para a
            // cadeia: ele cai no default, não "herda o de baixo".
            Assert.That(ConfigurationResolver.ResolveMcpToolCallsPerMinute(0, null, 30), Is.EqualTo(20),
                "site 0 é inválido => default 20");
            Assert.That(ConfigurationResolver.ResolveMcpToolCallsPerMinute(601, null, 30), Is.EqualTo(20),
                "site 601 acima do teto => default 20");
            Assert.That(ConfigurationResolver.ResolveMcpToolCallsPerMinute(null, null, 0), Is.EqualTo(20),
                "servidor 0 => default 20 (nunca ilimitado)");
            // Limites da faixa sobrevivem à cadeia.
            Assert.That(ConfigurationResolver.ResolveMcpToolCallsPerMinute(1, 20, 30), Is.EqualTo(1));
            Assert.That(ConfigurationResolver.ResolveMcpToolCallsPerMinute(600, 20, 30), Is.EqualTo(600));
        });
    }

    [Test]
    public async Task ResolvedConfiguration_RecordsInheritanceSourceForMcpToolCallsPerMinute()
    {
        // A herança real (NULL = herda) precisa estar registrada em
        // Inheritance[], que é o que a tela usa para dizer "herdado do servidor".
        // Mesmo molde do ConfigurationServiceInheritanceTests.
        await using var db = CreateConfigDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        db.ServerConfigurations.Add(new ServerConfiguration
        {
            Id = Guid.NewGuid(),
            McpToolCallsPerMinute = 45,
        });
        // O site precisa EXISTIR: ResolveForSiteAsync resolve o ClientId a partir
        // da entidade Site (e lança quando ela não existe). Nenhuma config de site
        // ou de cliente é criada de propósito — é o cenário "herda do servidor".
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });
        await db.SaveChangesAsync();

        var resolver = new ConfigurationResolver(
            new ServerConfigurationRepository(db, new NoopRedisService(), EmptyConfiguration()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            new FakeSiteRepository(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" }),
            new NoopRedisService(),
            new NoopSecretProtector());

        var resolved = await resolver.ResolveForSiteAsync(siteId);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.McpToolCallsPerMinute, Is.EqualTo(45),
                "sem config de client/site, o valor do servidor precisa chegar ao resolvido");
            Assert.That(resolved.Inheritance, Does.ContainKey("McpToolCallsPerMinute"),
                "a tela usa Inheritance[] para dizer de onde o valor veio");
        });
    }

    // ── 4. Validação 1..600 no ConfigurationService ─────────────────────────

    [Test]
    public async Task ValidateAsync_RejectsMcpToolCallsPerMinuteOutsideOneToSixHundred()
    {
        await using var db = CreateConfigDb();
        var service = BuildConfigurationService(db);

        var zero = await service.ValidateAsync(new ServerConfiguration
        {
            Id = Guid.NewGuid(),
            McpToolCallsPerMinute = 0,
        });
        Assert.That(zero.IsValid, Is.False, "0 é rejeitado: o rate limit não é desligável por aqui");
        Assert.That(zero.Errors.Any(e => e.Contains("McpToolCallsPerMinute")), Is.True,
            "o erro precisa nomear o campo para o operador saber o que corrigir");

        var acima = await service.ValidateAsync(new ServerConfiguration
        {
            Id = Guid.NewGuid(),
            McpToolCallsPerMinute = 601,
        });
        Assert.That(acima.IsValid, Is.False, "601 está acima do teto de 600");
    }

    [Test]
    public async Task ValidateAsync_AcceptsBothEndsOfTheRange()
    {
        await using var db = CreateConfigDb();
        var service = BuildConfigurationService(db);

        foreach (var value in new[] { 1, 20, 600 })
        {
            var result = await service.ValidateAsync(new ServerConfiguration
            {
                Id = Guid.NewGuid(),
                McpToolCallsPerMinute = value,
            });
            Assert.That(result.IsValid, Is.True,
                $"McpToolCallsPerMinute={value} é válido (faixa 1..600)");
        }
    }

    [Test]
    public async Task ValidateAsync_ValidatesNullableClientAndSiteValuesOnlyWhenPresent()
    {
        // O campo é int? em cliente/site: um int? com valor é BOXEADO como int,
        // então a validação precisa pegá-lo. Um int? SEM valor significa "herda"
        // e não pode ser validado (nem virar erro).
        await using var db = CreateConfigDb();
        var service = BuildConfigurationService(db);

        var herda = await service.ValidateAsync(new ClientConfiguration
        {
            Id = Guid.NewGuid(),
            McpToolCallsPerMinute = null,
        });
        Assert.That(herda.IsValid, Is.True, "null = herda, não é erro");

        var invalido = await service.ValidateAsync(new ClientConfiguration
        {
            Id = Guid.NewGuid(),
            McpToolCallsPerMinute = 0,
        });
        Assert.That(invalido.IsValid, Is.False,
            "0 explícito no cliente é rejeitado igual ao servidor (nullable com valor é validado)");

        var invalidoSite = await service.ValidateAsync(new SiteConfiguration
        {
            Id = Guid.NewGuid(),
            McpToolCallsPerMinute = 9999,
        });
        Assert.That(invalidoSite.IsValid, Is.False, "site acima de 600 é rejeitado");
    }

    // ── 5b. ResolveMaxCallsPerMinuteAsync: fiação no runtime da governança ──

    [Test]
    public async Task ResolveMaxCallsPerMinuteAsync_WithoutConfigurationResolver_UsesToolLimit()
    {
        // Fail-open: sem IConfigurationResolver injetado não há teto global
        // aplicável, então vale o limite da própria política da tool. É o registro
        // atual de Program.cs antes de alguém injetar o resolver — a governança
        // não pode passar a recusar chamadas por causa da ausência do teto.
        await using var db = NewMcpDb();
        var governance = new McpToolGovernance(
            new McpToolPolicyRepository(db), new CatalogExecutor(), redis: null);

        var effective = await governance.ResolveMaxCallsPerMinuteAsync(
            new McpToolScope(null, null, null), policyMaxCallsPerMinute: 10);

        Assert.That(effective, Is.EqualTo(10),
            "sem teto global aplicável, o limite da tool vale integralmente");
    }

    [Test]
    public async Task ResolveMaxCallsPerMinuteAsync_AppliesTheCeilingAsAFloor()
    {
        await using var db = NewMcpDb();
        var resolver = new FixedCeilingResolver(serverCeiling: 5);
        var governance = new McpToolGovernance(
            new McpToolPolicyRepository(db), new CatalogExecutor(),
            redis: null, configurationResolver: resolver);

        var abafado = await governance.ResolveMaxCallsPerMinuteAsync(
            new McpToolScope(null, null, null), policyMaxCallsPerMinute: 10);
        Assert.That(abafado, Is.EqualTo(5), "teto global 5 é um PISO: derruba a tool de 10 para 5");

        var folgado = await governance.ResolveMaxCallsPerMinuteAsync(
            new McpToolScope(null, null, null), policyMaxCallsPerMinute: 2);
        Assert.That(folgado, Is.EqualTo(2), "o teto global NUNCA aumenta o limite da tool");
    }

    [Test]
    public async Task ResolveMaxCallsPerMinuteAsync_FailsOpenWhenTheResolverThrows()
    {
        // Uma falha de leitura da configuração não pode derrubar o chat nem
        // desligar o limite por tool: cai no limite da própria política.
        await using var db = NewMcpDb();
        var governance = new McpToolGovernance(
            new McpToolPolicyRepository(db), new CatalogExecutor(),
            redis: null, configurationResolver: new ThrowingCeilingResolver());

        var effective = await governance.ResolveMaxCallsPerMinuteAsync(
            new McpToolScope(Guid.NewGuid(), null, null), policyMaxCallsPerMinute: 10);

        Assert.That(effective, Is.EqualTo(10),
            "resolvedor indisponível => vale o limite da tool (fail-open), não 0 nem exceção");
    }

    [Test]
    public async Task ResolveMaxCallsPerMinuteAsync_SanitizesTheToolLimit()
    {
        // O limite POR TOOL também passa pelo Normalize: uma política legada com 0
        // (ou acima de 600) não pode virar "ilimitado" no runtime.
        await using var db = NewMcpDb();
        var governance = new McpToolGovernance(
            new McpToolPolicyRepository(db), new CatalogExecutor(), redis: null);

        // (awaits resolvidos antes do Assert.Multiple: o delegate precisa ser
        // síncrono para o NUnit reportar cada falha com o seu contexto.)
        var comZero = await governance.ResolveMaxCallsPerMinuteAsync(new McpToolScope(null, null, null), 0);
        var acimaDoTeto = await governance.ResolveMaxCallsPerMinuteAsync(new McpToolScope(null, null, null), 9999);
        var valido = await governance.ResolveMaxCallsPerMinuteAsync(new McpToolScope(null, null, null), 7);

        Assert.Multiple(() =>
        {
            Assert.That(comZero, Is.EqualTo(20), "tool com 0 => padrão do produto, nunca ilimitado");
            Assert.That(acimaDoTeto, Is.EqualTo(20), "tool acima do teto => padrão do produto");
            Assert.That(valido, Is.EqualTo(7), "valor válido é preservado");
        });
    }

    // ── 5c. Ponto 4 da api-core: o teto vale IMEDIATAMENTE após o save ──────

    [Test]
    public async Task ResolveMaxCallsPerMinuteAsync_ReflectsANewlySavedToolLimitImmediately()
    {
        // O cache de 30s do teto global foi REMOVIDO de propósito (o teto se apoia
        // no cache do IConfigurationResolver). O que não pode voltar a existir é um
        // cache que deixe o limite EFETIVO desatualizado logo após o operador
        // salvar a política na tela de governança: o valor novo tem de valer já na
        // primeira chamada seguinte.
        await using var db = NewMcpDb();
        var governance = new McpToolGovernance(
            new McpToolPolicyRepository(db), new CatalogExecutor(), redis: null);
        var scope = new McpToolScope(null, null, null);

        // Antes de salvar não há política: o executor/chat usaria o default (20).
        var antes = await governance.ResolveMaxCallsPerMinuteAsync(scope, 20);
        Assert.That(antes, Is.EqualTo(20), "sem política salva, o padrão do produto vale");

        // O operador salva 3/min para a tool (dentro do teto global ausente).
        await governance.SavePolicyAsync("knowledge_search",
            new SaveMcpToolPolicyRequest(null, null, null, IsEnabled: true,
                MaxCallsPerMinute: 3, TimeoutSeconds: null, Locked: false));

        // A 1ª resolução depois do save precisa já refletir o limite novo.
        var depois = await governance.ResolveMaxCallsPerMinuteAsync(scope, 3);
        Assert.That(depois, Is.EqualTo(3),
            "o limite salvo vale imediatamente — nenhum cache pode servir o valor antigo");
    }

    [Test]
    public async Task ResolveMaxCallsPerMinuteAsync_AppliesTheCeilingToANewlySavedLimitImmediately()
    {
        // Mesmo cenário, agora com teto global MENOR que o limite salvo: o efetivo
        // precisa ser o teto já na primeira chamada após o save (o menor vence).
        await using var db = NewMcpDb();
        var governance = new McpToolGovernance(
            new McpToolPolicyRepository(db), new CatalogExecutor(),
            redis: null, configurationResolver: new FixedCeilingResolver(serverCeiling: 4));
        var scope = new McpToolScope(null, null, null);

        await governance.SavePolicyAsync("knowledge_search",
            new SaveMcpToolPolicyRequest(null, null, null, IsEnabled: true,
                MaxCallsPerMinute: 100, TimeoutSeconds: null, Locked: false));

        var effective = await governance.ResolveMaxCallsPerMinuteAsync(scope, 100);
        Assert.That(effective, Is.EqualTo(4),
            "teto global 4 abafa imediatamente o limite recém-salvo de 100");
    }

    // ── 7. Regressão do catálogo de governança ─────────────────────────────

    [Test]
    public async Task Catalog_ToolWithoutScopePolicy_ShowsProductDefaultOfTwenty()
    {
        await using var db = NewMcpDb();
        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new CatalogExecutor());

        var catalog = await governance.GetCatalogAsync(new McpToolScope(null, null, null));

        var kb = catalog.Tools.Single(t => t.Name == "knowledge_search");
        Assert.Multiple(() =>
        {
            Assert.That(kb.MaxCallsPerMinute, Is.EqualTo(20),
                "tool de SERVIDOR sem política no escopo: 20/min (era 5)");
            Assert.That(kb.TimeoutSeconds, Is.EqualTo(30), "o timeout de fallback do servidor não mudou");
        });
    }

    [Test]
    public async Task Catalog_AgentToolWithoutScopePolicy_AlsoShowsTwenty()
    {
        await using var db = NewMcpDb();
        await new McpToolPolicyRepository(db).EnsureGlobalPoliciesAsync(
            McpToolSources.Agent,
            new (string Name, string? Description)[] { ("service_control", "Controla serviços.") });

        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new CatalogExecutor());
        var catalog = await governance.GetCatalogAsync(new McpToolScope(null, null, null));

        var tool = catalog.Tools.Single(t => t.Name == "service_control");
        Assert.Multiple(() =>
        {
            Assert.That(tool.MaxCallsPerMinute, Is.EqualTo(20),
                "tool de AGENTE: o auto-registro não grava mais 10/min");
            Assert.That(tool.TimeoutSeconds, Is.EqualTo(120), "o timeout do agente continua 120s");
        });
    }

    // ── 8. Capacidades não executáveis continuam sem rate limit ────────────

    [Test]
    public async Task Catalog_NonExecutableCapability_HasNoRateLimit()
    {
        await using var db = NewMcpDb();
        var governance = new McpToolGovernance(new McpToolPolicyRepository(db), new CatalogExecutor());

        var catalog = await governance.GetCatalogAsync(new McpToolScope(Guid.NewGuid(), null, null));
        var capability = catalog.Tools.Single(t => t.Name == McpToolCatalogMetadata.A2uiCapability);

        Assert.Multiple(() =>
        {
            Assert.That(capability.MaxCallsPerMinute, Is.EqualTo(0),
                "a2ui não é executável: mostrar 20/min confundiria o operador");
            Assert.That(capability.TimeoutSeconds, Is.EqualTo(0));
        });
    }

    // ── 6. Guarda de schema da migration M287 ──────────────────────────────

    [Test]
    public void M287_IsDiscoveredWithAUniqueVersionAfterM286()
    {
        var assembly = typeof(M287_AddMcpToolCallLimit).Assembly;
        var versions = assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(Migration).IsAssignableFrom(type))
            .Select(type => type.GetCustomAttribute<MigrationAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Version)
            .ToList();

        Assert.That(versions, Is.Not.Empty);
        Assert.That(versions.Distinct().Count(), Is.EqualTo(versions.Count),
            "versões de migration duplicadas quebrariam o runner");

        var ours = typeof(M287_AddMcpToolCallLimit).GetCustomAttribute<MigrationAttribute>();
        Assert.That(ours, Is.Not.Null, "M287 precisa do atributo [Migration]");
        Assert.That(versions, Does.Contain(ours!.Version));
    }

    [Test]
    public void M287_DeclaresUpAndDown()
    {
        var type = typeof(M287_AddMcpToolCallLimit);

        Assert.Multiple(() =>
        {
            Assert.That(type.GetMethod(nameof(Migration.Up))?.DeclaringType, Is.EqualTo(type),
                "M287 precisa sobrescrever Up()");
            Assert.That(type.GetMethod(nameof(Migration.Down))?.DeclaringType, Is.EqualTo(type),
                "M287 precisa sobrescrever Down() — sem rollback a migration é irreversível");
        });
    }

    /// <summary>
    /// O EF mapeia a coluna por CONVENÇÃO (ApplySnakeCaseFallback), sem
    /// HasColumnName explícito. Um ToSnakeCase que não separe
    /// "McpToolCallsPerMinute" passaria no build e só quebraria no deploy com
    /// 42703. Este teste lê o nome REAL do modelo relacional completo (Npgsql,
    /// sem abrir conexão) e o compara com a migration — mesmo padrão da guarda
    /// da M239.
    /// </summary>
    [Test]
    public void M287_RelationalModelColumnNamesMatchTheMigration()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=model_only;Username=x;Password=x",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        using var db = new FullModelDbContext(options);

        var expected = new (Type Entity, string Table, string Column, bool Nullable)[]
        {
            (typeof(ServerConfiguration), "server_configurations", "mcp_tool_calls_per_minute", false),
            (typeof(ClientConfiguration), "client_configurations", "mcp_tool_calls_per_minute", true),
            (typeof(SiteConfiguration), "site_configurations", "mcp_tool_calls_per_minute", true),
        };

        foreach (var (entityType, table, column, nullable) in expected)
        {
            var entity = db.Model.FindEntityType(entityType);
            Assert.That(entity, Is.Not.Null, $"{entityType.Name} não está no modelo");

            Assert.Multiple(() =>
            {
                Assert.That(entity!.GetTableName(), Is.EqualTo(table), $"tabela de {entityType.Name}");

                var property = entity.FindProperty("McpToolCallsPerMinute");
                Assert.That(property, Is.Not.Null,
                    $"McpToolCallsPerMinute não está mapeada em {entityType.Name} — a migration criaria uma coluna órfã");
                Assert.That(property!.GetColumnName(), Is.EqualTo(column),
                    $"nome REAL da coluna em {table} (a migration usa '{column}')");
                Assert.That(property.IsNullable, Is.EqualTo(nullable),
                    nullable
                        ? $"{table}.{column} é NULLABLE na migration (null = herda)"
                        : $"{table}.{column} é NOT NULL DEFAULT 20 na migration");
                Assert.That(property.GetColumnType(), Is.EqualTo("integer"),
                    $"{table}.{column} é AsInt32() na migration");
            });
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static DiscoveryDbContext CreateConfigDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"mcp-ratelimit-config-{Guid.NewGuid():N}")
            .Options;
        return new ConfigTestDbContext(options);
    }

    private static DiscoveryDbContext NewMcpDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"mcp-ratelimit-catalog-{Guid.NewGuid():N}")
            .Options;
        return new CatalogTestDbContext(options);
    }

    private static ConfigurationService BuildConfigurationService(DiscoveryDbContext db)
    {
        var redis = new NoopRedisService();
        return new ConfigurationService(
            new ServerConfigurationRepository(db, redis, EmptyConfiguration()),
            new ClientConfigurationRepository(db),
            new SiteConfigurationRepository(db),
            null!, // ISiteRepository só é usado quando a config do site ainda não existe
            new NoopAuditService(),
            new NoopResolver(),
            new NoopSecretProtector(),
            redis,
            new NoopNatsValidator());
    }

    /// <summary>Modelo completo (todos os Configure*) com provider Npgsql, sem conexão.</summary>
    private sealed class FullModelDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options);

    /// <summary>Contexto reduzido às configurações (o InMemory não suporta pgvector).</summary>
    private sealed class ConfigTestDbContext(DbContextOptions<DiscoveryDbContext> options)
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
                typeof(ConfigurationAudit),
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

    /// <summary>Contexto reduzido à governança de tools.</summary>
    private sealed class CatalogTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            foreach (var entityType in typeof(McpToolPolicy).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null
                             && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => type != typeof(McpToolPolicy) && type != typeof(Site) && type != typeof(Agent)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<McpToolPolicy>(entity => entity.HasKey(p => p.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(s => s.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(a => a.Id));
        }
    }

    /// <summary>Executor falso: catálogo mínimo de tools de servidor.</summary>
    private sealed class CatalogExecutor : IMcpToolExecutor
    {
        private static readonly IReadOnlyList<(string Name, string Description)> Catalog =
        [
            ("knowledge_search", "Busca na base de conhecimento."),
        ];

        public void RegisterHandler(string toolName, Func<McpToolCallContext, Task<string>> handler) { }

        public Task<string> ExecuteAsync(
            string toolName, string argumentsJson, Guid? clientId, Guid? siteId, Guid? agentId,
            AIIntegrationSettings aiSettings, IReadOnlyCollection<Guid>? excludeArticleIds = null,
            Guid? departmentId = null, Guid? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult("{}");

        public Task<List<LlmTool>> GetAvailableToolsAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(new List<LlmTool>());

        public IReadOnlyList<(string Name, string Description)> GetServerCatalog() => Catalog;

        public Task<bool> IsToolEnabledAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(true);
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

    /// <summary>Repositório de sites mínimo: devolve sempre o site informado.</summary>
    private sealed class FakeSiteRepository(Site site) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id) => Task.FromResult<Site?>(site.Id == id ? site : null);
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) =>
            Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) =>
            Task.FromResult<IEnumerable<Site>>([]);
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) =>
            Task.FromResult<IEnumerable<Site>>([site]);
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) =>
            Task.FromResult<IEnumerable<Site>>([site]);
        public Task<Site> CreateAsync(Site entity) => Task.FromResult(entity);
        public Task UpdateAsync(Site entity) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    /// <summary>Resolvedor com teto global fixo no nível SERVIDOR.</summary>
    private sealed class FixedCeilingResolver(int serverCeiling) : IConfigurationResolver
    {
        public Task<ServerConfiguration> GetServerAsync() =>
            Task.FromResult(new ServerConfiguration { Id = Guid.NewGuid(), McpToolCallsPerMinute = serverCeiling });

        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => Task.FromResult<ClientConfiguration?>(null);
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => Task.FromResult<SiteConfiguration?>(null);
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new NotSupportedException();
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new NotSupportedException();
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();

        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) =>
            Task.FromResult(new ResolvedConfiguration { McpToolCallsPerMinute = serverCeiling });

        public Task<ResolvedConfiguration> ResolveForClientAsync(Guid clientId) =>
            Task.FromResult(new ResolvedConfiguration { McpToolCallsPerMinute = serverCeiling });

        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(Guid? clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ValidateInheritanceAsync() => Task.CompletedTask;
        public void ClearCache() { }
    }

    /// <summary>Resolvedor que sempre falha — exercita o fail-open do runtime.</summary>
    private sealed class ThrowingCeilingResolver : IConfigurationResolver
    {
        public Task<ServerConfiguration> GetServerAsync() => throw new InvalidOperationException("config indisponível");
        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) => throw new InvalidOperationException("config indisponível");
        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) => throw new InvalidOperationException("config indisponível");
        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) => throw new InvalidOperationException("config indisponível");
        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class => throw new InvalidOperationException("config indisponível");
        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new InvalidOperationException("config indisponível");
        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new InvalidOperationException("config indisponível");
        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) => throw new InvalidOperationException("config indisponível");
        public Task<ResolvedConfiguration> ResolveForClientAsync(Guid clientId) => throw new InvalidOperationException("config indisponível");
        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(Guid? clientId, CancellationToken ct = default) => throw new InvalidOperationException("config indisponível");
        public Task ValidateInheritanceAsync() => Task.CompletedTask;
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
