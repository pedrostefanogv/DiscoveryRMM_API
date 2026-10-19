using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.Cqrs.Alerts.Queries;
using Discovery.Core.Cqrs.MonitoringEvents.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Discovery.Migrations.Migrations;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Tools MCP de SERVIDOR que trazem contexto operacional do agente
/// (get_agent_alerts, get_agent_labels, get_agent_metrics_history).
///
/// Cobre: registro no catálogo, metadados editoriais, seed da M236 coerente
/// com o catálogo, uso EXCLUSIVO do escopo do contexto (nunca IDs do LLM),
/// limites de payload, redaction de e-mail e falha sem exceção.
/// </summary>
public class McpToolServerContextTests
{
    private static readonly Guid ContextAgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContextClientId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ContextSiteId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static McpToolExecutor BuildExecutor(FakeMediator mediator, FakePolicyRepository? policies = null)
        => new(
            policies ?? new FakePolicyRepository(),
            new ThrowingKnowledgeMcpTool(),
            new ThrowingKnowledgeListMcpTool(),
            new ThrowingMessageRepository(),
            mediator,
            NullLogger<McpToolExecutor>.Instance);

    private static Task<string> Execute(
        McpToolExecutor executor,
        string tool,
        string args,
        Guid? agentId = null,
        Guid? clientId = null,
        Guid? siteId = null,
        AIIntegrationSettings? settings = null)
        => executor.ExecuteAsync(
            tool, args, clientId, siteId, agentId,
            settings ?? new AIIntegrationSettings());

    // ── Catálogo / metadados / seed ─────────────────────────────────────────

    [Test]
    public void ServerCatalog_ContainsAgentContextTools()
    {
        var catalog = BuildExecutor(new FakeMediator()).GetServerCatalog();

        foreach (var name in M236_SeedAgentContextMcpTools.SeededToolNames)
        {
            var item = catalog.SingleOrDefault(t => t.Name == name);
            Assert.That(item.Name, Is.EqualTo(name), $"handler de '{name}' precisa estar registrado");
            Assert.That(item.Description, Does.Contain("SERVIDOR").IgnoreCase,
                $"descrição de '{name}' deve deixar claro que é leitura do servidor");
        }
    }

    [Test]
    public void CatalogMetadata_HasEditorialEntriesForServerTools()
    {
        var alerts = McpToolCatalogMetadata.For("get_agent_alerts", McpToolSources.Server);
        Assert.That(alerts.Category, Is.EqualTo("Alertas"));
        Assert.That(alerts.RecommendedTimeoutSeconds, Is.EqualTo(30));
        Assert.That(alerts.TimeoutApplies, Is.True);
        Assert.That(alerts.WhenToUse, Is.Not.Null.And.Not.Empty);

        var labels = McpToolCatalogMetadata.For("get_agent_labels", McpToolSources.Server);
        Assert.That(labels.Category, Is.EqualTo("Estado da máquina"));
        Assert.That(labels.RecommendedTimeoutSeconds, Is.EqualTo(20));
        Assert.That(labels.TimeoutApplies, Is.True);

        var metrics = McpToolCatalogMetadata.For("get_agent_metrics_history", McpToolSources.Server);
        Assert.That(metrics.Category, Is.EqualTo("Diagnóstico/histórico"));
        Assert.That(metrics.RecommendedTimeoutSeconds, Is.EqualTo(60));
        Assert.That(metrics.TimeoutApplies, Is.True);
    }

    [Test]
    public void CatalogMetadata_HasAttachmentToolEntries()
    {
        foreach (var name in new[] { "attach_images_to_ticket", "attach_images_to_ticket_comment" })
        {
            var metadata = McpToolCatalogMetadata.For(name, McpToolSources.Agent);
            Assert.That(metadata.Category, Is.EqualTo("Chamados"), name);
            Assert.That(metadata.RecommendedTimeoutSeconds, Is.EqualTo(90), name);
            Assert.That(metadata.TimeoutApplies, Is.True, name);
            Assert.That(metadata.WhenToUse, Does.Contain("pedir"), $"'{name}' deve orientar a só anexar após pedido");
            Assert.That(metadata.WhenToUse, Does.Contain("SERVIDOR"), $"'{name}' deve indicar envio ao servidor");
        }

        var list = McpToolCatalogMetadata.For("list_ticket_attachments", McpToolSources.Agent);
        Assert.That(list.Category, Is.EqualTo("Chamados"));
        Assert.That(list.RecommendedTimeoutSeconds, Is.EqualTo(30));
        Assert.That(list.TimeoutApplies, Is.True);
    }

