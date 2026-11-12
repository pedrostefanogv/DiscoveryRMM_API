using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Guarda do GRAFO DE INJEÇÃO DE DEPENDÊNCIA da governança de MCP tools.
///
/// O defeito que este teste previne: injetar <see cref="IMcpToolGovernance"/> no
/// construtor do <see cref="McpToolExecutor"/> fecha o ciclo
///
///     IMcpToolExecutor -> IMcpToolGovernance -> IMcpToolExecutor
///
/// e o Microsoft DI estoura em RUNTIME (InvalidOperationException, "A circular
/// dependency was detected") no primeiro request do chat. Um parâmetro com valor
/// default NÃO protege: o container só usa o default quando o serviço NÃO está
/// registrado, e IMcpToolGovernance está registrado no Program.cs.
///
/// Por que um teste dedicado: o build passa, os testes unitários passam (eles
/// constroem os objetos À MÃO, sem container) e a API só quebra em runtime. A
/// construção do provider com ValidateOnBuild=true é o próprio assert.
/// </summary>
[TestFixture]
public class McpToolDependencyGraphTests
{
    /// <summary>
    /// Caminho FELIZ: o grafo real (executor + governança) resolve em um escopo.
    /// Se alguém reintroduzir a dependência circular, ValidateOnBuild derruba a
    /// construção aqui.
    /// </summary>
    [Test]
    public void ExecutorAndGovernance_ResolveFromTheContainer_WithoutCircularDependency()
    {
        var services = BuildProductionGraph();

        // ValidateOnBuild: materializa o grafo e valida TODOS os serviços no build.
        // ValidateScopes: impede que um serviço scoped seja capturado pelo root.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        // ARMADILHA: serviços scoped NÃO podem ser resolvidos do provider RAIZ —
        // com ValidateScopes=true isso lança "Cannot resolve scoped service from
        // root provider" e o teste falharia por um motivo ERRADO (mascarando o
        // ciclo que ele existe para pegar). Sempre abrir um escopo primeiro.
        using var scope = provider.CreateScope();

        Assert.DoesNotThrow(() =>
        {
            var executor = scope.ServiceProvider.GetRequiredService<IMcpToolExecutor>();
            Assert.That(executor, Is.Not.Null);

            var governance = scope.ServiceProvider.GetRequiredService<IMcpToolGovernance>();
            Assert.That(governance, Is.Not.Null);
        }, "o grafo IMcpToolExecutor + IMcpToolGovernance precisa ser acíclico");
    }

    /// <summary>
    /// O grafo é resolvível de verdade: a governança obtida do container funciona
    /// e o executor real não depende dela. Sem isto, o teste acima poderia passar
    /// com um grafo que resolve mas não serve para nada.
    /// </summary>
    [Test]
    public async Task GovernanceFromTheContainer_ActuallyResolvesTheEffectiveLimit()
    {
        var services = BuildProductionGraph();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();

        var governance = scope.ServiceProvider.GetRequiredService<IMcpToolGovernance>();

        // Sem teto global no resolvedor falso => vale o limite da tool.
        var effective = await governance.ResolveMaxCallsPerMinuteAsync(
            new Discovery.Core.DTOs.McpToolScope(null, null, null), policyMaxCallsPerMinute: 7);

        Assert.That(effective, Is.EqualTo(7),
            "a governança resolvida pelo container precisa operar normalmente");
    }

    /// <summary>
    /// DENTES DO GUARD: um executor que RECEBE IMcpToolGovernance no construtor
    /// precisa fazer a construção do provider FALHAR. Isto documenta a proibição
    /// explicitamente — sem este teste, o guard acima poderia passar por acaso
    /// (ex.: se a governança deixasse de ser registrada).
    /// </summary>
    [Test]
    public void AnExecutorThatDependsOnGovernance_MakesTheContainerFail()
    {
        var services = BuildProductionGraph();
        // Substitui o executor pelo "infrator": mesmo ciclo que queremos proibir.
        services.RemoveAll<IMcpToolExecutor>();
        services.AddScoped<IMcpToolExecutor, CircularDependencyExecutor>();

        var ex = Assert.Throws<AggregateException>(() =>
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }, "um executor que dependa de IMcpToolGovernance DEVE estourar o ciclo");

