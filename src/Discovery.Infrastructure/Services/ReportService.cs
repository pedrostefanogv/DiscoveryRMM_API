using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Discovery.Infrastructure.Services;

public class ReportService : IReportService
{
    private readonly IReportExecutionRepository _executionRepository;
    private readonly IServerConfigurationRepository _serverConfigurationRepository;
    private readonly IReportTemplateRepository _templateRepository;
    private readonly IReportDatasetQueryService _datasetQueryService;
    private readonly IReportDatasetCatalogProvider? _datasetCatalog;
    private readonly IReportHtmlComposer _htmlComposer;
    private readonly Dictionary<ReportFormat, IReportRenderer> _renderers;
    private readonly INotificationService _notificationService;
    private readonly IObjectStorageProviderFactory _storageProviderFactory;
    private readonly ILogger<ReportService> _logger;
    private readonly IMemoryCache _cache;
    private readonly ReportingOptions _options;
    private readonly IServiceScopeFactory? _scopeFactory;
    
    // Cache key pattern for report results. O escopo do cliente faz parte da
    // chave: sem isso uma execucao cacheada por um cliente podia ser devolvida
    // (inclusive com URL de download) para uma requisicao de outro cliente.
    private const string CacheKeyFormat = "report-exec:{0}:{1}";

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
        ILogger<ReportService> logger,
        IReportDatasetCatalogProvider? datasetCatalog = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _executionRepository = executionRepository;
        _serverConfigurationRepository = serverConfigurationRepository;
        _templateRepository = templateRepository;
        _datasetQueryService = datasetQueryService;
        _datasetCatalog = datasetCatalog;
        _htmlComposer = htmlComposer;
        _notificationService = notificationService;
        _storageProviderFactory = storageProviderFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;

