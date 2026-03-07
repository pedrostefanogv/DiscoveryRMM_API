using Meduza.Core.Entities;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Meduza.Infrastructure.Services;

public class ReportService : IReportService
{
    private readonly IReportExecutionRepository _executionRepository;
    private readonly IReportTemplateRepository _templateRepository;
    private readonly IReportDatasetQueryService _datasetQueryService;
    private readonly Dictionary<ReportFormat, IReportRenderer> _renderers;
    private readonly ILogger<ReportService> _logger;
    private readonly string _outputDirectory;

    public ReportService(
        IReportExecutionRepository executionRepository,
        IReportTemplateRepository templateRepository,
        IReportDatasetQueryService datasetQueryService,
        IEnumerable<IReportRenderer> renderers,
        ILogger<ReportService> logger)
    {
        _executionRepository = executionRepository;
        _templateRepository = templateRepository;
        _datasetQueryService = datasetQueryService;
        _logger = logger;

        _renderers = renderers.ToDictionary(renderer => renderer.Format);

        _outputDirectory = Path.Combine(AppContext.BaseDirectory, "report-exports");
        Directory.CreateDirectory(_outputDirectory);
    }

    public async Task<ReportExecution> ProcessExecutionAsync(Guid executionId, Guid clientId, CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository.GetByIdAsync(executionId, clientId)
            ?? throw new InvalidOperationException($"Report execution {executionId} not found.");

        if (execution.Status != ReportExecutionStatus.Pending)
            return execution;

        var startedAt = DateTime.UtcNow;

        try
        {
            await _executionRepository.UpdateStatusAsync(executionId, clientId, ReportExecutionStatus.Running);

            var template = await _templateRepository.GetByIdAsync(execution.TemplateId, clientId)
                ?? throw new InvalidOperationException($"Template {execution.TemplateId} not found for client {clientId}.");

            var data = await _datasetQueryService.QueryAsync(template, clientId, execution.FiltersJson, cancellationToken);

            if (!_renderers.TryGetValue(execution.Format, out var renderer))
                throw new InvalidOperationException($"Format {execution.Format} is not enabled. Supported formats: {string.Join(", ", _renderers.Keys)}.");

            var document = await renderer.RenderAsync(template.Name, data, cancellationToken);

            var fileName = $"report-{executionId:N}.{document.FileExtension}";
            var filePath = Path.Combine(_outputDirectory, fileName);
            await File.WriteAllBytesAsync(filePath, document.Content, cancellationToken);

            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            await _executionRepository.UpdateResultAsync(
                executionId,
                clientId,
                filePath,
                document.ContentType,
                document.Content.LongLength,
                data.Rows.Count,
                elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process report execution {ExecutionId}", executionId);
            await _executionRepository.UpdateStatusAsync(executionId, clientId, ReportExecutionStatus.Failed, ex.Message);
        }

        return await _executionRepository.GetByIdAsync(executionId, clientId)
            ?? throw new InvalidOperationException($"Report execution {executionId} not found after processing.");
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

    public async Task<(byte[] Content, string ContentType, string FileName)?> GetDownloadAsync(Guid executionId, Guid clientId, CancellationToken cancellationToken = default)
    {
        var execution = await _executionRepository.GetByIdAsync(executionId, clientId);
        if (execution is null || execution.Status != ReportExecutionStatus.Completed || string.IsNullOrWhiteSpace(execution.ResultPath))
            return null;

        if (!File.Exists(execution.ResultPath))
            return null;

        var content = await File.ReadAllBytesAsync(execution.ResultPath, cancellationToken);
        var fileName = Path.GetFileName(execution.ResultPath);
        var contentType = string.IsNullOrWhiteSpace(execution.ResultContentType)
            ? "application/octet-stream"
            : execution.ResultContentType;

        return (content, contentType, fileName);
    }
}
