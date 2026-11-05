using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// BUG 3: a execucao do relatorio nao validava o layout.
///
/// O create/update e o preview validavam, mas ProcessExecutionAsync ia direto ao
/// QueryAsync. Um template gravado antes das correcoes (ou editado direto no
/// banco) executava com layout invalido e so falhava por acidente dentro do join,
/// com mensagem ruim — ou pior, gerava um relatorio vazio "com sucesso".
///
/// Aqui garantimos que a validacao roda ANTES do QueryAsync, que a execucao fica
/// com status Failed + mensagem, e que um layout valido continua funcionando.
/// </summary>
[TestFixture]
public class ReportExecutionValidationTests
{
    private static readonly Guid ExecutionId = Guid.NewGuid();
    private static readonly Guid TemplateId = Guid.NewGuid();

    private sealed class FakeExecutionRepository : IReportExecutionRepository
    {
        public ReportExecution Execution { get; }
        public string? FailedMessage { get; private set; }
        public ReportExecutionStatus? FailedStatus { get; private set; }

        public FakeExecutionRepository(ReportExecution execution) => Execution = execution;

        public Task<ReportExecution> CreateAsync(ReportExecution execution) => Task.FromResult(execution);

        public Task<ReportExecution?> GetByIdAsync(Guid id, Guid? clientId = null)
            => Task.FromResult<ReportExecution?>(Execution);

        public Task<IReadOnlyList<ReportExecution>> GetRecentByClientAsync(Guid? clientId = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<ReportExecution>>([]);

        public Task<IReadOnlyList<ReportExecution>> GetRecentByClientIdsAsync(IReadOnlyCollection<Guid> clientIds, int limit = 50)
            => Task.FromResult<IReadOnlyList<ReportExecution>>([]);

        public Task<IReadOnlyList<ReportExecution>> GetPendingAsync(int limit = 20)
            => Task.FromResult<IReadOnlyList<ReportExecution>>([]);

        public Task<IReadOnlyList<ReportExecution>> GetExpiredAsync(DateTime cutoff, int limit = 1000)
            => Task.FromResult<IReadOnlyList<ReportExecution>>([]);

        public Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids) => Task.FromResult(0);

        public Task UpdateStatusAsync(Guid id, Guid? clientId, ReportExecutionStatus status, string? errorMessage = null)
        {
            FailedStatus = status;
            FailedMessage = errorMessage;
            Execution.Status = status;
            return Task.CompletedTask;
        }

        public Task<bool> TryClaimPendingAsync(Guid id, Guid? clientId = null) => Task.FromResult(true);

        public Task<int> RequeueStaleRunningAsync(DateTime startedBefore, int limit = 100) => Task.FromResult(0);

        public Task UpdateResultAsync(
            Guid id, Guid? clientId, string storageObjectKey, string storageBucket,
            string storageContentType, long storageSizeBytes, string? storageChecksum,
            int storageProviderType, int rowCount, int executionTimeMs)
            => Task.CompletedTask;
    }

    private sealed class FakeTemplateRepository(ReportTemplate template) : IReportTemplateRepository
    {
        public Task<ReportTemplate> CreateAsync(ReportTemplate t) => Task.FromResult(t);
        public Task<ReportTemplate?> GetByIdAsync(Guid id, Guid? clientId = null)
            => Task.FromResult<ReportTemplate?>(template);
        public Task<IReadOnlyList<ReportTemplate>> GetAllAsync(Guid? clientId = null, ReportDatasetType? datasetType = null, bool? isActive = true)
            => Task.FromResult<IReadOnlyList<ReportTemplate>>([template]);
        public Task<IReadOnlyList<ReportTemplateHistory>> GetHistoryAsync(Guid templateId, int limit = 50)
            => Task.FromResult<IReadOnlyList<ReportTemplateHistory>>([]);
        public Task UpdateAsync(ReportTemplate t) => Task.CompletedTask;
        public Task<bool> DeleteAsync(Guid id, Guid? clientId = null) => Task.FromResult(true);
        public Task<int> DeleteHistoryOlderThanAsync(DateTime cutoff) => Task.FromResult(0);
        public Task<IReadOnlyList<ReportTemplate>> GetBuiltInAsync(ReportDatasetType? datasetType = null)
            => Task.FromResult<IReadOnlyList<ReportTemplate>>([]);
    }

    /// <summary>Conta chamadas para provar que o QueryAsync NAO foi executado.</summary>
    private sealed class CountingQueryService : IReportDatasetQueryService
    {
        public int Calls { get; private set; }