    [Test]
    public void MigrationSeed_NamesAreCoherentWithCatalogMetadata()
    {
        Assert.That(M236_SeedAgentContextMcpTools.SeededToolNames, Is.EquivalentTo(new[]
        {
            "get_agent_alerts",
            "get_agent_labels",
            "get_agent_metrics_history"
        }));

        foreach (var name in M236_SeedAgentContextMcpTools.SeededToolNames)
        {
            var metadata = McpToolCatalogMetadata.For(name, McpToolSources.Server);
            Assert.That(metadata.Category, Is.Not.EqualTo("Outros (servidor)"),
                $"'{name}' precisa de categoria editorial (seed x catálogo)");
            Assert.That(metadata.RecommendedTimeoutSeconds, Is.GreaterThan(0), name);
        }
    }

    // ── get_agent_alerts ────────────────────────────────────────────────────

    [Test]
    public async Task GetAgentAlerts_UsesOnlyContextScope_AndSlimPayload()
    {
        var mediator = new FakeMediator();
        var executor = BuildExecutor(mediator);
        var otherAgent = Guid.Parse("99999999-9999-9999-9999-999999999999");

        var json = await Execute(
            executor,
            "get_agent_alerts",
            // O LLM tenta injetar outro escopo e um limit absurdo: ambos devem
            // ser ignorados/limitados.
            $$"""{"agentId":"{{otherAgent}}","clientId":"{{otherAgent}}","siteId":"{{otherAgent}}","limit":9999}""",
            agentId: ContextAgentId,
            clientId: ContextClientId,
            siteId: ContextSiteId);

        Assert.That(mediator.LastRequest, Is.InstanceOf<ListAgentAlertsQuery>());
        var query = (ListAgentAlertsQuery)mediator.LastRequest!;
        Assert.That(query.ScopeAgentId, Is.EqualTo(ContextAgentId), "escopo do agente deve vir do contexto");
        Assert.That(query.ScopeClientId, Is.EqualTo(ContextClientId));
        Assert.That(query.ScopeSiteId, Is.EqualTo(ContextSiteId));
        Assert.That(query.ScopeType, Is.EqualTo("Agent"));
        Assert.That(query.IncludeParentScopes, Is.True,
            "a tool deve incluir alertas de site/cliente que valem para a máquina");
        Assert.That(query.Limit, Is.EqualTo(20), "limit deve ser limitado ao teto da tool (20)");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var alerts = root.GetProperty("alerts");
        Assert.That(alerts.GetArrayLength(), Is.EqualTo(1));
        var alert = alerts[0];
        Assert.That(alert.GetProperty("id").GetGuid(), Is.Not.EqualTo(Guid.Empty));
        Assert.That(alert.GetProperty("status").GetString(), Is.EqualTo("Dispatched"));
        Assert.That(alert.GetProperty("severity").GetString(), Is.EqualTo("Warning"));
        Assert.That(alert.GetProperty("title").GetString(), Does.Contain("[email]"),
            "e-mail no título deve ser mascarado");
        Assert.That(alert.GetProperty("title").GetString(), Does.Not.Contain("fulano@example.com"));
        Assert.That(alert.GetProperty("ticketId").GetGuid(), Is.EqualTo(FakeMediator.TicketId));
        Assert.That(alert.TryGetProperty("resolvedAt", out _), Is.True, "contrato mantém resolvedAt (nulo)");
    }

