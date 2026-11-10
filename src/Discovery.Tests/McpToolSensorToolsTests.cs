using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Discovery.Migrations.Migrations;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

/// <summary>
/// Tools MCP de SERVIDOR para SENSORES do agente:
///   • get_agent_sensors        — quais sensores a máquina tem e como estão agora
///   • get_agent_sensor_history — série/timeline de UM sensor
///
/// Cobre: registro no catálogo, metadados editoriais, seed da M285 coerente com o
/// catálogo, exigência de contexto de agente, filtros, clamp de limit, o caminho
/// "sem sensores" (hasSensors=false, NUNCA erro), sensor inexistente/fora do
/// escopo do agente (resposta vazia, não erro) e falha sem exceção.
/// </summary>
public class McpToolSensorToolsTests
{
    private static readonly Guid ContextAgentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContextClientId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ContextSiteId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Guid DiskSensorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ServiceSensorId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OtherAgentSensorId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static McpToolExecutor Build(
        FakeSensorReadService? read = null,
        FakeSensorSeriesService? series = null,
        FakePolicyRepository? policies = null)
        => new(
            policies ?? new FakePolicyRepository(),
            new ThrowingKnowledgeMcpTool(),
            new ThrowingKnowledgeListMcpTool(),
            new ThrowingMessageRepository(),
            new FakeMediator(),
            read ?? new FakeSensorReadService(),
            series ?? new FakeSensorSeriesService(),
            NullLogger<McpToolExecutor>.Instance);

    private static Task<string> Execute(
        McpToolExecutor executor, string tool, string args,
        Guid? agentId = null, Guid? clientId = null, Guid? siteId = null)
        => executor.ExecuteAsync(
            tool, args, clientId, siteId, agentId, new AIIntegrationSettings());

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    // ── Catálogo / metadados / seed ─────────────────────────────────────────

    [Test]
    public void ServerCatalog_ContainsSensorTools()
    {
        var catalog = Build().GetServerCatalog();

        foreach (var name in M285_SeedSensorMcpTools.SeededToolNames)
        {
            var item = catalog.SingleOrDefault(t => t.Name == name);
            Assert.That(item.Name, Is.EqualTo(name), $"handler de '{name}' precisa estar registrado");
            Assert.That(item.Description, Is.Not.Null.And.Not.Empty);
        }
    }

    [Test]
    public void SensorToolDescriptions_ExplainReadOnlyServerScope_AndNoThresholdRecalc()
    {
        var catalog = Build().GetServerCatalog();

        var sensors = catalog.Single(t => t.Name == "get_agent_sensors");
        Assert.That(sensors.Description, Does.Contain("SERVIDOR"),
            "a descrição deve deixar claro que a leitura é do servidor");
        Assert.That(sensors.Description, Does.Contain("SOMENTE LEITURA"));
        Assert.That(sensors.Description, Does.Contain("hasSensors"),
            "a descrição deve orientar o modelo sobre o caso sem sensores");
        Assert.That(sensors.Description, Does.Contain("metrics_history"),
            "a descrição deve diferenciar de get_agent_metrics_history (alertas)");

        var history = catalog.Single(t => t.Name == "get_agent_sensor_history");
        Assert.That(history.Description, Does.Contain("SERVIDOR"));
        Assert.That(history.Description, Does.Contain("sensorId"),
            "a descrição deve dizer que sensorId é obrigatório");
        Assert.That(history.Description, Does.Contain("get_agent_sensors"),
            "a descrição deve ensinar a descobrir o sensorId");
    }