        _renderers = renderers.ToDictionary(renderer => renderer.Format);
    }

    /// <summary>
    /// Valida o layout ANTES de consultar os dados na execucao.
    ///
    /// Leva em conta as mesmas regras da gravacao e do preview:
    ///  - <see cref="ReportLayoutValidator.ValidateJson"/> cobre forma, limites e
    ///    o grafo de joins;
    ///  - <see cref="ReportLayoutFieldResolver.Resolve"/> cobre as referencias de
    ///    campo e as chaves de join contra o catalogo real.
    ///
    /// Sem o catalogo injetado, roda apenas a validacao de forma (o resolver
    /// precisa do catalogo para saber quais campos existem).
    /// </summary>
    private void ValidateLayoutForExecution(ReportTemplate template)
    {
        var errors = ReportLayoutValidator.ValidateJson(template.LayoutJson).ToList();

        if (_datasetCatalog is not null)
        {
            errors.AddRange(ReportLayoutFieldResolver.Resolve(
                ReportLayoutDefinitionParser.ParseOrDefault(template.LayoutJson),
                _datasetCatalog.GetAll()));
        }

        if (errors.Count == 0)
            return;

        throw new InvalidOperationException(
            $"O layout do template '{template.Name}' e invalido e o relatorio nao pode ser gerado: " +
            string.Join(" ", errors));
    }

    public async Task<ReportExecution> ProcessExecutionAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default, ReportQueryScope? scope = null)
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
                var cacheKey = BuildExecutionCacheKey(executionId, clientId);
                if (_cache.TryGetValue(cacheKey, out ReportExecution? cached))
                    return cached!;
                return execution;
            }

            var startedAt = DateTime.UtcNow;

            _logger.LogInformation("Report execution {ExecutionId} started (timeout: {TimeoutSeconds}s)", 
                executionId, effectiveOptions.ProcessingTimeoutSeconds);
            
            // Claim atomico: com mais de uma replica da API, apenas a instancia
            // que conseguir mudar Pending -> Running processa esta execucao.
            if (!await _executionRepository.TryClaimPendingAsync(executionId, clientId))
            {
                _logger.LogInformation(
                    "Report execution {ExecutionId} was already claimed by another worker; skipping.",
                    executionId);

                var claimedElsewhere = await _executionRepository.GetByIdAsync(executionId, clientId);
                return claimedElsewhere ?? execution;
            }

            var template = await _templateRepository.GetByIdAsync(execution.TemplateId, null)
                ?? throw new InvalidOperationException($"Template {execution.TemplateId} not found.");

            // O create/update e o preview validam o layout, mas a EXECUCAO nao:
            // um template antigo (ou editado direto no banco) chegava ate aqui e
            // so falhava por acidente dentro do join, com mensagem ruim. A guarda
            // fica DEPOIS do TryClaimPendingAsync para nao gastar tempo em
            // execucoes ja reivindicadas por outra instancia.
            ValidateLayoutForExecution(template);

            // O escopo efetivo (execucao/usuario) sobrescreve o FiltersJson.
            var scopedFilters = ApplyQueryScope(execution.FiltersJson, execution.ClientId, scope);
            var data = await _datasetQueryService.QueryAsync(template, scopedFilters, timeoutCts.Token);
            var document = await RenderDocumentAsync(template, execution.Format, data, timeoutCts.Token);

            var fileName = $"report-{executionId:N}.{document.FileExtension}";
            var objectKey = ComposeReportObjectKey(clientId, executionId, fileName);

            var storageService = await _storageProviderFactory.CreateObjectStorageServiceAsync(timeoutCts.Token);
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
            _cache.Set(BuildExecutionCacheKey(executionId, clientId), completed, TimeSpan.FromHours(1));

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
                        ? $"/api/v1/reports/executions/{executionId}/download?clientId={clientId}"
                        : $"/api/v1/reports/executions/{executionId}/download"
                },
                RecipientUserId: ParseRecipientUserId(createdBy),
                RecipientKey: createdBy,
                CreatedBy: "ReportService"),
                cancellationToken);

            return completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Report execution {ExecutionId} cancelled due to host/caller cancellation.", executionId);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Report execution {ExecutionId} timed out after {TimeoutSeconds}s",
                executionId, effectiveOptions.ProcessingTimeoutSeconds);

            var timeoutMessage = $"Report processing timed out after {effectiveOptions.ProcessingTimeoutSeconds} seconds";
            try
            {
                await _executionRepository.UpdateStatusAsync(executionId, clientId, ReportExecutionStatus.Failed, timeoutMessage);
            }
            catch (Exception statusEx)
            {
                _logger.LogError(statusEx, "Failed to mark report execution {ExecutionId} as Failed (timeout).", executionId);
            }

            await PublishFailureNotificationAsync(executionId, createdBy, timeoutMessage, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process report execution {ExecutionId}", executionId);

            var safeMessage = SanitizeFailureMessage(ex);
            try
            {
                await _executionRepository.UpdateStatusAsync(executionId, clientId, ReportExecutionStatus.Failed, safeMessage);
            }
            catch (Exception statusEx)
            {
                // Nao deixar a falha ao gravar o status mascarar a causa original.
                _logger.LogError(statusEx, "Failed to mark report execution {ExecutionId} as Failed.", executionId);
            }

            await PublishFailureNotificationAsync(executionId, createdBy, safeMessage, cancellationToken);

            throw;
        }
    }

    private static Guid? ParseRecipientUserId(string? createdBy)
        => Guid.TryParse(createdBy, out var userId) ? userId : null;

    private static string SanitizeFailureMessage(Exception ex)
        => ex is InvalidOperationException or ArgumentException
            ? ex.Message
            : "Erro interno ao gerar o relatorio. Consulte os logs do servidor para detalhes.";

    private async Task PublishFailureNotificationAsync(Guid executionId, string? createdBy, string errorMessage, CancellationToken ct)
    {
        try
        {
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
                    error = errorMessage
                },
                RecipientUserId: ParseRecipientUserId(createdBy),
                RecipientKey: createdBy,
                CreatedBy: "ReportService"),
                ct);
        }
        catch (Exception notifyEx)
        {
            _logger.LogWarning(notifyEx, "Failed to publish failure notification for report execution {ExecutionId}.", executionId);
        }
    }

    public async Task<IReadOnlyList<ReportExecution>> ProcessPendingAsync(int maxItems, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = await GetEffectiveReportingOptionsAsync();
        var maxConcurrent = Math.Clamp(effectiveOptions.MaxConcurrentExecutions, 1, 16);

        // Recupera execucoes presas em Running (processo morto no meio do
        // trabalho). O corte usa o timeout de processamento + margem.
        var staleBefore = DateTime.UtcNow.AddSeconds(-(effectiveOptions.ProcessingTimeoutSeconds + 60));
        var requeued = await _executionRepository.RequeueStaleRunningAsync(staleBefore);
        if (requeued > 0)
        {
            _logger.LogWarning("Requeued {Count} stale report execution(s) stuck in Running.", requeued);
        }

        var pending = await _executionRepository.GetPendingAsync(maxItems);
        if (pending.Count == 0)
            return [];

        if (maxConcurrent == 1 || _scopeFactory is null)
        {
            var sequentialResults = new List<ReportExecution>(pending.Count);
            foreach (var execution in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var processed = await ProcessExecutionAsync(execution.Id, execution.ClientId, cancellationToken);
                    sequentialResults.Add(processed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Report execution {ExecutionId} failed while processing the pending batch.", execution.Id);
                }
            }

            return sequentialResults;
        }

        using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        var results = new ReportExecution?[pending.Count];

        // Cada task concorrente cria seu proprio AsyncServiceScope para isolar
        // o DbContext (EF Core nao e thread-safe).
        var tasks = pending.Select(async (execution, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var workerScope = _scopeFactory.CreateAsyncScope();
                var scopedReportService = workerScope.ServiceProvider.GetRequiredService<IReportService>();
                results[index] = await scopedReportService.ProcessExecutionAsync(execution.Id, execution.ClientId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Report execution {ExecutionId} failed while processing the pending batch.", execution.Id);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        return results.Where(result => result is not null).Select(result => result!).ToList();
    }

    public async Task<ReportPreviewResult> PreviewAsync(ReportTemplate template, ReportFormat format, string? filtersJson = null, CancellationToken cancellationToken = default, ReportQueryScope? scope = null)
    {
        var effectiveOptions = await GetEffectiveReportingOptionsAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveOptions.ProcessingTimeoutSeconds));

        var previewFilters = ClampPreviewFilters(ApplyQueryScope(filtersJson, null, scope));
        var data = await _datasetQueryService.QueryAsync(template, previewFilters, timeoutCts.Token);
        var document = await RenderDocumentAsync(template, format, data, timeoutCts.Token);
        var context = BuildRenderContext(template);

        return new ReportPreviewResult
        {
            Document = document,
            RowCount = data.Rows.Count,
            Title = context.Title
        };
    }

    public async Task<ReportHtmlPreviewResult> PreviewHtmlAsync(ReportTemplate template, string? filtersJson = null, CancellationToken cancellationToken = default, ReportQueryScope? scope = null)
    {
        var effectiveOptions = await GetEffectiveReportingOptionsAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveOptions.ProcessingTimeoutSeconds));

        var previewFilters = ClampPreviewFilters(ApplyQueryScope(filtersJson, null, scope));
        var data = await _datasetQueryService.QueryAsync(template, previewFilters, timeoutCts.Token);
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
        var execution = await GetDownloadableExecutionAsync(executionId, clientId);
        if (execution is null)
            return null;

        var serverConfig = await _serverConfigurationRepository.GetOrCreateDefaultAsync();
        var ttlHours = serverConfig.ObjectStorageUrlTtlHours > 0 ? serverConfig.ObjectStorageUrlTtlHours : 24;

        var storageService = await _storageProviderFactory.CreateObjectStorageServiceAsync(cancellationToken);
        var downloadUrl = await storageService.GetPresignedDownloadUrlAsync(execution.StorageObjectKey!, ttlHours, cancellationToken);

        return downloadUrl;
    }

    public async Task<ReportDownloadResult?> GetDownloadAsync(Guid executionId, Guid? clientId = null, CancellationToken cancellationToken = default)
    {
        var execution = await GetDownloadableExecutionAsync(executionId, clientId);
        if (execution is null)
            return null;

        var effectiveOptions = await GetEffectiveReportingOptionsAsync();
        using var downloadTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadTimeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveOptions.FileDownloadTimeoutSeconds));

        var storageService = await _storageProviderFactory.CreateObjectStorageServiceAsync(downloadTimeoutCts.Token);

        // Faz o streaming pelo proprio servidor sem bufferizar arquivos grandes na
        // RAM: o controller habilita range processing apenas quando CanSeek == true.
        var content = await storageService.DownloadAsync(execution.StorageObjectKey!, downloadTimeoutCts.Token);

        return new ReportDownloadResult
        {
            Content = content,
            ContentType = string.IsNullOrWhiteSpace(execution.StorageContentType) ? "application/octet-stream" : execution.StorageContentType,
            FileName = ResolveDownloadFileName(execution),
            SizeBytes = execution.StorageSizeBytes
        };
    }

    private async Task<ReportExecution?> GetDownloadableExecutionAsync(Guid executionId, Guid? clientId)
    {
        var cacheKey = BuildExecutionCacheKey(executionId, clientId);

        if (!_cache.TryGetValue(cacheKey, out ReportExecution? execution))
        {
            execution = await _executionRepository.GetByIdAsync(executionId, clientId);
            if (execution is not null)
            {
                _cache.Set(cacheKey, execution, TimeSpan.FromHours(1));
            }
        }

        if (execution is null || execution.Status != ReportExecutionStatus.Completed || string.IsNullOrWhiteSpace(execution.StorageObjectKey))
            return null;

        return execution;
    }

    private static string BuildExecutionCacheKey(Guid executionId, Guid? clientId)
        => string.Format(CacheKeyFormat, executionId, clientId?.ToString("N") ?? "global");

    private static string ResolveDownloadFileName(ReportExecution execution)
    {
        var objectKey = execution.StorageObjectKey;
        if (!string.IsNullOrWhiteSpace(objectKey))
        {
            var lastSegment = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(lastSegment))
                return lastSegment;
        }

        return $"report-{execution.Id:N}.{ExtensionFor(execution.StorageContentType)}";
    }

    private static string ExtensionFor(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return "bin";
        if (contentType.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase)) return "xlsx";
        if (contentType.Contains("csv", StringComparison.OrdinalIgnoreCase)) return "csv";
        if (contentType.Contains("markdown", StringComparison.OrdinalIgnoreCase)) return "md";
        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)) return "pdf";
        return "bin";
    }

    /// <summary>
    /// Limita o preview sincrono a no maximo 500 linhas para evitar OOM/timeout na API.
    /// </summary>
    private static string ClampPreviewFilters(string? filtersJson)
    {
        const int maxPreviewRows = 500;
        JsonObject body;
        if (string.IsNullOrWhiteSpace(filtersJson))
        {
            body = new JsonObject();
        }
        else
        {
            try
            {
                body = JsonNode.Parse(filtersJson) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                body = new JsonObject();
            }
        }

        body.Remove("allRows");
        if (body.TryGetPropertyValue("limit", out var limitNode) &&
            limitNode is JsonValue limitValue &&
            limitValue.TryGetValue<int>(out var requestedLimit) &&
            requestedLimit > 0)
        {
            body["limit"] = Math.Min(requestedLimit, maxPreviewRows);
        }
        else
        {
            body["limit"] = maxPreviewRows;
        }

        return body.ToJsonString();
    }

    /// <summary>
    /// Mescla o escopo efetivo no FiltersJson. O escopo (execucao/usuario) SEMPRE
    /// prevalece sobre o que veio do cliente — e o que impede um relatorio de um
    /// cliente conter dados de outros.
    /// </summary>
    public static string? ApplyQueryScope(string? filtersJson, Guid? executionClientId, ReportQueryScope? scope)
    {
        var clientId = executionClientId ?? scope?.ClientId;
        var siteId = scope?.SiteId;

        if (clientId is null && siteId is null)
            return filtersJson;

        JsonObject body;
        if (string.IsNullOrWhiteSpace(filtersJson))
        {
            body = new JsonObject();
        }
        else
        {
            try
            {
                body = JsonNode.Parse(filtersJson) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                body = new JsonObject();
            }
        }

        if (clientId is { } resolvedClient)
            body["clientId"] = resolvedClient.ToString();

        if (siteId is { } resolvedSite)
            body["siteId"] = resolvedSite.ToString();

        return body.ToJsonString();
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
        {
            // Ultimo recurso: formatos legados (ex.: o extinto Pdf = 1) ou nao
            // registrados nao podem derrubar a geracao com 500. Cai para Markdown
            // e registra a substituicao; a migracao M194 corrige os dados.
            if (!_renderers.TryGetValue(ReportFormat.Markdown, out renderer))
                throw new InvalidOperationException($"Format {format} is not enabled. Supported formats: {string.Join(", ", _renderers.Keys)}.");

            _logger.LogWarning(
                "Report format {Format} has no renderer registered; falling back to Markdown for template {TemplateId}.",
                format, template.Id);
        }

        // Campos calculados valem para TODOS os formatos (antes so o composer HTML
        // aplicava; Markdown/XLSX/CSV saiam com a coluna calculada vazia).
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(template.LayoutJson);
        var enrichedRows = ReportComputedFieldEvaluator.Enrich(layout, data.Rows);

        var enrichedData = ReferenceEquals(enrichedRows, data.Rows)
            ? data
            : new ReportQueryResult { Columns = data.Columns, Rows = enrichedRows };

        return renderer.RenderAsync(BuildRenderContext(template), enrichedData, cancellationToken);
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
                ProcessingTimeoutSeconds = persisted.ProcessingTimeoutSeconds > 0 ? persisted.ProcessingTimeoutSeconds : fallback.ProcessingTimeoutSeconds,
                FileDownloadTimeoutSeconds = persisted.FileDownloadTimeoutSeconds > 0 ? persisted.FileDownloadTimeoutSeconds : fallback.FileDownloadTimeoutSeconds,
                MaxConcurrentExecutions = persisted.MaxConcurrentExecutions > 0 ? persisted.MaxConcurrentExecutions : fallback.MaxConcurrentExecutions,
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