        var mensagens = string.Join(" | ", Flatten(ex!).Select(e => e.Message));
        Assert.That(mensagens, Does.Contain("circular").IgnoreCase,
            $"a falha precisa ser o ciclo de DI, e não outro erro. Recebido: {mensagens}");
    }

    private static IEnumerable<Exception> Flatten(AggregateException ex)
    {
        foreach (var inner in ex.Flatten().InnerExceptions)
            yield return inner;
    }

    /// <summary>
    /// Grafo equivalente ao de produção (Program.cs) com as dependências FALSAS:
    /// o que importa para o teste é a FORMA do grafo (quem depende de quem), não
    /// o comportamento das folhas.
    /// </summary>
    private static ServiceCollection BuildProductionGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Folhas falsas.
        services.AddScoped<IMcpToolPolicyRepository, FakePolicyRepository>();
        services.AddScoped<IKnowledgeMcpTool, NoopKnowledgeMcpTool>();
        services.AddScoped<IKnowledgeListMcpTool, NoopKnowledgeListMcpTool>();
        services.AddScoped<IAiChatMessageRepository, NoopMessageRepository>();
        services.AddScoped<IMediator, NoopMediator>();
        services.AddScoped<ISensorReadService, NoopSensorReadService>();
        services.AddScoped<ISensorSeriesService, NoopSensorSeriesService>();
        services.AddScoped<IConfigurationResolver, NoopConfigurationResolver>();

        // O grafo sob teste: executor NÃO depende da governança; a governança
        // depende do executor (é o que permite aplicar o rate limit na execução).
        services.AddScoped<IMcpToolExecutor, McpToolExecutor>();
        services.AddScoped<IMcpToolGovernance, McpToolGovernance>();

        return services;
    }

    /// <summary>Executor "infrator": reintroduz o ciclo de propósito (só no teste).</summary>
    private sealed class CircularDependencyExecutor(
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

        // Referências usadas só para o container enxergar a dependência.
        public IMcpToolPolicyRepository PolicyRepository => policyRepo;
        public IMcpToolGovernance Governance => governance;
    }

    // ── Fakes (folhas do grafo) ─────────────────────────────────────────────

    private sealed class FakePolicyRepository : IMcpToolPolicyRepository
    {
        public Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpToolPolicy>>([]);

        public Task<McpToolPolicy?> GetPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult<McpToolPolicy?>(null);

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

    private sealed class NoopKnowledgeMcpTool : IKnowledgeMcpTool
    {
        public Task<string> ExecuteAsync(
            Guid? clientId, Guid? siteId, string query, int maxResults = 3,
            Guid? departmentId = null, CancellationToken ct = default) =>
            Task.FromResult("{}");

        public Task<string> ExecuteWithSettingsAsync(
            Guid? clientId, Guid? siteId, string query, AIIntegrationSettings aiSettings,
            IReadOnlyCollection<Guid>? excludeArticleIds = null, int maxResults = 3,
            Guid? departmentId = null, CancellationToken ct = default) =>
            Task.FromResult("{}");
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
            Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult((0, 0));
    }

    private sealed class NoopMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest =>
            throw new NotSupportedException();

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
            where TNotification : INotification =>
            Task.CompletedTask;
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

    private sealed class NoopConfigurationResolver : IConfigurationResolver
    {
        public Task<ServerConfiguration> GetServerAsync() =>
            Task.FromResult(new ServerConfiguration { Id = Guid.NewGuid() });

        public Task<ClientConfiguration?> GetClientAsync(Guid clientId) =>
            Task.FromResult<ClientConfiguration?>(null);

        public Task<SiteConfiguration?> GetSiteAsync(Guid siteId) =>
            Task.FromResult<SiteConfiguration?>(null);

        public Task<T?> GetEffectiveValueAsync<T>(string level, string key, Guid? targetId = null) =>
            throw new NotSupportedException();

        public Task<T?> GetConfigurationObjectAsync<T>(string objectType) where T : class =>
            throw new NotSupportedException();

        public Task<BrandingSettings> GetBrandingSettingsAsync() => throw new NotSupportedException();

        public Task<AIIntegrationSettings> GetAISettingsAsync() => throw new NotSupportedException();

        public Task<ResolvedConfiguration> ResolveForSiteAsync(Guid siteId) =>
            Task.FromResult(new ResolvedConfiguration());

        public Task<ResolvedConfiguration> ResolveForClientAsync(Guid clientId) =>
            Task.FromResult(new ResolvedConfiguration());

        public Task<BackgroundProcessingSettings> ResolveBackgroundProcessingAsync(
            Guid? clientId, CancellationToken ct = default) =>
            Task.FromResult(new BackgroundProcessingSettings());

        public Task ValidateInheritanceAsync() => Task.CompletedTask;

        public void ClearCache() { }
    }
}