    [Test]
    public void CatalogMetadata_HasEditorialEntriesForSensorTools()
    {
        var sensors = McpToolCatalogMetadata.For("get_agent_sensors", McpToolSources.Server);
        Assert.That(sensors.Category, Is.EqualTo("Sensores"));
        Assert.That(sensors.RecommendedTimeoutSeconds, Is.EqualTo(30));
        Assert.That(sensors.TimeoutApplies, Is.True);
        Assert.That(sensors.WhenToUse, Is.Not.Null.And.Not.Empty);

        var history = McpToolCatalogMetadata.For("get_agent_sensor_history", McpToolSources.Server);
        Assert.That(history.Category, Is.EqualTo("Sensores"));
        Assert.That(history.RecommendedTimeoutSeconds, Is.EqualTo(60));
        Assert.That(history.TimeoutApplies, Is.True);
        Assert.That(history.WhenToUse, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void MigrationSeed_NamesMatchCatalog()
    {
        Assert.That(M285_SeedSensorMcpTools.SeededToolNames, Is.EquivalentTo(new[]
        {
            "get_agent_sensors",
            "get_agent_sensor_history"
        }));

        foreach (var name in M285_SeedSensorMcpTools.SeededToolNames)
        {
            var metadata = McpToolCatalogMetadata.For(name, McpToolSources.Server);
            Assert.That(metadata.Category, Is.EqualTo("Sensores"),
                $"'{name}' precisa de categoria editorial (seed x catálogo)");
            Assert.That(metadata.RecommendedTimeoutSeconds, Is.EqualTo(30).Or.EqualTo(60),
                $"'{name}' deve ter timeout coerente com o seed");
        }
    }

    /// <summary>
    /// Sem policy global o executor nega a tool mesmo com handler registrado —
    /// é exatamente o que a M285 semeia. Este teste amarra o nome semeado ao
    /// comportamento do executor.
    /// </summary>
    [Test]
    public async Task WithoutGlobalPolicy_ExecutorDeniesSensorTool()
    {
        var executor = Build(policies: new FakePolicyRepository(denyAll: true));

        var json = await Execute(executor, "get_agent_sensors", "{}", agentId: ContextAgentId);

        Assert.That(Root(json).GetProperty("error").GetString(), Does.Contain("não autorizada"));
    }

    // ── get_agent_sensors ───────────────────────────────────────────────────

    [Test]
    public async Task GetAgentSensors_RequiresAgentContext()
    {
        var json = await Execute(Build(), "get_agent_sensors", "{}");

        Assert.That(Root(json).GetProperty("error").GetString(), Does.Contain("agentId"));
    }

    [Test]
    public async Task GetAgentSensors_WithoutSensors_ReturnsHasSensorsFalse_NotError()
    {
        var read = new FakeSensorReadService { Payload = EmptyPayload() };

        var json = await Execute(Build(read), "get_agent_sensors", "{}", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.TryGetProperty("error", out _), Is.False, "ausência de sensores NÃO é erro");
        Assert.That(root.GetProperty("hasSensors").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("count").GetInt32(), Is.EqualTo(0));
        Assert.That(root.GetProperty("message").GetString(), Does.Contain("não tem sensores"));
    }

    [Test]
    public async Task GetAgentSensors_UnknownAgent_ReturnsEmpty_NotError()
    {
        var read = new FakeSensorReadService { Payload = null };

        var json = await Execute(Build(read), "get_agent_sensors", "{}", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.TryGetProperty("error", out _), Is.False);
        Assert.That(root.GetProperty("hasSensors").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("message").GetString(), Does.Contain("não encontrada"));
    }

    [Test]
    public async Task GetAgentSensors_ReturnsStateAndPassesThroughServerDecidedSeverity()
    {
        var json = await Execute(Build(), "get_agent_sensors", "{}", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.GetProperty("hasSensors").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("agentId").GetGuid(), Is.EqualTo(ContextAgentId));

        var sensors = root.GetProperty("sensors");
        // 3 itens: disco Failing, serviço Warning e um Pending (escondido no default).
        Assert.That(sensors.GetArrayLength(), Is.EqualTo(2),
            "sensores Pending/desatualizados são escondidos sem includeInactive");

        var disk = sensors.EnumerateArray().Single(s => s.GetProperty("sensorId").GetGuid() == DiskSensorId);
        Assert.That(disk.GetProperty("type").GetString(), Is.EqualTo("DiskSpace"));
        Assert.That(disk.GetProperty("currentStatus").GetString(), Is.EqualTo("Failing"));
        Assert.That(disk.GetProperty("severity").GetString(), Is.EqualTo("Critical"),
            "a severidade vem do serviço — a tool NÃO recalcula");
        Assert.That(disk.GetProperty("channels").GetProperty("free_percent").GetDouble(), Is.EqualTo(4.5));
    }

    [Test]
    public async Task GetAgentSensors_FiltersBySensorType()
    {
        var json = await Execute(
            Build(), "get_agent_sensors", """{"sensorType":"Service"}""", agentId: ContextAgentId);

        var sensors = Root(json).GetProperty("sensors");
        Assert.That(sensors.GetArrayLength(), Is.EqualTo(1));
        Assert.That(sensors[0].GetProperty("sensorId").GetGuid(), Is.EqualTo(ServiceSensorId));
    }

    [Test]
    public async Task GetAgentSensors_FiltersByStatus()
    {
        var json = await Execute(
            Build(), "get_agent_sensors", """{"status":"Failing"}""", agentId: ContextAgentId);

        var sensors = Root(json).GetProperty("sensors");
        Assert.That(sensors.GetArrayLength(), Is.EqualTo(1));
        Assert.That(sensors[0].GetProperty("currentStatus").GetString(), Is.EqualTo("Failing"));
    }

    [Test]
    public async Task GetAgentSensors_IncludeInactive_RevealsPendingSensors()
    {
        var json = await Execute(
            Build(), "get_agent_sensors", """{"includeInactive":true}""", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.GetProperty("sensors").GetArrayLength(), Is.EqualTo(3));
        Assert.That(root.GetProperty("count").GetInt32(), Is.EqualTo(3));
        Assert.That(root.GetProperty("totalForAgent").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public async Task GetAgentSensors_FilterMatchingNothing_ExplainsWithoutError()
    {
        var json = await Execute(
            Build(), "get_agent_sensors", """{"sensorType":"BitLocker"}""", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.TryGetProperty("error", out _), Is.False);
        Assert.That(root.GetProperty("count").GetInt32(), Is.EqualTo(0));
        Assert.That(root.GetProperty("message").GetString(), Does.Contain("filtros"));
    }

    [Test]
    public async Task GetAgentSensors_InvalidSensorType_ReturnsClearError()
    {
        var json = await Execute(
            Build(), "get_agent_sensors", """{"sensorType":"NaoExiste"}""", agentId: ContextAgentId);

        Assert.That(Root(json).GetProperty("error").GetString(), Does.Contain("sensorType"));
    }

    [Test]
    public async Task GetAgentSensors_InvalidStatus_ReturnsClearError()
    {
        var json = await Execute(
            Build(), "get_agent_sensors", """{"status":"NaoExiste"}""", agentId: ContextAgentId);

        Assert.That(Root(json).GetProperty("error").GetString(), Does.Contain("status"));
    }

    [Test]
    public async Task GetAgentSensors_ServiceFailure_ReturnsErrorJson_WithoutThrowing()
    {
        var read = new FakeSensorReadService { ThrowOnGet = new InvalidOperationException("boom") };

        var json = await Execute(Build(read), "get_agent_sensors", "{}", agentId: ContextAgentId);

        Assert.That(Root(json).GetProperty("error").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GetAgentSensors_IgnoresScopeIdsInjectedByLlm()
    {
        var read = new FakeSensorReadService();
        var other = Guid.Parse("99999999-9999-9999-9999-999999999999");

        await Execute(
            Build(read), "get_agent_sensors",
            $$"""{"agentId":"{{other}}","clientId":"{{other}}","siteId":"{{other}}"}""",
            agentId: ContextAgentId, clientId: ContextClientId, siteId: ContextSiteId);

        Assert.That(read.LastAgentId, Is.EqualTo(ContextAgentId),
            "o escopo do agente vem SEMPRE do contexto, nunca dos argumentos do LLM");
    }

    // ── get_agent_sensor_history ────────────────────────────────────────────

    [Test]
    public async Task GetAgentSensorHistory_RequiresAgentContext()
    {
        var json = await Execute(Build(), "get_agent_sensor_history", $$"""{"sensorId":"{{DiskSensorId}}"}""");

        Assert.That(Root(json).GetProperty("error").GetString(), Does.Contain("agentId"));
    }

    [Test]
    public async Task GetAgentSensorHistory_RequiresSensorId()
    {
        var json = await Execute(Build(), "get_agent_sensor_history", "{}", agentId: ContextAgentId);

        Assert.That(Root(json).GetProperty("error").GetString(), Does.Contain("sensorId"));
    }

    [Test]
    public async Task GetAgentSensorHistory_InvalidSensorId_ReturnsClearError()
    {
        var json = await Execute(
            Build(), "get_agent_sensor_history", """{"sensorId":"nao-e-guid"}""", agentId: ContextAgentId);

        Assert.That(Root(json).GetProperty("error").GetString(), Does.Contain("sensorId"));
    }

    [Test]
    public async Task GetAgentSensorHistory_DefaultsToLast24Hours_AndLimit50()
    {
        var series = new FakeSensorSeriesService();

        await Execute(
            Build(series: series), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}"}""", agentId: ContextAgentId);

        Assert.That(series.LastAgentId, Is.EqualTo(ContextAgentId));
        Assert.That(series.LastSensorId, Is.EqualTo(DiskSensorId));
        Assert.That(series.LastLimit, Is.EqualTo(50), "limit padrão = 50");
        var span = series.LastTo!.Value - series.LastFrom!.Value;
        Assert.That(span.TotalHours, Is.EqualTo(24).Within(0.1), "default = últimas 24h");
    }

    [Test]
    public async Task GetAgentSensorHistory_ClampsLimitTo200()
    {
        var series = new FakeSensorSeriesService();

        await Execute(
            Build(series: series), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}","limit":5000}""", agentId: ContextAgentId);

        Assert.That(series.LastLimit, Is.EqualTo(200), "teto de 200 pontos");
    }

    [Test]
    public async Task GetAgentSensorHistory_ClampsLimitToMinimum1()
    {
        var series = new FakeSensorSeriesService();

        await Execute(
            Build(series: series), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}","limit":0}""", agentId: ContextAgentId);

        Assert.That(series.LastLimit, Is.EqualTo(1));
    }

    [Test]
    public async Task GetAgentSensorHistory_SensorNotApplicable_ReturnsEmptyPayload_NotError()
    {
        // Sensor de OUTRA máquina: o serviço devolve null (guard de escopo) e a
        // tool responde vazio com mensagem — nunca erro e nunca dados alheios.
        var series = new FakeSensorSeriesService { NotApplicable = true, Events = EmptyEvents() };

        var json = await Execute(
            Build(series: series), "get_agent_sensor_history",
            $$"""{"sensorId":"{{OtherAgentSensorId}}"}""", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.TryGetProperty("error", out _), Is.False, "sensor fora do escopo NÃO é erro");
        Assert.That(root.GetProperty("hasData").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("points").GetArrayLength(), Is.EqualTo(0));
        Assert.That(root.GetProperty("events").GetArrayLength(), Is.EqualTo(0));
        Assert.That(root.GetProperty("message").GetString(), Does.Contain("não existe")
            .Or.Contain("não se aplica"));
        Assert.That(series.EventsCallCount, Is.Zero,
            "sensor fora do escopo não deve sequer consultar a tabela de eventos");
    }

    [Test]
    public async Task GetAgentSensorHistory_ReturnsPointsAndEvents_WithServerDecidedStatus()
    {
        var json = await Execute(
            Build(), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}","channel":"free_percent"}""", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.GetProperty("hasData").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("sensorId").GetGuid(), Is.EqualTo(DiskSensorId));
        Assert.That(root.GetProperty("resolution").GetString(), Is.EqualTo("raw"));

        var points = root.GetProperty("points");
        Assert.That(points.GetArrayLength(), Is.GreaterThan(0));
        var first = points[0];
        Assert.That(first.TryGetProperty("at", out _), Is.True);
        Assert.That(first.TryGetProperty("value", out _), Is.True, "canal único devolve 'value'");
        Assert.That(first.GetProperty("channels").GetProperty("free_percent").GetDouble(), Is.GreaterThan(0));

        var events = root.GetProperty("events");
        Assert.That(events.GetArrayLength(), Is.EqualTo(1));
        Assert.That(events[0].GetProperty("kind").GetString(), Is.EqualTo("StatusChanged"));
        Assert.That(events[0].GetProperty("status").GetString(), Is.EqualTo("Failing"));
        Assert.That(events[0].GetProperty("previousStatus").GetString(), Is.EqualTo("Warning"));

        // Limiares apenas informativos: a tool não julga status.
        Assert.That(root.GetProperty("thresholds").GetProperty("warning").GetDouble(), Is.EqualTo(15));
    }

    [Test]
    public async Task GetAgentSensorHistory_AppliedLimit_CapsReturnedPointsAndFlagsTruncation()
    {
        var series = new FakeSensorSeriesService { PointCount = 500 };

        var json = await Execute(
            Build(series: series), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}","limit":10}""", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.GetProperty("points").GetArrayLength(), Is.EqualTo(10));
        Assert.That(root.GetProperty("count").GetInt32(), Is.EqualTo(10));
        Assert.That(root.GetProperty("limit").GetInt32(), Is.EqualTo(10));
        Assert.That(root.GetProperty("truncated").GetBoolean(), Is.True,
            "série maior que o limit precisa sinalizar truncamento");
    }

    [Test]
    public async Task GetAgentSensorHistory_NotTruncated_WhenSeriesFitsLimit()
    {
        var series = new FakeSensorSeriesService { PointCount = 5 };

        var json = await Execute(
            Build(series: series), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}","limit":50}""", agentId: ContextAgentId);

        var root = Root(json);
        Assert.That(root.GetProperty("points").GetArrayLength(), Is.EqualTo(5));
        Assert.That(root.GetProperty("truncated").GetBoolean(), Is.False);
    }

