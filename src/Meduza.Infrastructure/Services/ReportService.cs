using Meduza.Core.Configuration;
using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
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
    private readonly Dictionary<ReportFormat, IReportRenderer> _renderers;
    private readonly INotificationService _notificationService;
    private readonly ILogger<ReportService> _logger;
    private readonly IMemoryCache _cache;
    private readonly ReportingOptions _options;
    private readonly string _outputDirectory;
    
    // Cache key pattern for report results
    private const string CacheKeyFormat = "report-exec:{0}";

    public ReportService(
        IReportExecutionRepository executionRepository,
        IServerConfigurationRepository serverConfigurationRepository,
        IReportTemplateRepository templateRepository,
        IReportDatasetQueryService datasetQueryService,
        INotificationService notificationService,
        IEnumerable<IReportRenderer> renderers,
        IMemoryCache cache,
        IOptions<ReportingOptions> options,
        ILogger<ReportService> logger)
    {
        _executionRepository = executionRepository;
        _serverConfigurationRepository = serverConfigurationRepository;
        _templateRepository = templateRepository;
        _datasetQueryService = datasetQueryService;
        _notificationService = notificationService;
        _cache = cache;
        _options = options.Value;
        _logger = logger;

        _renderers = renderers.ToDictionary(renderer => renderer.Format);

        _outputDirectory = Path.Combine(AppContext.BaseDirectory, "report-exports");
        Directory.CreateDirectory(_outputDirectory);
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
                    return cached;
                return execution;
            }

            var startedAt = DateTime.UtcNow;

            _logger.LogInformation("Report execution {ExecutionId} started (timeout: {TimeoutSeconds}s)", 
                executionId, effectiveOptions.ProcessingTimeoutSeconds);
            
            await _executionRepository.UpdateStatusAsync(executionId, clientId, ReportExecutionStatus.Running);

            var template = await _templateRepository.GetByIdAsync(execution.TemplateId, null)
                ?? throw new InvalidOperationException($"Template {execution.TemplateId} not found.");

            var data = await _datasetQueryService.QueryAsync(template, execution.FiltersJson, timeoutCts.Token);

            if (!_renderers.TryGetValue(execution.Format, out var renderer))
                throw new InvalidOperationException($"Format {execution.Format} is not enabled. Supported formats: {string.Join(", ", _renderers.Keys)}.");

            var document = await renderer.RenderAsync(template.Name, data, timeoutCts.Token);

            var fileName = $"report-{executionId:N}.{document.FileExtension}";
            var filePath = Path.Combine(_outputDirectory, fileName);
            await File.WriteAllBytesAsync(filePath, document.Content, timeoutCts.Token);

            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            await _executionRepository.UpdateResultAsync(
                executionId,
                clientId,
                filePath,
                document.ContentType,
                document.Content.LongLength,
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

    public async Task<(byte[] Content, string ContentType, string FileName)?> GetDownloadAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default)
    {
        // Try cache first to avoid DB query on repeated downloads
        var cacheKey = string.Format(CacheKeyFormat, executionId);
        ReportExecution? execution = null;
        
        if (!_cache.TryGetValue(cacheKey, out execution))
        {
            // Cache miss - fetch from DB
            execution = await _executionRepository.GetByIdAsync(executionId, clientId);
            if (execution is not null)
            {
                // Cache for 1 hour to avoid repeated DB queries
                _cache.Set(cacheKey, execution, TimeSpan.FromHours(1));
            }
        }
        
        if (execution is null || execution.Status != ReportExecutionStatus.Completed || string.IsNullOrWhiteSpace(execution.ResultPath))
            return null;

        if (!File.Exists(execution.ResultPath))
            return null;

        // Use streaming for large files - don't load entire file into memory
        var fileInfo = new FileInfo(execution.ResultPath);
        
        // Only load into memory if file is reasonably sized (< 50MB)
        // For larger files, consider returning a FileStream and updating controller
        if (fileInfo.Length > 52_428_800) // 50MB threshold
        {
            _logger.LogWarning("Report file {FileName} is {SizeBytes} bytes. Consider implementing streaming download.", 
                fileInfo.Name, fileInfo.Length);
        }

        var content = await File.ReadAllBytesAsync(execution.ResultPath, cancellationToken);
        var fileName = Path.GetFileName(execution.ResultPath);
        var contentType = string.IsNullOrWhiteSpace(execution.ResultContentType)
            ? "application/octet-stream"
            : execution.ResultContentType;

        _logger.LogInformation("Downloaded report {ExecutionId} ({SizeBytes} bytes)", executionId, fileInfo.Length);

        return (content, contentType, fileName);
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
