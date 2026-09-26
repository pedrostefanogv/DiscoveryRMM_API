using System.Globalization;
using System.Text;
using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <inheritdoc />
public class TicketSubmissionService(
    DiscoveryDbContext db,
    IDepartmentCustomFieldService departmentCustomFieldService) : ITicketSubmissionService
{
    public async Task<TicketSubmissionResult> PrepareAsync(
        TicketSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        TicketTemplate? template = null;
        if (request.TemplateId.HasValue)
        {
            template = await db.TicketTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == request.TemplateId.Value && t.IsActive, cancellationToken);

            // TemplateId informado e inexistente/inativo NÃO é silencioso: sem
            // isso o agent/portais acham que o template foi aplicado.
            if (template is null)
            {
                return new TicketSubmissionResult(
                    request.DepartmentId,
                    request.Title ?? string.Empty,
                    request.Description ?? string.Empty,
                    request.Category,
                    request.Priority,
                    new Dictionary<Guid, string>(),
                    new[] { new DepartmentFieldValidationError(
                        request.TemplateId.Value, "TemplateId", "Template de chamado não encontrado ou inativo.") },
                    null);
            }
        }

        var title = string.IsNullOrWhiteSpace(request.Title) ? template?.Title ?? string.Empty : request.Title!.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description)
            ? template?.Description ?? string.Empty
            : request.Description!;
        var category = string.IsNullOrWhiteSpace(request.Category) ? template?.Category : request.Category!.Trim();
        var priority = string.IsNullOrWhiteSpace(request.Priority)
            ? template?.Priority?.ToString()
            : request.Priority!.Trim();

        var departmentId = request.DepartmentId ?? template?.DepartmentId;

        // Mescla valores: template primeiro, valor informado vence.
        var merged = new Dictionary<Guid, JsonElement>();
        foreach (var (definitionId, value) in ParseTemplateDefaults(template?.CustomFieldDefaultsJson))
            merged[definitionId] = value;
        if (request.CustomFieldValues is not null)
            foreach (var (definitionId, value) in request.CustomFieldValues)
                merged[definitionId] = value;

        if (merged.Count == 0 && template is null)
        {
            return new TicketSubmissionResult(
                departmentId, title, description, category, priority,
                new Dictionary<Guid, string>(), Array.Empty<DepartmentFieldValidationError>(), null);
        }

        if (merged.Count > 0 && departmentId is null)
        {
            return new TicketSubmissionResult(
                departmentId, title, description, category, priority,
                new Dictionary<Guid, string>(),
                new[] { new DepartmentFieldValidationError(Guid.Empty, "DepartmentId", "Selecione um departamento para usar campos personalizados.") },
                null);
        }

        var rawValues = new Dictionary<Guid, string>();
        var errors = new List<DepartmentFieldValidationError>();
        Dictionary<Guid, CustomFieldDefinition> definitions = new();

        if (departmentId.HasValue)
        {
            definitions = await db.CustomFieldDefinitions
                .AsNoTracking()
                .Where(d => d.DepartmentId == departmentId.Value && d.IsActive && !d.IsInternal)
                .ToDictionaryAsync(d => d.Id, cancellationToken);

            foreach (var (definitionId, value) in merged)
            {
                if (!definitions.ContainsKey(definitionId))
                {
                    errors.Add(new DepartmentFieldValidationError(
                        definitionId, definitionId.ToString("D"),
                        "O campo personalizado informado não pertence ao departamento."));
                    continue;
                }
                rawValues[definitionId] = value.GetRawText();
            }

            if (errors.Count == 0)
            {
                var validationErrors = await departmentCustomFieldService.ValidateTicketFieldsAsync(
                    departmentId.Value, rawValues, cancellationToken);
                errors.AddRange(validationErrors);
            }
        }

        if (errors.Count > 0)
        {
            return new TicketSubmissionResult(
                departmentId, title, description, category, priority,
                rawValues, errors, null);
        }

        var snapshot = await BuildSnapshot(template, departmentId, definitions, rawValues, priority, category, title);

        return new TicketSubmissionResult(
            departmentId, title, description, category, priority, rawValues, errors, snapshot);
    }

    private static IEnumerable<(Guid DefinitionId, JsonElement Value)> ParseTemplateDefaults(string? defaultsJson)
    {
        if (string.IsNullOrWhiteSpace(defaultsJson)) yield break;

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(defaultsJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) yield break;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (Guid.TryParse(property.Name, out var definitionId))
                    yield return (definitionId, property.Value.Clone());
            }
        }
    }

    private async Task<string?> BuildSnapshot(
        TicketTemplate? template,
        Guid? departmentId,
        IReadOnlyDictionary<Guid, CustomFieldDefinition> definitions,
        IReadOnlyDictionary<Guid, string> rawValues,
        string? priority,
        string? category,
        string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine("### Formulário do chamado");
        builder.AppendLine();

        if (template is not null)
            builder.AppendLine($"- **Template:** {Escape(template.Name)}");
        if (!string.IsNullOrWhiteSpace(title))
            builder.AppendLine($"- **Título enviado:** {Escape(title)}");
        if (!string.IsNullOrWhiteSpace(priority))
            builder.AppendLine($"- **Prioridade:** {Escape(priority)}");
        if (!string.IsNullOrWhiteSpace(category))
            builder.AppendLine($"- **Categoria:** {Escape(category)}");

        if (departmentId.HasValue)
        {
            var departmentName = await db.Departments
                .AsNoTracking()
                .Where(d => d.Id == departmentId.Value)
                .Select(d => d.Name)
                .FirstOrDefaultAsync();
            if (!string.IsNullOrWhiteSpace(departmentName))
                builder.AppendLine($"- **Departamento:** {Escape(departmentName)}");
        }

        builder.AppendLine($"- **Enviado em:** {DateTime.UtcNow:dd/MM/yyyy HH:mm} (UTC)");

        var rows = new List<(string Label, string Value)>();
        foreach (var (definitionId, rawValue) in rawValues)
        {
            if (!definitions.TryGetValue(definitionId, out var definition)) continue;
            if (definition.IsSecret) continue;

            var formatted = FormatValue(definition, rawValue);
            if (string.IsNullOrWhiteSpace(formatted)) continue;
            rows.Add((definition.Label, formatted));
        }

        if (rows.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("| Campo | Valor |");
            builder.AppendLine("| --- | --- |");
            foreach (var (label, value) in rows)
                builder.AppendLine($"| {Escape(label)} | {Escape(value)} |");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatValue(CustomFieldDefinition definition, string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var value = document.RootElement;
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return string.Empty;
                case JsonValueKind.True:
                    return "Sim";
                case JsonValueKind.False:
                    return "Não";
                case JsonValueKind.Array:
                    return string.Join(", ", value.EnumerateArray().Select(item => item.ToString()));
                case JsonValueKind.String:
                    return value.GetString() ?? string.Empty;
                default:
                    return value.ToString();
            }
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }

    private static string Escape(string value) =>
        value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
}
