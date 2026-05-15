using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public class DepartmentCustomFieldService : IDepartmentCustomFieldService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryDbContext _db;
    private readonly IDepartmentRepository _departmentRepo;
    private readonly ILogRepository _logRepository;
    private readonly ILogger<DepartmentCustomFieldService> _logger;

    public DepartmentCustomFieldService(
        DiscoveryDbContext db,
        IDepartmentRepository departmentRepo,
        ILogRepository logRepository,
        ILogger<DepartmentCustomFieldService> logger)
    {
        _db = db;
        _departmentRepo = departmentRepo;
        _logRepository = logRepository;
        _logger = logger;
    }

    // ── Definitions CRUD ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsByDepartmentAsync(
        Guid departmentId,
        CancellationToken cancellationToken = default)
    {
        await EnsureDepartmentExistsAsync(departmentId, cancellationToken);

        return await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(d => d.DepartmentId == departmentId)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<CustomFieldDefinition> CreateDepartmentFieldAsync(
        Guid departmentId,
        CreateDepartmentCustomFieldInput input,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        await EnsureDepartmentExistsAsync(departmentId, cancellationToken);
        ValidateInput(input);

        var normalizedName = NormalizeFieldName(input.Name);
        var exists = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .AnyAsync(d => d.DepartmentId == departmentId && d.Name == normalizedName, cancellationToken);

        if (exists)
            throw new InvalidOperationException($"Já existe um campo com o nome '{input.Name}' neste departamento.");

        var now = DateTime.UtcNow;
        var definition = new CustomFieldDefinition
        {
            Id = IdGenerator.NewId(),
            Name = normalizedName,
            Label = input.Label.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            ScopeType = CustomFieldScopeType.Department,
            DataType = input.DataType,
            IsRequired = input.IsRequired,
            IsActive = input.IsActive,
            IsSecret = false,
            IsInternal = input.IsInternal,
            DepartmentId = departmentId,
            OptionsJson = SerializeOptions(input.Options),
            ValidationRegex = string.IsNullOrWhiteSpace(input.ValidationRegex) ? null : input.ValidationRegex.Trim(),
            MinLength = input.MinLength,
            MaxLength = input.MaxLength,
            MinValue = input.MinValue,
            MaxValue = input.MaxValue,
            AllowRuntimeRead = false,
            AllowAgentWrite = false,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.Disabled,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.CustomFieldDefinitions.Add(definition);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Department custom field '{FieldName}' created for department {DepartmentId} by {UpdatedBy}",
            definition.Name, departmentId, updatedBy);

        return definition;
    }

    public async Task<CustomFieldDefinition?> UpdateDepartmentFieldAsync(
        Guid fieldId,
        Guid departmentId,
        UpdateDepartmentCustomFieldInput input,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        await EnsureDepartmentExistsAsync(departmentId, cancellationToken);
        ValidateInput(input);

        var definition = await _db.CustomFieldDefinitions
            .SingleOrDefaultAsync(d => d.Id == fieldId, cancellationToken);

        if (definition is null)
            return null;

        if (definition.DepartmentId != departmentId)
            throw new InvalidOperationException("Este campo não pertence ao departamento informado.");

        var normalizedName = NormalizeFieldName(input.Name);
        var duplicateExists = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .AnyAsync(d => d.Id != fieldId && d.DepartmentId == departmentId && d.Name == normalizedName, cancellationToken);

        if (duplicateExists)
            throw new InvalidOperationException($"Já existe um campo com o nome '{input.Name}' neste departamento.");

        definition.Name = normalizedName;
        definition.Label = input.Label.Trim();
        definition.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        definition.DataType = input.DataType;
        definition.IsRequired = input.IsRequired;
        definition.IsActive = input.IsActive;
        definition.IsInternal = input.IsInternal;
        definition.OptionsJson = SerializeOptions(input.Options);
        definition.ValidationRegex = string.IsNullOrWhiteSpace(input.ValidationRegex) ? null : input.ValidationRegex.Trim();
        definition.MinLength = input.MinLength;
        definition.MaxLength = input.MaxLength;
        definition.MinValue = input.MinValue;
        definition.MaxValue = input.MaxValue;
        definition.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Department custom field '{FieldName}' updated for department {DepartmentId} by {UpdatedBy}",
            definition.Name, departmentId, updatedBy);

        return definition;
    }

    public async Task<bool> DeleteDepartmentFieldAsync(
        Guid fieldId,
        Guid departmentId,
        CancellationToken cancellationToken = default)
    {
        var definition = await _db.CustomFieldDefinitions
            .SingleOrDefaultAsync(d => d.Id == fieldId, cancellationToken);

        if (definition is null)
            return false;

        if (definition.DepartmentId != departmentId)
            throw new InvalidOperationException("Este campo não pertence ao departamento informado.");

        definition.IsActive = false;
        definition.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Department custom field '{FieldName}' deleted for department {DepartmentId}",
            definition.Name, departmentId);

        return true;
    }

    // ── Schema (formulário) ──────────────────────────────────────────────

    public async Task<IReadOnlyList<DepartmentFieldSchemaItemDto>> GetPublicSchemaForDepartmentAsync(
        Guid departmentId,
        CancellationToken cancellationToken = default)
    {
        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(d => d.DepartmentId == departmentId && d.IsActive && !d.IsInternal)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        return definitions.Select(MapToSchemaDto).ToList();
    }

    public async Task<IReadOnlyList<DepartmentFieldSchemaItemDto>> GetFullSchemaForDepartmentAsync(
        Guid departmentId,
        Guid? ticketId,
        CancellationToken cancellationToken = default)
    {
        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(d => d.DepartmentId == departmentId && d.IsActive)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        if (definitions.Count == 0)
            return [];

        // Se tiver ticketId, busca valores já preenchidos
        Dictionary<Guid, string>? valuesByDefinition = null;
        if (ticketId.HasValue)
        {
            var definitionIds = definitions.Select(d => d.Id).ToList();
            var entityKey = ticketId.Value.ToString("D");
            valuesByDefinition = await _db.CustomFieldValues
                .AsNoTracking()
                .Where(v => definitionIds.Contains(v.DefinitionId) && v.EntityKey == entityKey)
                .ToDictionaryAsync(v => v.DefinitionId, v => v.ValueJson, cancellationToken);
        }

        return definitions.Select(d =>
        {
            var dto = MapToSchemaDto(d);
            if (valuesByDefinition != null && valuesByDefinition.TryGetValue(d.Id, out var val))
                dto = dto with { CurrentValueJson = val };
            return dto;
        }).ToList();
    }

    // ── Validação na criação do ticket ────────────────────────────────────

    public async Task<IReadOnlyList<DepartmentFieldValidationError>> ValidateTicketFieldsAsync(
        Guid departmentId,
        IReadOnlyDictionary<Guid, string> fieldValues,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<DepartmentFieldValidationError>();

        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(d => d.DepartmentId == departmentId && d.IsActive && !d.IsInternal)
            .ToListAsync(cancellationToken);

        foreach (var definition in definitions)
        {
            var hasValue = fieldValues.TryGetValue(definition.Id, out var rawValue);

            if (definition.IsRequired && (!hasValue || string.IsNullOrWhiteSpace(rawValue) || rawValue == "null"))
            {
                errors.Add(new DepartmentFieldValidationError(
                    definition.Id,
                    definition.Name,
                    $"O campo '{definition.Label}' é obrigatório."));
                continue;
            }

            if (!hasValue || string.IsNullOrWhiteSpace(rawValue) || rawValue == "null")
                continue; // não obrigatório e vazio, ok

            // Validar o valor
            try
            {
                ValidateFieldValueInternal(definition, rawValue);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new DepartmentFieldValidationError(
                    definition.Id,
                    definition.Name,
                    $"Campo '{definition.Label}': {ex.Message}"));
            }
        }

        return errors;
    }

    // ── Persistência ─────────────────────────────────────────────────────

    public async Task SaveTicketFieldValuesAsync(
        Guid ticketId,
        Guid departmentId,
        IReadOnlyDictionary<Guid, string> fieldValues,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        if (fieldValues.Count == 0)
            return;

        var definitionIds = fieldValues.Keys.ToList();
        var definitions = await _db.CustomFieldDefinitions
            .Where(d => definitionIds.Contains(d.Id) && d.DepartmentId == departmentId)
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var entityKey = ticketId.ToString("D");
        var now = DateTime.UtcNow;

        foreach (var (definitionId, rawValue) in fieldValues)
        {
            if (!definitions.TryGetValue(definitionId, out var definition))
                continue;

            if (!definition.IsActive)
                continue;

            if (string.IsNullOrWhiteSpace(rawValue) || rawValue == "null")
            {
                if (definition.IsRequired)
                    throw new InvalidOperationException($"O campo '{definition.Label}' é obrigatório.");
                continue;
            }

            try
            {
                var valueJson = NormalizeJsonInternal(rawValue);
                ValidateFieldValueInternal(definition, valueJson);

                _db.CustomFieldValues.Add(new CustomFieldValue
                {
                    Id = IdGenerator.NewId(),
                    DefinitionId = definitionId,
                    ScopeType = CustomFieldScopeType.Ticket,
                    EntityId = ticketId,
                    EntityKey = entityKey,
                    ValueJson = valueJson,
                    UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim(),
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            catch (InvalidOperationException)
            {
                // Se falhar validação, loga mas não bloqueia a criação do ticket
                _logger.LogWarning(
                    "Failed to persist department custom field value. DefinitionId={DefinitionId}, TicketId={TicketId}",
                    definitionId, ticketId);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private async Task EnsureDepartmentExistsAsync(Guid departmentId, CancellationToken ct)
    {
        var dept = await _departmentRepo.GetByIdAsync(departmentId);
        if (dept is null)
            throw new InvalidOperationException("Departamento não encontrado.");
    }

    private static void ValidateInput(CreateDepartmentCustomFieldInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new InvalidOperationException("O nome do campo é obrigatório.");

        if (string.IsNullOrWhiteSpace(input.Label))
            throw new InvalidOperationException("O label do campo é obrigatório.");

        if (input.MinLength.HasValue && input.MinLength.Value < 0)
            throw new InvalidOperationException("minLength não pode ser negativo.");

        if (input.MaxLength.HasValue && input.MaxLength.Value < 0)
            throw new InvalidOperationException("maxLength não pode ser negativo.");

        if (input.MinLength.HasValue && input.MaxLength.HasValue && input.MinLength.Value > input.MaxLength.Value)
            throw new InvalidOperationException("minLength não pode ser maior que maxLength.");

        if (input.MinValue.HasValue && input.MaxValue.HasValue && input.MinValue.Value > input.MaxValue.Value)
            throw new InvalidOperationException("minValue não pode ser maior que maxValue.");

        if ((input.DataType == CustomFieldDataType.Dropdown || input.DataType == CustomFieldDataType.ListBox)
            && (input.Options is null || input.Options.Count == 0))
        {
            throw new InvalidOperationException("Campos do tipo Dropdown e ListBox exigem opções.");
        }

        if (input.Options is { Count: > 0 } && input.Options.Any(o => string.IsNullOrWhiteSpace(o)))
            throw new InvalidOperationException("Opções do campo não podem conter valores vazios.");

        if (!string.IsNullOrWhiteSpace(input.ValidationRegex))
        {
            try
            {
                _ = new Regex(input.ValidationRegex.Trim(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InvalidOperationException("ValidationRegex é muito complexo.");
            }
        }
    }

    private static void ValidateInput(UpdateDepartmentCustomFieldInput input) =>
        ValidateInput(new CreateDepartmentCustomFieldInput(
            input.Name, input.Label, input.Description, input.DataType,
            input.IsRequired, input.IsInternal, input.IsActive,
            input.Options, input.ValidationRegex,
            input.MinLength, input.MaxLength, input.MinValue, input.MaxValue));

    private static string NormalizeFieldName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(normalized, "^[a-z0-9_\\-]+$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("O nome do campo só pode conter letras minúsculas, números, underscore e hífen.");

        return normalized;
    }

    private static string? SerializeOptions(IReadOnlyList<string>? options)
    {
        if (options is null || options.Count == 0)
            return null;

        var normalized = options
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static IReadOnlyCollection<string> ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            return [];

        return JsonSerializer.Deserialize<List<string>>(optionsJson, JsonOptions) ?? [];
    }

    private static DepartmentFieldSchemaItemDto MapToSchemaDto(CustomFieldDefinition d)
    {
        return new DepartmentFieldSchemaItemDto(
            d.Id,
            d.Name,
            d.Label,
            d.Description,
            d.DataType,
            d.IsRequired,
            d.IsInternal,
            d.IsActive,
            ParseOptions(d.OptionsJson).ToList(),
            d.ValidationRegex,
            d.MinLength,
            d.MaxLength,
            d.MinValue,
            d.MaxValue,
            null);
    }

    private static string NormalizeJsonInternal(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new InvalidOperationException("O valor do campo não pode ser vazio.");

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("O valor do campo deve ser JSON válido.", ex);
        }
    }

    private static void ValidateFieldValueInternal(CustomFieldDefinition definition, string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        var value = document.RootElement;

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (definition.IsRequired)
                throw new InvalidOperationException("O valor do campo é obrigatório.");
            return;
        }

        switch (definition.DataType)
        {
            case CustomFieldDataType.Text:
                ValidateTextInternal(definition, value);
                break;
            case CustomFieldDataType.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var intVal))
                    throw new InvalidOperationException("O campo espera um valor inteiro.");
                if (definition.MinValue.HasValue && intVal < (double)definition.MinValue.Value)
                    throw new InvalidOperationException($"O valor mínimo permitido é {definition.MinValue}.");
                if (definition.MaxValue.HasValue && intVal > (double)definition.MaxValue.Value)
                    throw new InvalidOperationException($"O valor máximo permitido é {definition.MaxValue}.");
                break;
            case CustomFieldDataType.Decimal:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var decVal))
                    throw new InvalidOperationException("O campo espera um valor decimal.");
                if (definition.MinValue.HasValue && decVal < definition.MinValue.Value)
                    throw new InvalidOperationException($"O valor mínimo permitido é {definition.MinValue}.");
                if (definition.MaxValue.HasValue && decVal > definition.MaxValue.Value)
                    throw new InvalidOperationException($"O valor máximo permitido é {definition.MaxValue}.");
                break;
            case CustomFieldDataType.Boolean:
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException("O campo espera um valor booleano (true/false).");
                break;
            case CustomFieldDataType.Date:
            case CustomFieldDataType.DateTime:
                if (value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("O campo espera uma data em formato string.");
                var strVal = value.GetString();
                if (!DateTime.TryParse(strVal, out _))
                    throw new InvalidOperationException("O valor informado não é uma data válida.");
                break;
            case CustomFieldDataType.Dropdown:
                ValidateDropdownInternal(definition, value);
                break;
            case CustomFieldDataType.ListBox:
                ValidateListBoxInternal(definition, value);
                break;
            default:
                throw new InvalidOperationException("Tipo de campo não suportado.");
        }
    }

    private static void ValidateTextInternal(CustomFieldDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("O campo espera um valor de texto.");

        var stringValue = value.GetString() ?? string.Empty;
        if (definition.MinLength.HasValue && stringValue.Length < definition.MinLength.Value)
            throw new InvalidOperationException($"O texto deve ter no mínimo {definition.MinLength} caracteres.");

        if (definition.MaxLength.HasValue && stringValue.Length > definition.MaxLength.Value)
            throw new InvalidOperationException($"O texto deve ter no máximo {definition.MaxLength} caracteres.");

        if (!string.IsNullOrWhiteSpace(definition.ValidationRegex))
        {
            try
            {
                if (!Regex.IsMatch(stringValue, definition.ValidationRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    throw new InvalidOperationException("O valor não corresponde ao formato esperado.");
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InvalidOperationException("Timeout na validação do formato.");
            }
        }
    }

    private static void ValidateDropdownInternal(CustomFieldDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Dropdown espera um valor de texto.");

        var stringValue = value.GetString() ?? string.Empty;
        var options = ParseOptions(definition.OptionsJson);
        if (!options.Contains(stringValue, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("O valor selecionado não é uma opção válida.");
    }

    private static void ValidateListBoxInternal(CustomFieldDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ListBox espera uma lista de valores.");

        var options = ParseOptions(definition.OptionsJson);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Itens do ListBox devem ser texto.");

            var option = item.GetString() ?? string.Empty;
            if (!options.Contains(option, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Um ou mais valores selecionados não são opções válidas.");
        }
    }
}
