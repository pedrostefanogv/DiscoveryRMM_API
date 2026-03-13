using Meduza.Core.Configuration;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Meduza.Core.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Meduza.Infrastructure.Services;

public class ReportService : IReportService
{
    private readonly IReportExecutionRepository _executionRepository;
    private readonly IServerConfigurationRepository _serverConfigurationRepository;
    private readonly IReportTemplateRepository _templateRepository;
    private readonly IReportDatasetQueryService _datasetQueryService;
    private readonly IReportHtmlComposer _htmlComposer;
    private readonly Dictionary<ReportFormat, IReportRenderer> _renderers;
    private readonly INotificationService _notificationService;
    private readonly IObjectStorageProviderFactory _storageProviderFactory;
    private readonly ILogger<ReportService> _logger;
    private readonly IMemoryCache _cache;
    private readonly ReportingOptions _options;
    
    // Cache key pattern for report results
    private const string CacheKeyFormat = "report-exec:{0}";

    public ReportService(
        IReportExecutionRepository executionRepository,
        IServerConfigurationRepository serverConfigurationRepository,
        IReportTemplateRepository templateRepository,
        IReportDatasetQueryService datasetQueryService,
        IReportHtmlComposer htmlComposer,
        INotificationService notificationService,
        IObjectStorageProviderFactory storageProviderFactory,
        IEnumerable<IReportRenderer> renderers,
        IMemoryCache cache,
        IOptions<ReportingOptions> options,
        ILogger<ReportService> logger)
    {
        _executionRepository = executionRepository;
        _serverConfigurationRepository = serverConfigurationRepository;
        _templateRepository = templateRepository;
        _datasetQueryService = datasetQueryService;
        _htmlComposer = htmlComposer;
        _notificationService = notificationService;
        _storageProviderFactory = storageProviderFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;

        _renderers = renderers.ToDictionary(renderer => renderer.Format);
    }

    public async Task<ReportExecution> ProcessExecutionAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = await GetEffectiveReportingOptionsAsync();

        // Create a timeout for processing based on configured timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveOptions.ProcessingTimeoutSeconds));

        ReportExecution? execution = null;
        string? createdBy = null;