    [Test]
    public async Task GetAgentAlerts_WithoutAgentContext_ReturnsErrorJson()
    {
        var executor = BuildExecutor(new FakeMediator());

        var json = await Execute(executor, "get_agent_alerts", "{}", agentId: null);

        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("error").GetString(), Does.Contain("agentId"));
    }

    [Test]
    public async Task GetAgentAlerts_InvalidStatus_ReturnsClearError()
    {
        var executor = BuildExecutor(new FakeMediator());

        var json = await Execute(
            executor, "get_agent_alerts", """{"status":"NaoExiste"}""", agentId: ContextAgentId);

        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("error").GetString(), Does.Contain("Status inválido"));
    }

    // ── get_agent_labels ────────────────────────────────────────────────────

    [Test]
    public async Task GetAgentLabels_RequiresAgentContext()
    {
        var executor = BuildExecutor(new FakeMediator());

        var json = await Execute(executor, "get_agent_labels", "{}");

        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("error").GetString(), Does.Contain("agentId"));
    }

    [Test]
    public async Task GetAgentLabels_ReturnsLabelSourceAndRuleName()
    {
        var mediator = new FakeMediator();
        var executor = BuildExecutor(mediator);

        var json = await Execute(executor, "get_agent_labels", "{}", agentId: ContextAgentId);

        Assert.That(mediator.LabelsQuery, Is.Not.Null);
        Assert.That(mediator.LabelsQuery!.AgentId, Is.EqualTo(ContextAgentId));

        using var doc = JsonDocument.Parse(json);
        var labels = doc.RootElement.GetProperty("labels");
        Assert.That(labels.GetArrayLength(), Is.EqualTo(2));

        var prod = labels[0];
        Assert.That(prod.GetProperty("label").GetString(), Is.EqualTo("PROD"));
        Assert.That(prod.GetProperty("source").GetString(), Is.EqualTo("Automatic"));
        Assert.That(prod.GetProperty("ruleName").GetString(), Is.EqualTo("Regra PROD"),
            "nome da regra deve ser cruzado pelo label");
    }

    // ── get_agent_metrics_history ───────────────────────────────────────────

    [Test]
    public async Task GetAgentMetricsHistory_RequiresAgentContext()
    {
        var executor = BuildExecutor(new FakeMediator());

        var json = await Execute(executor, "get_agent_metrics_history", "{}");

        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("error").GetString(), Does.Contain("agentId"));
    }

    [Test]
    public async Task GetAgentMetricsHistory_ClampsLimitTo200_AndSummarizes()
    {
        var mediator = new FakeMediator();
        var executor = BuildExecutor(mediator);

        var json = await Execute(
            executor, "get_agent_metrics_history", """{"limit":5000}""",
            agentId: ContextAgentId, clientId: ContextClientId, siteId: ContextSiteId);

        var query = (ListMonitoringEventsQuery)mediator.LastRequest!;
        Assert.That(query.AgentId, Is.EqualTo(ContextAgentId));
        Assert.That(query.ClientId, Is.EqualTo(ContextClientId));
        Assert.That(query.SiteId, Is.EqualTo(ContextSiteId));
        Assert.That(query.Limit, Is.EqualTo(200), "payload máximo de 200 eventos");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.That(root.GetProperty("count").GetInt32(), Is.EqualTo(3));
        Assert.That(root.GetProperty("limit").GetInt32(), Is.EqualTo(200));
        Assert.That(root.GetProperty("summary").GetProperty("bySeverity").GetProperty("Critical").GetInt32(),
            Is.EqualTo(1));
    }

    // ── resiliência ─────────────────────────────────────────────────────────

    [Test]
    public async Task HandlerFailure_ReturnsErrorJson_WithoutThrowing()
    {
        var mediator = new FakeMediator { ThrowOnSend = new InvalidOperationException("boom") };
        var executor = BuildExecutor(mediator);

        var json = await Execute(executor, "get_agent_alerts", "{}", agentId: ContextAgentId);

        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("error").GetString(), Is.Not.Null.And.Not.Empty);
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeMediator : IMediator
    {
        public static readonly Guid TicketId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        public object? LastRequest { get; private set; }
        public ListAgentLabelsQuery? LabelsQuery { get; private set; }
        public Exception? ThrowOnSend { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend is not null) throw ThrowOnSend;
            LastRequest = request;
            if (request is ListAgentLabelsQuery labelsQuery)
                LabelsQuery = labelsQuery;

            object response = request switch
            {
                ListAgentAlertsQuery alerts => Result<ListAlertsResult>.Success(new ListAlertsResult(
                    new List<AlertDto>
                    {
                        new(Guid.NewGuid(), ContextAgentId, "Alerta de fulano@example.com", "Warning",
                            "Dispatched", DateTime.UtcNow, TicketId)
                    },
                    NextCursor: null, HasMore: false, Total: 1)),
                ListAgentLabelsQuery => Result<IReadOnlyList<AgentLabelDto>>.Success(new List<AgentLabelDto>
                {
                    new(Guid.NewGuid(), ContextAgentId, "PROD", "Automatic", DateTime.UtcNow.AddDays(-1)),
                    new(Guid.NewGuid(), ContextAgentId, "MANUAL", "Manual", DateTime.UtcNow)
                }),
                ListLabelRulesQuery => Result<IReadOnlyList<LabelRuleDto>>.Success(new List<LabelRuleDto>
                {
                    new(Guid.NewGuid(), "Regra PROD", "PROD", null, true, "Apply", "Exact",
                        new AgentLabelRuleExpressionNodeDto(), null, DateTime.UtcNow, DateTime.UtcNow)
                }),
                ListMonitoringEventsQuery => Result<IReadOnlyList<MonitoringEventDto>>.Success(
                    new List<MonitoringEventDto>
                    {
                        new(Guid.NewGuid(), ContextAgentId, "cpu.high", "Critical", "Agent", DateTime.UtcNow, null),
                        new(Guid.NewGuid(), ContextAgentId, "memory.high", "Warning", "Agent", DateTime.UtcNow, null),
                        new(Guid.NewGuid(), ContextAgentId, "disk.high", "Warning", "Server", DateTime.UtcNow, null)
                    }),
                _ => throw new NotSupportedException(request.GetType().Name)
            };

            return Task.FromResult((TResponse)response);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class FakePolicyRepository : IMcpToolPolicyRepository
    {
        private static McpToolPolicy Policy(string tool) => new()
        {
            Id = Guid.NewGuid(),
            ToolName = tool,
            IsEnabled = true,
            Source = McpToolSources.Server,
            MaxCallsPerMinute = 1000,
            TimeoutSeconds = 60
        };

        public Task<IReadOnlyList<McpToolPolicy>> GetEffectivePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolPolicy>>(new[]
            {
                Policy("get_agent_alerts"),
                Policy("get_agent_labels"),
                Policy("get_agent_metrics_history")
            });

        public Task<McpToolPolicy?> GetPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<McpToolPolicy?>(Policy(toolName));

        public Task<IReadOnlyList<McpToolPolicy>> GetScopePoliciesAsync(
            Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<McpToolPolicy>>(Array.Empty<McpToolPolicy>());

        public Task<McpToolPolicy> UpsertAsync(McpToolPolicy policy, CancellationToken ct = default)
            => Task.FromResult(policy);

        public Task<bool> DeleteScopePolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task EnsureGlobalPoliciesAsync(
            string source, IEnumerable<(string Name, string? Description)> tools, CancellationToken ct = default)
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

    private sealed class ThrowingKnowledgeMcpTool : IKnowledgeMcpTool
    {
        public Task<string> ExecuteAsync(
            Guid? clientId, Guid? siteId, string query, int maxResults = 3,
            Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> ExecuteWithSettingsAsync(
            Guid? clientId, Guid? siteId, string query, AIIntegrationSettings aiSettings,
            IReadOnlyCollection<Guid>? excludeArticleIds = null, int maxResults = 3,
            Guid? departmentId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingKnowledgeListMcpTool : IKnowledgeListMcpTool
    {
        public Task<string> ExecuteAsync(
            Guid? clientId, Guid? siteId, string? category, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingMessageRepository : IAiChatMessageRepository
    {
        public Task<AiChatMessage> CreateAsync(AiChatMessage message, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task CreateBatchAsync(IReadOnlyList<AiChatMessage> messages, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<AiChatMessage>> GetRecentBySessionAsync(
            Guid sessionId, int limit, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<List<AiChatMessage>> SearchByAgentAsync(
            Guid agentId, string query, int limit, Guid? excludeSessionId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<(int MessageCount, int EstimatedTokens)> GetStatsAsync(
            Guid sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
