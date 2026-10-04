using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Reports.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Reports;

// ── Layout schema ─────────────────────────────────────────────
public sealed class GetReportLayoutSchemaQueryHandler(IReportDatasetCatalogProvider catalog)
    : IRequestHandler<GetReportLayoutSchemaQuery, Result<ReportLayoutSchemaDto>>
{
    public Task<Result<ReportLayoutSchemaDto>> Handle(GetReportLayoutSchemaQuery q, CancellationToken ct)
    {
        var items = catalog.GetAll();
        var (compatibility, suggestions) = ReportJoinCompatibilityBuilder.Build(items);

        var dataSources = items
            .Select(item => new ReportLayoutSchemaDataSourceDto(
                item.DatasetType,
                item.Name,
                ReportJoinCompatibilityBuilder.DefaultAliasFor(item),
                item.Description))
            .ToList();

        // Converte as sugestoes em regras source/target para a UI.
        var joinRules = suggestions
            .Select(s =>
            {
                var source = items.FirstOrDefault(i => string.Equals(i.Key, s.SourceA, StringComparison.OrdinalIgnoreCase));
                var target = items.FirstOrDefault(i => string.Equals(i.Key, s.SourceB, StringComparison.OrdinalIgnoreCase));
                return new ReportLayoutJoinRuleDto(
                    source?.DatasetType ?? 0,
                    target?.DatasetType ?? 0,
                    s.PreferredKey,
                    s.PreferredKey,
                    s.RecommendedJoinType,
                    null);
            })
            .Where(r => r.SourceDatasetType != 0 || r.TargetDatasetType != 0)
            .ToList();

        var dto = new ReportLayoutSchemaDto(
            PreviewModes: ["document", "html"],
            ResponseDispositions: ["inline", "attachment"],
            SupportedOrientations: ReportLayoutValidator.GetSupportedOrientations().ToList(),
            SupportedColumnFormats: ReportLayoutValidator.GetSupportedColumnFormats().ToList(),
            SupportedSummaryAggregates: ReportLayoutValidator.GetSupportedSummaryAggregates().ToList(),
            MultiSource: new ReportLayoutSchemaMultiSourceDto(
                Enabled: true,
                FieldReferenceMode: "alias.field",
                DataSources: dataSources,
                JoinTypes: ["left", "inner"],
                JoinRules: joinRules,
                Notes: ["Use dataSources apenas com duas ou mais fontes."]),
            Limits: new ReportLayoutSchemaLimitsDto(
                ReportLayoutValidator.GetMaxLayoutJsonLength(),
                ReportLayoutValidator.GetMaxTopLevelColumns(),
                ReportLayoutValidator.GetMaxSections(),
                ReportLayoutValidator.GetMaxColumnsPerSection(),
                ReportLayoutValidator.GetMaxSummaries(),
                ReportLayoutValidator.GetMaxGroupDetails()),
            Notes: ["Limites aplicados pelo ReportLayoutValidator na gravacao e no preview."]);

        return Task.FromResult(Result<ReportLayoutSchemaDto>.Success(dto));
    }
}

// ── Join compatibility ────────────────────────────────────────
public sealed class GetReportJoinCompatibilityQueryHandler(IReportDatasetCatalogProvider catalog)
    : IRequestHandler<GetReportJoinCompatibilityQuery, Result<ReportJoinCompatibilityDto>>
{
    public Task<Result<ReportJoinCompatibilityDto>> Handle(GetReportJoinCompatibilityQuery q, CancellationToken ct)
    {
        var (compatibility, suggestions) = ReportJoinCompatibilityBuilder.Build(catalog.GetAll());

        var dto = new ReportJoinCompatibilityDto(
            Compatibility: compatibility.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            JoinSuggestions: suggestions,
            Note: "Sugestoes derivadas de campos marcados como chave de join no catalogo de datasets.");

        return Task.FromResult(Result<ReportJoinCompatibilityDto>.Success(dto));
    }
}