        try
        {
            execution = await _executionRepository.GetByIdAsync(executionId, clientId)
                ?? throw new InvalidOperationException($"Report execution {executionId} not found.");
            
            createdBy = execution.CreatedBy;

            if (execution.Status != ReportExecutionStatus.Pending)
            {
                // Return cached result if already processed
                var cacheKey = string.Format(CacheKeyFormat, executionId);
                if (_cache.TryGetValue(cacheKey, out ReportExecution? cached))
                    return cached!;
                return execution;
            }

            var startedAt = DateTime.UtcNow;

            _logger.LogInformation("Report execution {ExecutionId} started (timeout: {TimeoutSeconds}s)", 
                executionId, effectiveOptions.ProcessingTimeoutSeconds);
            
            await _executionRepository.UpdateStatusAsync(executionId, clientId, ReportExecutionStatus.Running);

            var template = await _templateRepository.GetByIdAsync(execution.TemplateId, null)
                ?? throw new InvalidOperationException($"Template {execution.TemplateId} not found.");

            var data = await _datasetQueryService.QueryAsync(template, execution.FiltersJson, timeoutCts.Token);
            var document = await RenderDocumentAsync(template, execution.Format, data, timeoutCts.Token);

            var fileName = $"report-{executionId:N}.{document.FileExtension}";
            var objectKey = ComposeReportObjectKey(clientId, executionId, fileName);

            var storageService = _storageProviderFactory.CreateObjectStorageService();
            await using var contentStream = new MemoryStream(document.Content, writable: false);
            var storageObject = await storageService.UploadAsync(
                objectKey,
                contentStream,
                document.ContentType,
                timeoutCts.Token);

            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            await _executionRepository.UpdateResultAsync(
                executionId,
                clientId,
                storageObject.ObjectKey,
                storageObject.Bucket,
                document.ContentType,
                storageObject.SizeBytes,
                storageObject.Checksum,
                (int)storageObject.StorageProvider,
                data.Rows.Count,
                elapsed);

            _logger.LogInformation("Report execution {ExecutionId} completed in {ElapsedMs}ms", executionId, elapsed);

            // Fetch updated execution after update
            var completed = await _executionRepository.GetByIdAsync(executionId, clientId)
                ?? throw new InvalidOperationException($"Report execution {executionId} not found after processing.");

            // Cache for 1 hour to avoid repeated DB queries for downloads
            _cache.Set(string.Format(CacheKeyFormat, executionId), completed, TimeSpan.FromHours(1));

            await _notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "report.completed",
                Topic: "reports",
                Title: "Relatorio concluido",
                Message: $"O relatorio '{template.Name}' foi gerado com sucesso.",
                Severity: NotificationSeverity.Informational,
                Payload: new
                {
                    executionId,
                    templateId = template.Id,
                    templateName = template.Name,
                    status = ReportExecutionStatus.Completed,
                    rowCount = data.Rows.Count,
                    format = execution.Format,
                    downloadPath = clientId.HasValue
                        ? $"/api/reports/executions/{executionId}/download?clientId={clientId}"
                        : $"/api/reports/executions/{executionId}/download"
                },
                RecipientUserId: null,
                RecipientKey: createdBy,
                CreatedBy: "ReportService"),
                cancellationToken);

            return completed;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Report execution {ExecutionId} timed out after {TimeoutSeconds}s", 
                executionId, effectiveOptions.ProcessingTimeoutSeconds);
            await _executionRepository.UpdateStatusAsync(executionId, clientId, ReportExecutionStatus.Failed, 
                $"Report processing timed out after {effectiveOptions.ProcessingTimeoutSeconds} seconds");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process report execution {ExecutionId}", executionId);
            await _executionRepository.UpdateStatusAsync(executionId, clientId, ReportExecutionStatus.Failed, ex.Message);

            await _notificationService.PublishAsync(new NotificationPublishRequest(
                EventType: "report.failed",
                Topic: "reports",
                Title: "Falha na geracao do relatorio",
                Message: "Ocorreu um erro ao processar o relatorio solicitado.",
                Severity: NotificationSeverity.Critical,
                Payload: new
                {
                    executionId,
                    status = ReportExecutionStatus.Failed,
                    error = ex.Message
                },
                RecipientUserId: null,
                RecipientKey: createdBy,
                CreatedBy: "ReportService"),
                cancellationToken);

            throw;
        }
    }

    public async Task<IReadOnlyList<ReportExecution>> ProcessPendingAsync(int maxItems, CancellationToken cancellationToken = default)
    {
        var pending = await _executionRepository.GetPendingAsync(maxItems);
        var results = new List<ReportExecution>(pending.Count);

        foreach (var execution in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processed = await ProcessExecutionAsync(execution.Id, execution.ClientId, cancellationToken);
            results.Add(processed);
        }

        return results;
    }

    public async Task<ReportPreviewResult> PreviewAsync(ReportTemplate template, ReportFormat format, string? filtersJson = null, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = await GetEffectiveReportingOptionsAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveOptions.ProcessingTimeoutSeconds));

        var data = await _datasetQueryService.QueryAsync(template, filtersJson, timeoutCts.Token);
        var document = await RenderDocumentAsync(template, format, data, timeoutCts.Token);
        var context = BuildRenderContext(template);

        return new ReportPreviewResult
        {
            Document = document,
            RowCount = data.Rows.Count,
            Title = context.Title
        };
    }

    public async Task<ReportHtmlPreviewResult> PreviewHtmlAsync(ReportTemplate template, string? filtersJson = null, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = await GetEffectiveReportingOptionsAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveOptions.ProcessingTimeoutSeconds));

        var data = await _datasetQueryService.QueryAsync(template, filtersJson, timeoutCts.Token);
        var context = BuildRenderContext(template);

        return new ReportHtmlPreviewResult
        {
            Html = _htmlComposer.Compose(context, data),
            RowCount = data.Rows.Count,
            Title = context.Title
        };
    }

    public async Task<string?> GetPresignedDownloadUrlAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default)
    {
        var cacheKey = string.Format(CacheKeyFormat, executionId);
        ReportExecution? execution = null;

        if (!_cache.TryGetValue(cacheKey, out execution))
        {
            execution = await _executionRepository.GetByIdAsync(executionId, clientId);
            if (execution is not null)
            {
                _cache.Set(cacheKey, execution, TimeSpan.FromHours(1));
            }
        }

        if (execution is null || execution.Status != ReportExecutionStatus.Completed || string.IsNullOrWhiteSpace(execution.StorageObjectKey))
            return null;

        var serverConfig = await _serverConfigurationRepository.GetOrCreateDefaultAsync();
        var ttlHours = serverConfig.ObjectStorageUrlTtlHours > 0 ? serverConfig.ObjectStorageUrlTtlHours : 24;

        var storageService = _storageProviderFactory.CreateObjectStorageService();
        var downloadUrl = await storageService.GetPresignedDownloadUrlAsync(execution.StorageObjectKey, ttlHours, cancellationToken);

        return downloadUrl;
    }

    private static string ComposeReportObjectKey(Guid? clientId, Guid executionId, string fileName)
    {
        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? $"report-{executionId:N}.bin" : fileName.Trim();
        return clientId.HasValue && clientId.Value != Guid.Empty
            ? $"clients/{clientId.Value:N}/reports/{executionId:N}/{safeFileName}"
            : $"global/reports/{executionId:N}/{safeFileName}";
    }

    private Task<ReportDocument> RenderDocumentAsync(ReportTemplate template, ReportFormat format, ReportQueryResult data, CancellationToken cancellationToken)
    {
        if (!_renderers.TryGetValue(format, out var renderer))
            throw new InvalidOperationException($"Format {format} is not enabled. Supported formats: {string.Join(", ", _renderers.Keys)}.");

        return renderer.RenderAsync(BuildRenderContext(template), data, cancellationToken);
    }

    private static ReportRenderContext BuildRenderContext(ReportTemplate template)
    {
        return new ReportRenderContext
        {
            TemplateName = string.IsNullOrWhiteSpace(template.Name) ? "Report Preview" : template.Name,
            LayoutJson = template.LayoutJson
        };
    }

    private async Task<ReportingOptions> GetEffectiveReportingOptionsAsync()
    {
        var fallback = _options;
        var server = await _serverConfigurationRepository.GetOrCreateDefaultAsync();

        if (string.IsNullOrWhiteSpace(server.ReportingSettingsJson))
            return fallback;

        try
        {
            var persisted = JsonSerializer.Deserialize<ReportingOptions>(server.ReportingSettingsJson, JsonSerializerOptions.Web);
            if (persisted is null)
                return fallback;

            return new ReportingOptions
            {
                EnablePdf = persisted.EnablePdf,
                ProcessingTimeoutSeconds = persisted.ProcessingTimeoutSeconds > 0 ? persisted.ProcessingTimeoutSeconds : fallback.ProcessingTimeoutSeconds,
                FileDownloadTimeoutSeconds = persisted.FileDownloadTimeoutSeconds > 0 ? persisted.FileDownloadTimeoutSeconds : fallback.FileDownloadTimeoutSeconds,
                DatabaseRetentionDays = persisted.DatabaseRetentionDays > 0 ? persisted.DatabaseRetentionDays : fallback.DatabaseRetentionDays,
                FileRetentionDays = persisted.FileRetentionDays > 0 ? persisted.FileRetentionDays : fallback.FileRetentionDays,
                AllowedRetentionDays = persisted.AllowedRetentionDays is { Length: > 0 } ? persisted.AllowedRetentionDays : fallback.AllowedRetentionDays
            };
        }
        catch
        {
            return fallback;
        }
    }
}