    [Test]
    public async Task GetAgentSensorHistory_FromAfterTo_ReturnsClearError()
    {
        var json = await Execute(
            Build(), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}","from":"2026-11-02T00:00:00Z","to":"2026-11-01T00:00:00Z"}""",
            agentId: ContextAgentId);

        Assert.That(Root(json).GetProperty("error").GetString(), Does.Contain("from"));
    }

    [Test]
    public async Task GetAgentSensorHistory_PassesChannelAndTargetKeyThrough()
    {
        var series = new FakeSensorSeriesService();

        await Execute(
            Build(series: series), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}","channel":"used_percent","targetKey":"C:","from":"2026-11-01T00:00:00Z","to":"2026-11-02T00:00:00Z"}""",
            agentId: ContextAgentId);

        Assert.That(series.LastChannels, Is.EquivalentTo(new[] { "used_percent" }));
        Assert.That(series.LastTargetKey, Is.EqualTo("C:"));
        Assert.That(series.LastFrom!.Value, Is.EqualTo(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(series.LastTo!.Value, Is.EqualTo(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public async Task GetAgentSensorHistory_ServiceFailure_ReturnsErrorJson_WithoutThrowing()
    {
        var series = new FakeSensorSeriesService { ThrowOnGetSeries = new InvalidOperationException("boom") };

        var json = await Execute(
            Build(series: series), "get_agent_sensor_history",
            $$"""{"sensorId":"{{DiskSensorId}}"}""", agentId: ContextAgentId);

        Assert.That(Root(json).GetProperty("error").GetString(), Is.Not.Null.And.Not.Empty);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static AgentSensorsPayloadDto EmptyPayload() => new()
    {
        AgentId = ContextAgentId,
        GeneratedAt = DateTime.UtcNow,
        Counts = new SensorCountsDto(),
        Items = Array.Empty<SensorItemDto>()
    };

    private static SensorEventPageDto EmptyEvents() => new() { Items = Array.Empty<SensorEventDto>() };

    private static AgentSensorsPayloadDto PayloadWithThreeSensors() => new()
    {
        AgentId = ContextAgentId,
        GeneratedAt = DateTime.UtcNow,
        UpToDate = true,
        Counts = new SensorCountsDto { Total = 3, Failing = 1, Warning = 1, Pending = 1, Stale = 1 },
        Items = new[]
        {
            new SensorItemDto
            {
                SensorId = DiskSensorId,
                Key = "disk.c",
                Name = "Espaço em disco C:",
                SensorType = SensorType.DiskSpace,
                Target = "C:",
                TargetKey = "C:",
                Status = SensorStatus.Failing,
                Severity = SensorSeverity.Critical,
                Message = "Pouco espaço livre",
                IsStale = false,
                LastRunAt = DateTime.UtcNow.AddMinutes(-2),
                LastStatusChangedAt = DateTime.UtcNow.AddHours(-3),
                CurrentChannels = new Dictionary<string, double> { ["free_percent"] = 4.5 },
                PrimaryChannel = "free_percent",
                PrimaryValue = 4.5
            },
            new SensorItemDto
            {
                SensorId = ServiceSensorId,
                Key = "svc.spooler",
                Name = "Serviço Spooler",
                SensorType = SensorType.Service,
                Target = "Spooler",
                TargetKey = "Spooler",
                Status = SensorStatus.Warning,
                Severity = SensorSeverity.Warning,
                IsStale = false,
                LastRunAt = DateTime.UtcNow.AddMinutes(-5),
                LastStatusChangedAt = DateTime.UtcNow.AddHours(-1),
                CurrentChannels = new Dictionary<string, double> { ["running"] = 0 }
            },
            new SensorItemDto
            {
                SensorId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                Key = "new.sensor",
                Name = "Sensor recém-criado",
                SensorType = SensorType.Cpu,
                Target = null,
                TargetKey = string.Empty,
                // Ainda sem leitura: escondido por padrão (includeInactive=false).
                Status = SensorStatus.Pending,
                IsStale = true,
                LastRunAt = null
            }
        }
    };

    private static SensorSeriesDto SeriesWith(int pointCount) => new()
    {
        AgentId = ContextAgentId,
        SensorId = DiskSensorId,
        TargetKey = "C:",
        From = DateTime.UtcNow.AddHours(-24),
        To = DateTime.UtcNow,
        Resolution = "raw",
        StepSeconds = 300,
        WarningThreshold = 15,
        CriticalThreshold = 5,
        Comparison = SensorComparison.LowerIsWorse,
        ThresholdUnit = SensorChannelUnit.Percent,
        Channels = new[]
        {
            new SensorSeriesChannelDto
            {
                Key = "free_percent",
                Label = "Livre (%)",
                Unit = SensorChannelUnit.Percent,
                Points = Enumerable.Range(0, pointCount).Select(i => new SensorSeriesPointDto
                {
                    T = DateTime.UtcNow.AddMinutes(-pointCount + i),
                    V = 10 + i
                }).ToList(),
                Stats = new SensorSeriesStatsDto { Min = 10, Avg = 12, Max = 20, P95 = 19, Last = 15, Count = pointCount }
            }
        }
    };

    internal sealed class FakeSensorReadService : ISensorReadService
    {
        public AgentSensorsPayloadDto? Payload { get; init; } = PayloadWithThreeSensors();
        public Exception? ThrowOnGet { get; init; }
        public Guid? LastAgentId { get; private set; }

        public Task<AgentSensorsPayloadDto?> GetForAgentAsync(Guid agentId, CancellationToken ct = default)
        {
            if (ThrowOnGet is not null) throw ThrowOnGet;
            LastAgentId = agentId;
            return Task.FromResult(Payload);
        }

        public Task<SensorFleetSummaryDto> GetFleetSummaryAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
            => Task.FromResult(new SensorFleetSummaryDto());
    }

    internal sealed class FakeSensorSeriesService : ISensorSeriesService
    {
        /// <summary>true simula sensor que NÃO se aplica ao agente (fora do escopo → serviço devolve null).</summary>
        public bool NotApplicable { get; init; }
        public int PointCount { get; init; } = 3;
        public SensorEventPageDto Events { get; init; } = new()
        {
            Items = new[]
            {
                new SensorEventDto
                {
                    Id = Guid.NewGuid(),
                    TargetKey = "C:",
                    Kind = SensorEventKind.StatusChanged,
                    Status = SensorStatus.Failing,
                    PreviousStatus = SensorStatus.Warning,
                    Severity = SensorSeverity.Critical,
                    PreviousSeverity = SensorSeverity.Warning,
                    Message = "Espaço livre abaixo do limiar crítico",
                    OccurredAt = DateTime.UtcNow.AddHours(-3)
                }
            }
        };
        public Exception? ThrowOnGetSeries { get; init; }

        /// <summary>Quantas vezes GetEventsAsync foi chamado (para provar o curto-circuito).</summary>
        public int EventsCallCount { get; private set; }

        public Guid? LastAgentId { get; private set; }
        public Guid? LastSensorId { get; private set; }
        public string? LastTargetKey { get; private set; }
        public DateTime? LastFrom { get; private set; }
        public DateTime? LastTo { get; private set; }
        public IReadOnlyList<string>? LastChannels { get; private set; }
        public int LastLimit { get; private set; }

        public Task<SensorSeriesDto?> GetSeriesAsync(
            Guid agentId, Guid sensorId, string? targetKey,
            DateTime from, DateTime to, IReadOnlyList<string>? channels, CancellationToken ct = default)
        {
            if (ThrowOnGetSeries is not null) throw ThrowOnGetSeries;
            LastAgentId = agentId;
            LastSensorId = sensorId;
            LastTargetKey = targetKey;
            LastFrom = from;
            LastTo = to;
            LastChannels = channels;
            return Task.FromResult(NotApplicable ? null : SeriesWith(PointCount));
        }

        public Task<SensorEventPageDto> GetEventsAsync(
            Guid agentId, Guid sensorId, string? targetKey, string? cursor, int limit, CancellationToken ct = default)
        {
            if (ThrowOnGetSeries is not null) throw ThrowOnGetSeries;
            EventsCallCount++;
            LastLimit = limit;
            return Task.FromResult(Events);
        }
    }

    private sealed class FakePolicyRepository(bool denyAll = false) : IMcpToolPolicyRepository
    {
        private McpToolPolicy? Policy(string tool) => denyAll
            ? null
            : new McpToolPolicy
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
            => Task.FromResult<IReadOnlyList<McpToolPolicy>>(denyAll
                ? Array.Empty<McpToolPolicy>()
                : new[] { Policy("get_agent_sensors")!, Policy("get_agent_sensor_history")! });

        public Task<McpToolPolicy?> GetPolicyAsync(
            string toolName, Guid? clientId, Guid? siteId, Guid? agentId, CancellationToken ct = default)
            => Task.FromResult(Policy(toolName));

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

    private sealed class FakeMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
            => throw new NotSupportedException(request.GetType().Name);

        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest
            => Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken ct = default)
            => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken ct = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
            where TNotification : INotification => Task.CompletedTask;
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