// ── Autocomplete de campos ────────────────────────────────────
public sealed class GetReportFieldAutocompleteQueryHandler(IReportDatasetCatalogProvider catalog)
    : IRequestHandler<GetReportFieldAutocompleteQuery, Result<ReportAutocompleteDto>>
{
    private const int MaxItems = 50;

    public Task<Result<ReportAutocompleteDto>> Handle(GetReportFieldAutocompleteQuery q, CancellationToken ct)
    {
        var term = q.Term?.Trim() ?? string.Empty;
        var results = new List<ReportAutocompleteItemDto>();

        foreach (var item in catalog.GetAll())
        {
            if (q.DatasetType > 0 && item.DatasetType != q.DatasetType)
                continue;

            foreach (var field in item.FieldMetadata)
            {
                if (string.IsNullOrWhiteSpace(field.Field))
                    continue;

                if (term.Length > 0
                    && !field.Field.Contains(term, StringComparison.OrdinalIgnoreCase)
                    && !(field.Label ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)
                    && !(field.Reference ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var reference = string.IsNullOrWhiteSpace(q.Alias)
                    ? field.Reference ?? field.Field
                    : $"{q.Alias}.{field.Field}";

                results.Add(new ReportAutocompleteItemDto(
                    item.DatasetType,
                    item.Key,
                    item.Name,
                    field.Field,
                    reference,
                    field.DataType,
                    field.IsJoinKey,
                    field.DefaultAlias));

                if (results.Count >= MaxItems)
                    break;
            }

            if (results.Count >= MaxItems)
                break;
        }

        return Task.FromResult(Result<ReportAutocompleteDto>.Success(
            new ReportAutocompleteDto("alias.field", results.Count, results)));
    }
}

// ── Historico de template ─────────────────────────────────────
public sealed class GetReportTemplateHistoryQueryHandler(IReportTemplateRepository repo)
    : IRequestHandler<GetReportTemplateHistoryQuery, Result<IReadOnlyList<ReportTemplateHistoryDto>>>
{
    public async Task<Result<IReadOnlyList<ReportTemplateHistoryDto>>> Handle(GetReportTemplateHistoryQuery q, CancellationToken ct)
    {
        var history = await repo.GetHistoryAsync(q.TemplateId, q.Limit);
        return Result<IReadOnlyList<ReportTemplateHistoryDto>>.Success(history.Select(Map).ToList());
    }

    private static ReportTemplateHistoryDto Map(ReportTemplateHistory history)
    {
        var name = string.Empty;
        var datasetType = 0;
        var defaultFormat = 0;
        var layoutJson = "{}";
        string? filtersJson = null;
        var isActive = true;
        var createdAt = history.ChangedAt;
        var createdBy = history.ChangedBy;

        try
        {
            using var document = JsonDocument.Parse(history.SnapshotJson);
            var root = document.RootElement;

            if (root.TryGetProperty("Name", out var nameElement)) name = nameElement.GetString() ?? string.Empty;
            if (root.TryGetProperty("DatasetType", out var datasetElement) && datasetElement.TryGetInt32(out var parsedDataset)) datasetType = parsedDataset;
            if (root.TryGetProperty("DefaultFormat", out var formatElement) && formatElement.TryGetInt32(out var parsedFormat)) defaultFormat = parsedFormat;
            if (root.TryGetProperty("LayoutJson", out var layoutElement)) layoutJson = layoutElement.GetString() ?? "{}";
            if (root.TryGetProperty("FiltersJson", out var filtersElement) && filtersElement.ValueKind != JsonValueKind.Null) filtersJson = filtersElement.GetString();
            if (root.TryGetProperty("IsActive", out var activeElement)) isActive = activeElement.ValueKind != JsonValueKind.False;
            if (root.TryGetProperty("CreatedAt", out var createdAtElement) && createdAtElement.TryGetDateTime(out var parsedCreatedAt)) createdAt = parsedCreatedAt;
            if (root.TryGetProperty("CreatedBy", out var createdByElement) && createdByElement.ValueKind != JsonValueKind.Null) createdBy = createdByElement.GetString();
        }
        catch (JsonException)
        {
            // Snapshot legado/corrompido: devolve o que da para mostrar.
        }

        return new ReportTemplateHistoryDto(
            history.Id,
            history.TemplateId,
            history.Version,
            history.ChangeType,
            name,
            datasetType,
            defaultFormat,
            layoutJson,
            filtersJson,
            isActive,
            createdAt,
            createdBy);
    }
}

// ── Biblioteca de templates ───────────────────────────────────
public sealed class ListReportLibraryQueryHandler(IReportTemplateRepository repo)
    : IRequestHandler<ListReportLibraryQuery, Result<IReadOnlyList<ReportLibraryTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<ReportLibraryTemplateDto>>> Handle(ListReportLibraryQuery q, CancellationToken ct)
    {
        var datasetType = q.DatasetType.HasValue && Enum.IsDefined(typeof(ReportDatasetType), q.DatasetType.Value)
            ? (ReportDatasetType)q.DatasetType.Value
            : (ReportDatasetType?)null;

        var templates = await repo.GetBuiltInAsync(datasetType);

        var items = templates
            .Select(t => new ReportLibraryTemplateDto(
                t.Id,
                t.Name,
                t.Description,
                (int)t.DatasetType,
                (int)t.DefaultFormat,
                t.LayoutJson,
                t.FiltersJson,
                t.IsBuiltIn,
                null,
                null,
                t.CreatedAt,
                t.UpdatedAt))
            .ToList();

        return Result<IReadOnlyList<ReportLibraryTemplateDto>>.Success(items);
    }
}

public sealed class InstallReportLibraryTemplateCommandHandler(IReportTemplateRepository repo)
    : IRequestHandler<InstallReportLibraryTemplateCommand, Result<ReportTemplateDto>>
{
    public async Task<Result<ReportTemplateDto>> Handle(InstallReportLibraryTemplateCommand cmd, CancellationToken ct)
    {
        var source = await repo.GetByIdAsync(cmd.TemplateId, null);
        if (source is null || !source.IsBuiltIn)
            return Result<ReportTemplateDto>.Failure(Error.NotFound($"Library template {cmd.TemplateId} not found"));

        // Instalar = clonar como template editavel do cliente (o embutido fica intacto).
        var copy = new ReportTemplate
        {
            Id = Guid.NewGuid(),
            ClientId = cmd.ClientId,
            Name = source.Name,
            Description = source.Description,
            Instructions = source.Instructions,
            ExecutionSchemaJson = source.ExecutionSchemaJson,
            DatasetType = source.DatasetType,
            DefaultFormat = source.DefaultFormat,
            LayoutJson = source.LayoutJson,
            FiltersJson = source.FiltersJson,
            IsActive = true,
            IsBuiltIn = false,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = cmd.CreatedBy,
            UpdatedBy = cmd.CreatedBy
        };

        var created = await repo.CreateAsync(copy);

        return Result<ReportTemplateDto>.Success(new ReportTemplateDto(
            created.Id, created.ClientId, created.Name, created.Description,
            created.Instructions, (int)created.DatasetType, (int)created.DefaultFormat,
            created.IsActive, created.IsBuiltIn, created.Version,
            created.CreatedAt, created.UpdatedAt,
            created.LayoutJson, created.FiltersJson, created.ExecutionSchemaJson, created.CreatedBy, created.UpdatedBy));
    }
}

// ── Helper compartilhado ──────────────────────────────────────
internal static class ReportJoinCompatibilityBuilder
{
    public static (Dictionary<string, List<string>> Compatibility, List<ReportJoinSuggestionDto> Suggestions) Build(
        IReadOnlyList<ReportDatasetCatalogItemDto> items)
    {
        var compatibility = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var suggestions = new List<ReportJoinSuggestionDto>();

        for (var i = 0; i < items.Count; i++)
        {
            var source = items[i];
            var sourceKeys = JoinKeys(source);
            if (sourceKeys.Count == 0)
                continue;

            for (var j = 0; j < items.Count && suggestions.Count < 200; j++)
            {
                if (i == j)
                    continue;

                var target = items[j];
                var common = sourceKeys
                    .Intersect(JoinKeys(target), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (common.Count == 0)
                    continue;

                if (!compatibility.TryGetValue(source.Key, out var targets))
                {
                    targets = [];
                    compatibility[source.Key] = targets;
                }

                if (!targets.Contains(target.Key, StringComparer.OrdinalIgnoreCase))
                    targets.Add(target.Key);

                suggestions.Add(new ReportJoinSuggestionDto(
                    source.Key,
                    target.Key,
                    common,
                    common[0],
                    "left"));
            }
        }

        return (compatibility, suggestions);
    }

    public static string DefaultAliasFor(ReportDatasetCatalogItemDto item)
    {
        var candidate = item.FieldMetadata
            .Select(metadata => metadata.DefaultAlias)
            .FirstOrDefault(alias => !string.IsNullOrWhiteSpace(alias));

        if (string.IsNullOrWhiteSpace(candidate))
            return item.Key[..Math.Min(3, item.Key.Length)].ToLowerInvariant();

        var dotIndex = candidate.IndexOf('.');
        return dotIndex > 0 ? candidate[..dotIndex] : candidate;
    }

    private static List<string> JoinKeys(ReportDatasetCatalogItemDto item)
        => item.FieldMetadata
            .Where(metadata => metadata.IsJoinKey && !string.IsNullOrWhiteSpace(metadata.Field))
            .Select(metadata => metadata.Field)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