        public Task<ReportQueryResult> QueryAsync(ReportTemplate template, string? filtersJson, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReportQueryResult
            {
                Columns = ["agentHostname"],
                Rows = [new Dictionary<string, object?> { ["agentHostname"] = "PC-01" }]
            });
        }
    }

    private sealed class FakeServerConfigurationRepository : IServerConfigurationRepository
    {
        public Task<ServerConfiguration?> GetAsync() => Task.FromResult<ServerConfiguration?>(null);
        public Task<ServerConfiguration> GetOrCreateDefaultAsync() => Task.FromResult(new ServerConfiguration());
        public Task UpdateAsync(ServerConfiguration config) => Task.CompletedTask;
        public Task<bool> ExistsAsync() => Task.FromResult(false);
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public List<NotificationPublishRequest> Published { get; } = [];

        public Task<AppNotification> PublishAsync(NotificationPublishRequest request, CancellationToken cancellationToken = default)
        {
            Published.Add(request);
            return Task.FromResult(new AppNotification());
        }

        public Task<IReadOnlyList<AppNotification>> GetRecentAsync(Guid? recipientUserId = null, Guid? recipientAgentId = null,
            string? recipientKey = null, string? topic = null, NotificationSeverity? severity = null, bool? isRead = null, int limit = 50)
            => Task.FromResult<IReadOnlyList<AppNotification>>([]);

        public Task<bool> MarkAsReadAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null, string? recipientKey = null)
            => Task.FromResult(true);

        public Task<bool> DeleteAsync(Guid id, Guid? recipientUserId = null, Guid? recipientAgentId = null)
            => Task.FromResult(true);
    }

    private static ReportService BuildService(
        string layoutJson,
        CountingQueryService queryService,
        FakeExecutionRepository executionRepository,
        NoopNotificationService notifications,
        bool withCatalog = true)
    {
        var template = new ReportTemplate
        {
            Id = TemplateId,
            Name = "template",
            DatasetType = ReportDatasetType.AgentHardware,
            DefaultFormat = ReportFormat.Markdown,
            LayoutJson = layoutJson,
            IsActive = true
        };

        return new ReportService(
            executionRepository,
            new FakeServerConfigurationRepository(),
            new FakeTemplateRepository(template),
            queryService,
            new ReportHtmlComposerStub(),
            notifications,
            new ThrowingStorageFactory(),
            [],
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ReportingOptions { ProcessingTimeoutSeconds = 30 }),
            NullLogger<ReportService>.Instance,
            withCatalog ? new ReportDatasetCatalogProvider() : null);
    }

    private static ReportExecution PendingExecution() => new()
    {
        Id = ExecutionId,
        TemplateId = TemplateId,
        ClientId = null,
        Format = ReportFormat.Markdown,
        Status = ReportExecutionStatus.Pending,
        CreatedAt = DateTime.UtcNow
    };

    private sealed class ReportHtmlComposerStub : IReportHtmlComposer
    {
        public string Compose(ReportRenderContext context, ReportQueryResult data) => "<html></html>";
    }

    private sealed class ThrowingStorageFactory : IObjectStorageProviderFactory
    {
        public IObjectStorageService CreateObjectStorageService()
            => throw new InvalidOperationException("nao deveria chegar no storage");

        public Task<IObjectStorageService> CreateObjectStorageServiceAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("nao deveria chegar no storage");

        public Task<List<string>> ValidateConfigurationAsync() => Task.FromResult(new List<string>());

        public Task<ObjectStorageTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ObjectStorageTestResult(
                Success: false,
                ConfigurationValid: false,
                BucketReachable: false,
                Errors: [],
                LatencyMs: 0));
    }

    // ── Testes ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Execution_WithInvalidLayout_FailsAndNeverQueriesData()
    {
        var queryService = new CountingQueryService();
        var executionRepository = new FakeExecutionRepository(PendingExecution());
        var notifications = new NoopNotificationService();

        // Layout invalido: dataSources com datasetType ausente (a execucao nao
        // passa pelo validator na gravacao, entao chega aqui cru).
        const string invalidLayout = """
        {
          "dataSources": [
            { "datasetType": 4, "alias": "hw" },
            { "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } }
          ],
          "columns": [ { "field": "hw.agentHostname" } ]
        }
        """;

        var service = BuildService(invalidLayout, queryService, executionRepository, notifications);

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ProcessExecutionAsync(ExecutionId));

        Assert.That(queryService.Calls, Is.Zero,
            "o QueryAsync NAO pode rodar quando o layout e invalido");
        Assert.That(executionRepository.FailedStatus, Is.EqualTo(ReportExecutionStatus.Failed),
            "a execucao precisa terminar como Failed");
        Assert.That(executionRepository.FailedMessage, Is.Not.Null.And.Not.Empty,
            "a mensagem de validacao precisa ser gravada na execucao");
        Assert.That(erro!.Message, Does.Contain("invalido"));
    }

    [Test]
    public async Task Execution_WithUnknownFieldReference_FailsAndNeverQueriesData()
    {
        var queryService = new CountingQueryService();
        var executionRepository = new FakeExecutionRepository(PendingExecution());
        var notifications = new NoopNotificationService();

        // Forma valida, mas o campo nao existe no catalogo: so o resolver pega.
        const string invalidLayout = """
        {
          "dataSources": [
            { "datasetType": 4, "alias": "hw" },
            { "datasetType": 29, "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } }
          ],
          "columns": [ { "field": "hw.campoQueNaoExiste" } ]
        }
        """;

        var service = BuildService(invalidLayout, queryService, executionRepository, notifications);

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ProcessExecutionAsync(ExecutionId));

        Assert.That(queryService.Calls, Is.Zero,
            "o resolver precisa barrar antes de consultar os dados");
        Assert.That(erro!.Message, Does.Contain("campoQueNaoExiste"));
        Assert.That(executionRepository.FailedStatus, Is.EqualTo(ReportExecutionStatus.Failed));
    }

    [Test]
    public async Task Execution_WithValidLayout_ReachesTheQuery()
    {
        var queryService = new CountingQueryService();
        var executionRepository = new FakeExecutionRepository(PendingExecution());
        var notifications = new NoopNotificationService();

        const string validLayout = """
        {
          "dataSources": [
            { "datasetType": 4, "alias": "hw" },
            { "datasetType": 29, "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } }
          ],
          "columns": [ { "field": "hw.agentHostname" } ]
        }
        """;

        var service = BuildService(validLayout, queryService, executionRepository, notifications);

        // Vai falhar depois, no storage (que lança de proposito), mas o que
        // importa e que a VALIDACAO deixou passar e o QueryAsync rodou.
        await Assert.CatchAsync(async () => await service.ProcessExecutionAsync(ExecutionId));

        Assert.That(queryService.Calls, Is.EqualTo(1),
            "layout valido precisa chegar ao QueryAsync");
    }
}
