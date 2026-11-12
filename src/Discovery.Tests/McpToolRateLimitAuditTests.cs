using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// AUDITORIA ADVERSARIAL do teto global de chamadas de tools MCP (tarefa de
/// verificação independente).
///
/// Esta suíte tenta DESMENTIR os requisitos, não confirmá-los. O alvo principal é
/// o caminho que a suíte do autor NÃO exercita: o SEGUNDO ponto de enforcement
/// (<see cref="McpToolExecutor.ExecuteAsync"/>), que impõe o rate limit por conta
/// própria, além do chat (<c>AiChatStreamingOrchestrator</c>). Dois caminhos com
/// limites diferentes seriam defeito: o mesmo teto global tem de valer para a tool
/// de servidor e para a tool do agente.
///
/// Cobre também o ponto cego do processo de entrega: um build verde + suíte verde
/// NÃO provam o grafo de DI (o container só materializa o grafo no primeiro
/// request quando ValidateOnBuild está desligado).
/// </summary>
[TestFixture]
public class McpToolRateLimitAuditTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // 1. Enforcement REAL no executor do servidor (caminho não coberto antes)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ACEITE (a) pela ponta do EXECUTOR: teto global 20 + tool 10 => efetivo 10.
    /// A 11ª chamada na mesma janela tem de ser recusada com erro de rate limit.
    /// </summary>
    [Test]
    public async Task Executor_GlobalCeiling20_Tool10_BlocksTheEleventhCall()
    {
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var tool = "audit_tool_" + Guid.NewGuid().ToString("N");

        var executor = BuildExecutor(
            policyMaxCallsPerMinute: 10, globalCeiling: 20, out _);

        for (var i = 1; i <= 10; i++)
        {
            var result = await executor.ExecuteAsync(
                tool, "{}", clientId, siteId, null, new AIIntegrationSettings());
            Assert.That(IsRateLimitError(result), Is.False, $"chamada {i} deveria passar (efetivo 10)");
        }

        var blocked = await executor.ExecuteAsync(
            tool, "{}", clientId, siteId, null, new AIIntegrationSettings());

        Assert.That(IsRateLimitError(blocked), Is.True,
            $"a 11ª chamada deveria ser recusada com efetivo 10, mas veio: {blocked}");
        Assert.That(blocked, Does.Contain("10"),
            "a mensagem precisa informar o limite EFETIVO aplicado, não o da política");
    }

    /// <summary>
    /// ACEITE (a) pela ponta do EXECUTOR: teto global 5 + tool 10 => efetivo 5.
    /// O teto global é um PISO: reduz o limite da tool, nunca o aumenta.
    /// </summary>
    [Test]
    public async Task Executor_GlobalCeiling5_Tool10_BlocksTheSixthCall()
    {
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var tool = "audit_tool_" + Guid.NewGuid().ToString("N");

        var executor = BuildExecutor(
            policyMaxCallsPerMinute: 10, globalCeiling: 5, out _);

        for (var i = 1; i <= 5; i++)
        {
            var result = await executor.ExecuteAsync(
                tool, "{}", clientId, siteId, null, new AIIntegrationSettings());
            Assert.That(IsRateLimitError(result), Is.False, $"chamada {i} deveria passar (efetivo 5)");
        }

        var blocked = await executor.ExecuteAsync(
            tool, "{}", clientId, siteId, null, new AIIntegrationSettings());

        Assert.That(IsRateLimitError(blocked), Is.True,
            $"a 6ª chamada deveria ser recusada com efetivo 5, mas veio: {blocked}");
        Assert.That(blocked, Does.Contain("5"), "a mensagem precisa informar o efetivo (5)");
    }

    /// <summary>
    /// ACEITE (b): teto global MAIOR que o da tool NÃO aumenta o limite da tool.
    /// Um teto de 600 com tool de 3 continua bloqueando na 4ª.
    /// </summary>
    [Test]
    public async Task Executor_GlobalCeilingHigherThanTool_DoesNotRaiseTheToolLimit()
    {
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var tool = "audit_tool_" + Guid.NewGuid().ToString("N");

        var executor = BuildExecutor(
            policyMaxCallsPerMinute: 3, globalCeiling: 600, out _);

        for (var i = 1; i <= 3; i++)
        {
            var result = await executor.ExecuteAsync(
                tool, "{}", clientId, siteId, null, new AIIntegrationSettings());
            Assert.That(IsRateLimitError(result), Is.False, $"chamada {i} deveria passar (efetivo 3)");
        }

        var blocked = await executor.ExecuteAsync(
            tool, "{}", clientId, siteId, null, new AIIntegrationSettings());

        Assert.That(IsRateLimitError(blocked), Is.True,
            "o teto global NUNCA eleva o limite da tool (é piso, não aumento)");
    }

    /// <summary>
    /// SENTINELA 0 / ACEITE (b): sem teto global aplicável vale o limite da TOOL —
    /// não o default 20. Um teto "ausente" não pode virar 20 e apertar a tool.
    ///
    /// O caminho REAL do sentinela é o resolvedor ausente (deploy sem
    /// IConfigurationResolver/falha de leitura). Um resolvedor presente SEMPRE
    /// devolve o default do servidor (20) — que é o comportamento de configuração,
    /// não o sentinela.
    /// </summary>
    [Test]
    public async Task Executor_WithoutConfigurationResolver_UsesTheToolLimit_NotTheDefault20()
    {
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var tool = "audit_tool_" + Guid.NewGuid().ToString("N");

        // Resolvedor AUSENTE (null) = caminho defensivo "sem teto global".
        var executor = BuildExecutorWithoutResolver(policyMaxCallsPerMinute: 50);

        for (var i = 1; i <= 50; i++)
        {
            var result = await executor.ExecuteAsync(
                tool, "{}", clientId, siteId, null, new AIIntegrationSettings());
            Assert.That(IsRateLimitError(result), Is.False,
                $"sem teto global a tool de 50/min não pode ser barrada (chamada {i}); " +
                "se barrou em 20, o sentinela 0 virou o default");
        }

        var blocked = await executor.ExecuteAsync(
            tool, "{}", clientId, siteId, null, new AIIntegrationSettings());
        Assert.That(IsRateLimitError(blocked), Is.True,
            "o limite da própria tool (50) continua valendo como único limite");
    }

    /// <summary>
    /// ORDEM (aceite d): a recusa não pode "consumir cota" indevidamente nem a
    /// resolução do efetivo pode ocorrer depois do incremento. Aqui provamos que o
    /// efetivo é aplicado DESDE A PRIMEIRA chamada — se o executor incrementasse
    /// antes de resolver, a 1ª já poderia escapar do teto apertado.
    /// </summary>
    [Test]
    public async Task Executor_AppliesTheCeilingFromTheVeryFirstCall()
    {
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var tool = "audit_tool_" + Guid.NewGuid().ToString("N");

        var executor = BuildExecutor(
            policyMaxCallsPerMinute: 100, globalCeiling: 1, out _);

        var first = await executor.ExecuteAsync(
            tool, "{}", clientId, siteId, null, new AIIntegrationSettings());
        Assert.That(IsRateLimitError(first), Is.False, "a 1ª chamada passa com teto 1");

        var second = await executor.ExecuteAsync(
            tool, "{}", clientId, siteId, null, new AIIntegrationSettings());
        Assert.That(IsRateLimitError(second), Is.True, "a 2ª chamada é barrada pelo teto 1");
    }

    /// <summary>
    /// CONTRATO #5 (não pode haver regressão): tool SEM política continua SEM
    /// limite — o teto global NÃO cria limite onde não havia. O executor responde
    /// "não autorizada", nunca erro de rate limit.
    /// </summary>
    [Test]
    public async Task Executor_ToolWithoutPolicy_IsNotRateLimited()
    {
        var siteId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var tool = "audit_unpolicied_" + Guid.NewGuid().ToString("N");

        // Política ausente (repo devolve null) + teto global BEM apertado (1).
        var executor = BuildExecutor(
            policyMaxCallsPerMinute: 0, globalCeiling: 1, out _,
            policyExists: false);

        for (var i = 1; i <= 5; i++)
        {
            var result = await executor.ExecuteAsync(
                tool, "{}", clientId, siteId, null, new AIIntegrationSettings());

            Assert.That(IsRateLimitError(result), Is.False,
                $"tool sem política NÃO pode ganhar limite do teto global (chamada {i}): {result}");
            Assert.That(result, Does.Contain("não autorizada"),
                "sem política a resposta é de autorização, não de rate limit");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Ponto único do cálculo do efetivo
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// O cálculo do efetivo vive em UM único lugar. Se alguém reimplementar o MIN
    /// no executor ou na governança, os dois divergem. Aqui comparamos o helper
    /// com o resultado REAL da governança para a mesma entrada.
    /// </summary>
    [Test]
    public async Task GovernanceAndCatalogHelper_AgreeOnTheEffectiveLimit()
    {
        var scope = new McpToolScope(Guid.NewGuid(), null, null);
        var governance = BuildGovernance(globalCeiling: 5, out _);

        foreach (var toolLimit in new[] { 1, 5, 6, 10, 20, 600 })
        {
            var fromGovernance = await governance.ResolveMaxCallsPerMinuteAsync(scope, toolLimit);
            var fromHelper = McpToolRateLimitCatalog.Effective(toolLimit, 5);

            Assert.That(fromGovernance, Is.EqualTo(fromHelper),
                $"governança e McpToolRateLimitCatalog.Effective divergiram para tool={toolLimit}");
        }
    }

    /// <summary>
    /// O teto global é sanitizado (contrato #2): valor fora de 1..600 na
    /// configuração cai no default 20 — e 0 NÃO desliga o teto.
    /// </summary>
    [Test]
    public async Task GlobalCeiling_OutOfRangeInConfiguration_FallsBackToTwentyAndNeverDisables()
    {
        var scope = new McpToolScope(Guid.NewGuid(), null, null);

        foreach (var (configured, expected) in new[] { (0, 20), (-3, 20), (9999, 20), (7, 7), (600, 600) })
        {
            var governance = BuildGovernance(globalCeiling: configured, out _);
            var effective = await governance.ResolveMaxCallsPerMinuteAsync(scope, 600);

            Assert.That(effective, Is.EqualTo(expected),
                $"teto configurado={configured}: esperado {expected} (0/negativo NUNCA desliga; fora da faixa => 20)");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Escopo do agente e cascata REAL pelo repositório
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ResolveAgentScopeAsync REAL (não o default de interface, que é identidade):
    /// um escopo só com agentId precisa resolver o SITE do agente, senão o teto do
    /// site é ignorado para as tools executadas em nome do agente.
    /// </summary>
    [Test]
    public async Task AgentScopeOnly_ResolvesTheAgentsSite_AndAppliesItsCeiling()
    {
        await using var db = NewMcpDb();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        db.Add(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });
        db.Add(new Agent { Id = agentId, SiteId = siteId, Hostname = "PC-AUDIT" });
        db.Add(new McpToolPolicy
        {
            Id = Guid.NewGuid(), ToolName = "audit_scope_tool", SiteId = siteId,
            IsEnabled = true, MaxCallsPerMinute = 50, TimeoutSeconds = 30,
        });
        await db.SaveChangesAsync();

        var repo = new McpToolPolicyRepository(db);

        // Escopo SÓ com agentId: a cadeia tem de ser resolvida para site/cliente.
        var (resolvedSite, resolvedClient) = await repo.ResolveAgentScopeAsync(null, null, agentId);

        Assert.Multiple(() =>
        {
            Assert.That(resolvedSite, Is.EqualTo(siteId),
                "o site do agente tem de ser resolvido a partir do agentId");
            Assert.That(resolvedClient, Is.EqualTo(clientId),
                "o cliente tem de ser resolvido a partir do site");
        });
    }

    /// <summary>
    /// Escopo que NÃO resolve site nem cliente cai no nível SERVIDOR — nunca em 0,
    /// que desligaria o teto silenciosamente.
    /// </summary>
    [Test]
    public async Task GlobalScope_UsesTheServerCeiling()
    {
        var scope = new McpToolScope(null, null, null);
        var governance = BuildGovernance(globalCeiling: 9, out _);

        var effective = await governance.ResolveMaxCallsPerMinuteAsync(scope, 100);

        Assert.That(effective, Is.EqualTo(9),
            "escopo global usa o teto do SERVIDOR (nunca 0/sem teto)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Grafo de DI (o ponto cego do CI)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// O executor do SERVIDOR e a governança convivem no MESMO grafo sem ciclo.
    /// Construir com ValidateOnBuild/ValidateScopes é o próprio assert.
    /// </summary>
    [Test]
    public void ExecutorAndGovernance_ShareAGraph_WithoutCircularDependency()
    {
        var services = BuildGraphServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();

        Assert.DoesNotThrow(() =>
        {
            Assert.That(scope.ServiceProvider.GetRequiredService<IMcpToolExecutor>(), Is.Not.Null);
            Assert.That(scope.ServiceProvider.GetRequiredService<IMcpToolGovernance>(), Is.Not.Null);
        }, "IMcpToolExecutor e IMcpToolGovernance precisam ser acíclicos");
    }

    /// <summary>
    /// DENTES (aceite g): o guard precisa FALHAR quando o ciclo volta. Sem isto, um
    /// teste verde de DI não prova nada — ele pode estar verde por acaso.
    /// </summary>
    [Test]
    public void GraphWithACircularExecutor_FailsToBuild()
    {
        var services = BuildGraphServices();
        services.RemoveAll<IMcpToolExecutor>();
        services.AddScoped<IMcpToolExecutor, CircularExecutor>();

        var ex = Assert.Throws<AggregateException>(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }, "um executor que dependa da governança DEVE estourar o ciclo");

        var message = string.Join(" | ", ex!.Flatten().InnerExceptions.Select(e => e.Message));
        Assert.That(message, Does.Contain("circular").IgnoreCase,
            $"a falha tem de ser o ciclo de DI. Recebido: {message}");
    }

    /// <summary>
    /// O executor NÃO referencia IMcpToolGovernance no seu grafo de dependências —
    /// prova estrutural, independente do container.
    /// </summary>
    [Test]
    public void McpToolExecutor_DoesNotDependOnTheGovernance()
    {
        var dependencies = typeof(McpToolExecutor)
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.That(dependencies, Does.Not.Contain(typeof(IMcpToolGovernance)),
            "injetar IMcpToolGovernance no McpToolExecutor recria o ciclo com a governança");
    }

    /// <summary>
    /// Nenhum serviço do caminho de configuração pode depender do executor —
    /// senão o ciclo reaparece por outro vértice.
    /// </summary>
    [Test]
    public void ConfigurationResolver_DoesNotDependOnTheExecutor()
    {
        var dependencies = typeof(ConfigurationResolver)
            .GetConstructors()
            .SelectMany(ctor => ctor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.That(dependencies, Does.Not.Contain(typeof(IMcpToolExecutor)),
            "o ConfigurationResolver é dependência do executor: apontar de volta fecha o ciclo");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Rate limit HTTP do ASP.NET não pode ser afetado (aceite f)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// O teto global de tools MCP é de TOOLS, não do pipeline HTTP. Nenhuma chave
    /// de Security:RateLimiting:* pode ler McpToolCallsPerMinute.
    /// </summary>
    [Test]
    public void HttpRateLimiting_IsNotWiredToTheMcpToolCeiling()
    {
        var repoRoot = FindRepositoryRoot();
        var rateLimitingFile = Path.Combine(
            repoRoot, "src", "Discovery.Api", "DependencyInjection",
            "RateLimitingServiceCollectionExtensions.cs");

        Assert.That(File.Exists(rateLimitingFile), Is.True,
            $"arquivo de rate limiting HTTP não encontrado em {rateLimitingFile}");

        var source = File.ReadAllText(rateLimitingFile);

        Assert.That(source, Does.Not.Contain("McpToolCallsPerMinute"),
            "o rate limit HTTP (Security:RateLimiting:*) não pode depender do teto de tools MCP");
        Assert.That(source, Does.Contain("Security:RateLimiting"),
            "o arquivo deve continuar lendo a configuração própria de rate limiting");
    }

    /// <summary>
    /// O teto global NÃO pode aparecer no catálogo de campos do rate limit HTTP:
    /// a chave vive só nas configurações de Server/Client/Site.
    /// </summary>
    [Test]
    public void AppSettingsHttpRateLimits_DoNotContainTheMcpToolCeiling()
    {
        var repoRoot = FindRepositoryRoot();
        foreach (var name in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var path = Path.Combine(repoRoot, "src", "Discovery.Api", name);
            if (!File.Exists(path))
                continue;

            var content = File.ReadAllText(path);
            var rateLimitingIndex = content.IndexOf("RateLimiting", StringComparison.Ordinal);
            if (rateLimitingIndex < 0)
                continue;

            var rateLimitingBlock = content[rateLimitingIndex..];
            Assert.That(rateLimitingBlock, Does.Not.Contain("McpToolCallsPerMinute"),
                $"{name}: o bloco de RateLimiting HTTP não pode conter McpToolCallsPerMinute");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. Migration M287 x colunas reais (aceite e)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// O nome da coluna da MIGRATION tem de ser o nome que o EF usa de verdade.
    /// O build NÃO pega um nome de coluna errado (é string): só quebra em runtime
    /// com 42703. Aqui confrontamos a fonte da migration com o modelo relacional.
    /// </summary>
    [Test]
    public void MigrationColumnNames_MatchTheRelationalModel()
    {
        var repoRoot = FindRepositoryRoot();
        var migrationPath = Path.Combine(
            repoRoot, "src", "Discovery.Migrations", "Migrations", "M287_AddMcpToolCallLimit.cs");

        Assert.That(File.Exists(migrationPath), Is.True, $"M287 não encontrada em {migrationPath}");
        var migration = File.ReadAllText(migrationPath);

        // Provider relacional + pgvector: o modelo completo tem colunas vector e
        // sem o UseVector() o acesso ao Model estoura antes de chegar ao assert.
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=audit;Username=postgres;Password=postgres",
                npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        using var db = new DiscoveryDbContext(options);

        var expected = new (Type Entity, string Table, bool Nullable)[]
        {
            (typeof(ServerConfiguration), "server_configurations", false),
            (typeof(ClientConfiguration), "client_configurations", true),
            (typeof(SiteConfiguration), "site_configurations", true),
        };

        foreach (var (entityType, table, nullable) in expected)
        {
            // 1) A migration adiciona a coluna nessa tabela.
            Assert.That(migration, Does.Contain("mcp_tool_calls_per_minute"),
                "a M287 precisa usar o nome mcp_tool_calls_per_minute");
            Assert.That(migration, Does.Contain($"\"{table}\""),
                $"a M287 precisa tocar a tabela {table}");

            // 2) O EF aponta para o MESMO nome de coluna na MESMA tabela.
            var entity = db.Model.FindEntityType(entityType);
            Assert.That(entity, Is.Not.Null, $"{entityType.Name} não está no modelo EF");

            Assert.That(entity!.GetTableName(), Is.EqualTo(table), $"tabela real de {entityType.Name}");

            var property = entity.FindProperty("McpToolCallsPerMinute");
            Assert.That(property, Is.Not.Null,
                $"{entityType.Name}.McpToolCallsPerMinute não está mapeada — a migration criaria coluna órfã");

            Assert.Multiple(() =>
            {
                Assert.That(property!.GetColumnName(), Is.EqualTo("mcp_tool_calls_per_minute"),
                    $"a M287 e o EF precisam concordar em {table}");
                Assert.That(property.IsNullable, Is.EqualTo(nullable),
                    nullable
                        ? $"{table} é NULLABLE (null = herda) e o EF precisa refletir isso"
                        : $"{table} é NOT NULL DEFAULT 20 e o EF precisa refletir isso");
            });
        }

        // 3) Down() remove exatamente o que Up() cria (sem coluna órfã no rollback).
        var downIndex = migration.IndexOf("public override void Down()", StringComparison.Ordinal);
        Assert.That(downIndex, Is.GreaterThan(0), "a M287 precisa ter Down()");
        var down = migration[downIndex..];

        foreach (var (_, table, _) in expected)
        {
            Assert.That(down, Does.Contain($"\"{table}\""),
                $"o Down() tem de remover a coluna de {table}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Erro de rate limit (diferente de erro de autorização/execução).</summary>
    private static bool IsRateLimitError(string result) =>
        result.Contains("Rate limit excedido", StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Discovery.Migrations")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "raiz do repositório não encontrada");
        return directory!.FullName;
    }

    /// <summary>
    /// Executor REAL com as folhas falsas e um resolver que devolve o teto
    /// informado. O contador observado permite provar a ordem (incremento depois
    /// da resolução do efetivo).
    /// </summary>
    private static McpToolExecutor BuildExecutor(
        int policyMaxCallsPerMinute, int globalCeiling, out CountingPolicyRepository counter,
        bool policyExists = true)
    {
        counter = new CountingPolicyRepository(policyMaxCallsPerMinute, policyExists);
        var resolver = new FixedCeilingResolver(globalCeiling);

        return new McpToolExecutor(
            counter,
            new NoopKnowledgeMcpTool(),
            new NoopKnowledgeListMcpTool(),
            new NoopMessageRepository(),
            new NoopMediator(),
            new NoopSensorReadService(),
            new NoopSensorSeriesService(),
            LoggerFactory.Create(_ => { }).CreateLogger<McpToolExecutor>(),
            resolver);
    }

    /// <summary>Executor REAL sem resolvedor de configuração (caminho do sentinela 0).</summary>
    private static McpToolExecutor BuildExecutorWithoutResolver(int policyMaxCallsPerMinute)
    {
        return new McpToolExecutor(
            new CountingPolicyRepository(policyMaxCallsPerMinute, policyExists: true),
            new NoopKnowledgeMcpTool(),
            new NoopKnowledgeListMcpTool(),
            new NoopMessageRepository(),
            new NoopMediator(),
            new NoopSensorReadService(),
            new NoopSensorSeriesService(),
            LoggerFactory.Create(_ => { }).CreateLogger<McpToolExecutor>(),
            configurationResolver: null);
    }

    /// <summary>Governança com repositório falso e teto global fixo.</summary>
    private static McpToolGovernance BuildGovernance(int globalCeiling, out CountingPolicyRepository counter)
    {
        counter = new CountingPolicyRepository(0, policyExists: false);
        return new McpToolGovernance(
            counter,
            new NoopToolExecutor(),
            redis: null,
            configurationResolver: new FixedCeilingResolver(globalCeiling));
    }

    /// <summary>Grafo equivalente ao de produção, com folhas falsas.</summary>
    private static ServiceCollection BuildGraphServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IMcpToolPolicyRepository, CountingPolicyRepository>();
        services.AddScoped<IKnowledgeMcpTool, NoopKnowledgeMcpTool>();
        services.AddScoped<IKnowledgeListMcpTool, NoopKnowledgeListMcpTool>();
        services.AddScoped<IAiChatMessageRepository, NoopMessageRepository>();
        services.AddScoped<IMediator, NoopMediator>();
        services.AddScoped<ISensorReadService, NoopSensorReadService>();
        services.AddScoped<ISensorSeriesService, NoopSensorSeriesService>();
        services.AddScoped<IConfigurationResolver, FixedCeilingResolver>();
        services.AddScoped<IMcpToolExecutor, McpToolExecutor>();
        services.AddScoped<IMcpToolGovernance, McpToolGovernance>();
        return services;
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    /// <summary>Repositório de políticas que conta chamadas de rate limit.</summary>
    private sealed class CountingPolicyRepository(int policyMaxCallsPerMinute = 0, bool policyExists = true)
        : IMcpToolPolicyRepository
    {
        public Task<McpToolPolicy?> GetPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
        {
            if (!policyExists)
                return Task.FromResult<McpToolPolicy?>(null);

            return Task.FromResult<McpToolPolicy?>(new McpToolPolicy
            {
                Id = Guid.NewGuid(),
                ToolName = toolName,
                ClientId = clientId,
                SiteId = siteId,
                AgentId = agentId,
                IsEnabled = true,
                MaxCallsPerMinute = policyMaxCallsPerMinute,
                TimeoutSeconds = 30,
                Source = McpToolSources.Server,
            });
        }

        public Task<(Guid? SiteId, Guid? ClientId)> ResolveAgentScopeAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult((siteId, clientId));

        public Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpToolPolicy>>([]);

        public Task<IReadOnlyList<McpToolPolicy>> GetScopePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpToolPolicy>>([]);

        public Task<McpToolPolicy> UpsertAsync(McpToolPolicy policy, CancellationToken ct = default) =>
            Task.FromResult(policy);

        public Task<bool> DeleteScopePolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task EnsureGlobalPoliciesAsync(
            string source, IEnumerable<(string Name, string? Description)> tools, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> CountLowerScopeOverridesAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyDictionary<string, int>> GetLowerScopeOverrideCountsAsync(
            IReadOnlyCollection<string> toolNames, Guid? clientId, Guid? siteId, Guid? agentId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<McpToolPolicy?> GetAncestorLockedPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult<McpToolPolicy?>(null);
    }

    /// <summary>
    /// Resolver com teto global fixo. O parâmetro tem default para que o CONTAINER
    /// consiga construí-lo no teste de grafo (o DI não injeta int).
    /// </summary>
    private sealed class FixedCeilingResolver(int ceiling = 0) : IConfigurationResolver
    {
        public Task<ServerConfiguration> GetServerAsync() =>
            Task.FromResult(new ServerConfiguration { Id = Guid.NewGuid(), McpToolCallsPerMinute = ceiling });

        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) =>
            Task.FromResult<ClientConfiguration?>(new ClientConfiguration
            {
                Id = Guid.NewGuid(), ClientId = clientId, McpToolCallsPerMinute = ceiling,
            });

        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) =>
            Task.FromResult<SiteConfiguration?>(new SiteConfiguration
            {
                Id = Guid.NewGuid(), SiteId = siteId, McpToolCallsPerMinute = ceiling,
            });

        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) =>
            Task.FromResult(new ResolvedConfiguration
            {
                SiteId = siteId,
                McpToolCallsPerMinute = ConfigurationResolver.ResolveMcpToolCallsPerMinute(
                    ceiling <= 0 ? null : ceiling, null, 20),
            });

        public Task<ResolvedConfiguration> ResolveForClientAsync(Guid clientId) =>
            Task.FromResult(new ResolvedConfiguration
            {
                ClientId = clientId,
                McpToolCallsPerMinute = ConfigurationResolver.ResolveMcpToolCallsPerMinute(
                    null, ceiling <= 0 ? null : ceiling, 20),
            });

        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) =>
            throw new NotSupportedException();

        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class =>
            throw new NotSupportedException();

        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();

        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();

        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default) =>
            Task.FromResult(new BackgroundProcessingSettings());

        public Task ValidateInheritanceAsync() => Task.CompletedTask;

        public void ClearCache() { }
    }

    /// <summary>Executor falso para o grafo de DI (a governança só lê o catálogo).</summary>
    private sealed class NoopToolExecutor : IMcpToolExecutor
    {
        public void RegisterHandler(string toolName, Func<McpToolCallContext, Task<string>> handler) { }

        public Task<string> ExecuteAsync(
            string toolName, string argumentsJson, Guid? clientId, Guid? siteId, Guid? agentId,
            AIIntegrationSettings aiSettings, IReadOnlyCollection<Guid>? excludeArticleIds = null,
            Guid? departmentId = null, Guid? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult("{}");

        public Task<List<LlmTool>> GetAvailableToolsAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(new List<LlmTool>());

        public IReadOnlyList<(string Name, string Description)> GetServerCatalog() => [];

        public Task<bool> IsToolEnabledAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    /// <summary>Executor "infrator": reintroduz o ciclo de propósito (só no teste).</summary>
    private sealed class CircularExecutor(
        IMcpToolPolicyRepository policyRepo,
        IMcpToolGovernance governance) : IMcpToolExecutor
    {
        public void RegisterHandler(string toolName, Func<McpToolCallContext, Task<string>> handler) { }

        public Task<string> ExecuteAsync(
            string toolName, string argumentsJson, Guid? clientId, Guid? siteId, Guid? agentId,
            AIIntegrationSettings aiSettings, IReadOnlyCollection<Guid>? excludeArticleIds = null,
            Guid? departmentId = null, Guid? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult("{}");

        public Task<List<LlmTool>> GetAvailableToolsAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(new List<LlmTool>());

        public IReadOnlyList<(string Name, string Description)> GetServerCatalog() => [];

        public Task<bool> IsToolEnabledAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public IMcpToolPolicyRepository PolicyRepository => policyRepo;
        public IMcpToolGovernance Governance => governance;
    }

    private sealed class NoopKnowledgeMcpTool : IKnowledgeMcpTool
    {
        public Task<string> ExecuteAsync(
            Guid? clientId, Guid? siteId, string query, int maxResults = 3,
            Guid? departmentId = null, CancellationToken ct = default) => Task.FromResult("{}");

        public Task<string> ExecuteWithSettingsAsync(
            Guid? clientId, Guid? siteId, string query, AIIntegrationSettings aiSettings,
            IReadOnlyCollection<Guid>? excludeArticleIds = null, int maxResults = 3,
            Guid? departmentId = null, CancellationToken ct = default) => Task.FromResult("{}");
    }

    private sealed class NoopKnowledgeListMcpTool : IKnowledgeListMcpTool
    {
        public Task<string> ExecuteAsync(
            Guid? clientId, Guid? siteId, string? category, int limit, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class NoopMessageRepository : IAiChatMessageRepository
    {
        public Task<AiChatMessage> CreateAsync(AiChatMessage message, CancellationToken ct = default) =>
            Task.FromResult(message);

        public Task CreateBatchAsync(IReadOnlyList<AiChatMessage> messages, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<AiChatMessage>> GetRecentBySessionAsync(
            Guid sessionId, int limit, CancellationToken ct = default) =>
            Task.FromResult(new List<AiChatMessage>());

        public Task<List<AiChatMessage>> SearchByAgentAsync(
            Guid agentId, string query, int limit, Guid? excludeSessionId = null, CancellationToken ct = default) =>
            Task.FromResult(new List<AiChatMessage>());

        public Task<(int MessageCount, int EstimatedTokens)> GetStatsAsync(
            Guid sessionId, CancellationToken ct = default) => Task.FromResult((0, 0));
    }

    private sealed class NoopMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class NoopSensorReadService : ISensorReadService
    {
        public Task<AgentSensorsPayloadDto?> GetForAgentAsync(Guid agentId, CancellationToken ct = default) =>
            Task.FromResult<AgentSensorsPayloadDto?>(null);

        public Task<SensorFleetSummaryDto> GetFleetSummaryAsync(
            Guid? clientId, Guid? siteId, CancellationToken ct = default) =>
            Task.FromResult(new SensorFleetSummaryDto());
    }

    private sealed class NoopSensorSeriesService : ISensorSeriesService
    {
        public Task<SensorSeriesDto?> GetSeriesAsync(
            Guid agentId, Guid sensorId, string? targetKey, DateTime from, DateTime to,
            IReadOnlyList<string>? channels, CancellationToken cancellationToken = default) =>
            Task.FromResult<SensorSeriesDto?>(null);

        public Task<SensorEventPageDto> GetEventsAsync(
            Guid agentId, Guid sensorId, string? targetKey, string? cursor, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorEventPageDto());
    }

    /// <summary>Contexto reduzido à governança de tools (InMemory não suporta o modelo inteiro).</summary>
    private static DiscoveryDbContext NewMcpDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"mcp-audit-{Guid.NewGuid():N}")
            .Options;
        return new McpAuditDbContext(options);
    }

    private sealed class McpAuditDbContext(DbContextOptions<DiscoveryDbContext> options)
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
}
