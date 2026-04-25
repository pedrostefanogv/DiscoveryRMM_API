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

public class CustomFieldService : ICustomFieldService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryDbContext _db;
    private readonly IAgentRepository _agentRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentAutoLabelingService _autoLabelingService;
    private readonly ILogger<CustomFieldService> _logger;

    public CustomFieldService(
        DiscoveryDbContext db,
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        IAgentAutoLabelingService autoLabelingService,
        ILogger<CustomFieldService> logger)
    {
        _db = db;
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _autoLabelingService = autoLabelingService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsAsync(
        CustomFieldScopeType? scopeType,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = _db.CustomFieldDefinitions.AsNoTracking().AsQueryable();

        if (scopeType.HasValue)
            query = query.Where(definition => definition.ScopeType == scopeType.Value);

        if (!includeInactive)
            query = query.Where(definition => definition.IsActive);

        return await query
            .OrderBy(definition => definition.ScopeType)
            .ThenBy(definition => definition.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<CustomFieldDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.CustomFieldDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(definition => definition.Id == id, cancellationToken);
    }

    public async Task<CustomFieldDefinition> CreateDefinitionAsync(
        UpsertCustomFieldDefinitionInput input,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        _ = updatedBy;
        ValidateDefinitionInput(input);

        var normalizedName = NormalizeFieldName(input.Name);
        var existing = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                definition => definition.ScopeType == input.ScopeType && definition.Name == normalizedName,
                cancellationToken);

        if (existing is not null)
            throw new InvalidOperationException("A custom field with the same name already exists for this scope.");

        var now = DateTime.UtcNow;
        var definition = new CustomFieldDefinition
        {
            Id = IdGenerator.NewId(),
            Name = normalizedName,
            Label = input.Label.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            ScopeType = input.ScopeType,
            DataType = input.DataType,
            IsRequired = input.IsRequired,
            IsActive = input.IsActive,
            IsSecret = input.IsSecret,
            OptionsJson = SerializeOptions(input.Options),
            ValidationRegex = string.IsNullOrWhiteSpace(input.ValidationRegex) ? null : input.ValidationRegex.Trim(),
            MinLength = input.MinLength,
            MaxLength = input.MaxLength,
            MinValue = input.MinValue,
            MaxValue = input.MaxValue,
            AllowRuntimeRead = input.AllowRuntimeRead,
            AllowAgentWrite = input.AllowAgentWrite,
            RuntimeAccessMode = input.RuntimeAccessMode,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.CustomFieldDefinitions.Add(definition);
        await _db.SaveChangesAsync(cancellationToken);

        await ReplaceExecutionAccessAsync(definition.Id, input.AccessBindings, cancellationToken);
        return definition;
    }

    public async Task<CustomFieldDefinition?> UpdateDefinitionAsync(
        Guid id,
        UpsertCustomFieldDefinitionInput input,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        _ = updatedBy;
        ValidateDefinitionInput(input);

        var definition = await _db.CustomFieldDefinitions
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (definition is null)
            return null;

        var normalizedName = NormalizeFieldName(input.Name);
        var duplicateExists = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .AnyAsync(
                item => item.Id != id && item.ScopeType == input.ScopeType && item.Name == normalizedName,
                cancellationToken);

        if (duplicateExists)
            throw new InvalidOperationException("A custom field with the same name already exists for this scope.");

        definition.Name = normalizedName;
        definition.Label = input.Label.Trim();
        definition.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        definition.ScopeType = input.ScopeType;
        definition.DataType = input.DataType;
        definition.IsRequired = input.IsRequired;
        definition.IsActive = input.IsActive;
        definition.IsSecret = input.IsSecret;
        definition.OptionsJson = SerializeOptions(input.Options);
        definition.ValidationRegex = string.IsNullOrWhiteSpace(input.ValidationRegex) ? null : input.ValidationRegex.Trim();
        definition.MinLength = input.MinLength;
        definition.MaxLength = input.MaxLength;
        definition.MinValue = input.MinValue;
        definition.MaxValue = input.MaxValue;
        definition.AllowRuntimeRead = input.AllowRuntimeRead;
        definition.AllowAgentWrite = input.AllowAgentWrite;
        definition.RuntimeAccessMode = input.RuntimeAccessMode;
        definition.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await ReplaceExecutionAccessAsync(definition.Id, input.AccessBindings, cancellationToken);

        return definition;
    }

    public async Task<bool> DeactivateDefinitionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await _db.CustomFieldDefinitions
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (definition is null)
            return false;

        definition.IsActive = false;
        definition.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<IReadOnlyList<CustomFieldResolvedValueDto>> GetValuesAsync(
        CustomFieldScopeType scopeType,
        Guid? entityId,
        bool includeSecrets = true,
        CancellationToken cancellationToken = default)
    {
        ValidateScopeEntity(scopeType, entityId);

        var entityKey = BuildEntityKey(scopeType, entityId);
        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(definition => definition.ScopeType == scopeType && definition.IsActive)
            .OrderBy(definition => definition.Name)
            .ToListAsync(cancellationToken);

        var definitionIds = definitions.Select(definition => definition.Id).ToList();
        var values = await _db.CustomFieldValues
            .AsNoTracking()
            .Where(value => definitionIds.Contains(value.DefinitionId) && value.EntityKey == entityKey)
            .ToDictionaryAsync(value => value.DefinitionId, cancellationToken);

        var result = new List<CustomFieldResolvedValueDto>(definitions.Count);
        foreach (var definition in definitions)
        {
            if (!values.TryGetValue(definition.Id, out var value))
            {
                result.Add(new CustomFieldResolvedValueDto(
                    definition.Id,
                    definition.Name,
                    definition.Label,
                    scopeType,
                    entityId,
                    "null",
                    definition.UpdatedAt,
                    definition.IsSecret));
                continue;
            }

            var outputJson = includeSecrets || !definition.IsSecret ? value.ValueJson : JsonSerializer.Serialize("***", JsonOptions);
            result.Add(new CustomFieldResolvedValueDto(
                definition.Id,
                definition.Name,
                definition.Label,
                scopeType,
                entityId,
                outputJson,
                value.UpdatedAt,
                definition.IsSecret));
        }

        return result;
    }

    public async Task<IReadOnlyList<CustomFieldSchemaItemDto>> GetSchemaAsync(
        CustomFieldScopeType scopeType,
        Guid? entityId,
        bool includeInactive = false,
        bool includeSecrets = true,
        CancellationToken cancellationToken = default)
    {
        ValidateScopeEntity(scopeType, entityId);

        var entityKey = BuildEntityKey(scopeType, entityId);
        var definitionsQuery = _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(definition => definition.ScopeType == scopeType);

        if (!includeInactive)
            definitionsQuery = definitionsQuery.Where(definition => definition.IsActive);

        var definitions = await definitionsQuery
            .OrderBy(definition => definition.Name)
            .ToListAsync(cancellationToken);

        if (definitions.Count == 0)
            return [];

        var definitionIds = definitions.Select(definition => definition.Id).ToList();
        var values = await _db.CustomFieldValues
            .AsNoTracking()
            .Where(value => definitionIds.Contains(value.DefinitionId) && value.EntityKey == entityKey)
            .ToDictionaryAsync(value => value.DefinitionId, cancellationToken);

        var accessBindings = await _db.CustomFieldExecutionAccesses
            .AsNoTracking()
            .Where(binding => definitionIds.Contains(binding.DefinitionId))
            .OrderBy(binding => binding.TaskId)
            .ThenBy(binding => binding.ScriptId)
            .ToListAsync(cancellationToken);

        var bindingsByDefinition = accessBindings
            .GroupBy(binding => binding.DefinitionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CustomFieldAccessBindingDto>)group
                    .Select(binding => new CustomFieldAccessBindingDto(
                        binding.TaskId,
                        binding.ScriptId,
                        binding.CanRead,
                        binding.CanWrite))
                    .ToList());

        return definitions.Select(definition =>
        {
            values.TryGetValue(definition.Id, out var value);
            var outputJson = value is null
                ? "null"
                : includeSecrets || !definition.IsSecret
                    ? value.ValueJson
                    : JsonSerializer.Serialize("***", JsonOptions);

            return new CustomFieldSchemaItemDto(
                definition.Id,
                definition.Name,
                definition.Label,
                definition.Description,
                definition.ScopeType,
                definition.DataType,
                definition.IsRequired,
                definition.IsActive,
                definition.IsSecret,
                ParseOptions(definition.OptionsJson).ToList(),
                definition.ValidationRegex,
                definition.MinLength,
                definition.MaxLength,
                definition.MinValue,
                definition.MaxValue,
                definition.AllowRuntimeRead,
                definition.AllowAgentWrite,
                definition.RuntimeAccessMode,
                bindingsByDefinition.GetValueOrDefault(definition.Id, []),
                entityId,
                outputJson,
                value?.UpdatedAt);
        }).ToList();
    }

    public async Task<CustomFieldResolvedValueDto> UpsertValueAsync(
        UpsertCustomFieldValueInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateScopeEntity(input.ScopeType, input.EntityId);

        var definition = await _db.CustomFieldDefinitions
            .SingleOrDefaultAsync(item => item.Id == input.DefinitionId, cancellationToken)
            ?? throw new InvalidOperationException("Custom field definition was not found.");

        if (!definition.IsActive)
            throw new InvalidOperationException("Custom field definition is not active.");

        if (definition.ScopeType != input.ScopeType)
            throw new InvalidOperationException("Custom field scope does not match the definition scope.");

        var valueJson = NormalizeJson(input.ValueJson);
        ValidateFieldValue(definition, valueJson);

        var entityKey = BuildEntityKey(input.ScopeType, input.EntityId);
        var existing = await _db.CustomFieldValues
            .SingleOrDefaultAsync(
                value => value.DefinitionId == input.DefinitionId && value.EntityKey == entityKey,
                cancellationToken);

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            existing = new CustomFieldValue
            {
                Id = IdGenerator.NewId(),
                DefinitionId = input.DefinitionId,
                ScopeType = input.ScopeType,
                EntityId = input.EntityId,
                EntityKey = entityKey,
                ValueJson = valueJson,
                UpdatedBy = string.IsNullOrWhiteSpace(input.UpdatedBy) ? null : input.UpdatedBy.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.CustomFieldValues.Add(existing);
        }
        else
        {
            existing.ScopeType = input.ScopeType;
            existing.EntityId = input.EntityId;
            existing.ValueJson = valueJson;
            existing.UpdatedBy = string.IsNullOrWhiteSpace(input.UpdatedBy) ? null : input.UpdatedBy.Trim();
            existing.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _ = TriggerLabelRevaluationAsync(input.ScopeType, input.EntityId);

        return new CustomFieldResolvedValueDto(
            definition.Id,
            definition.Name,
            definition.Label,
            input.ScopeType,
            input.EntityId,
            definition.IsSecret ? JsonSerializer.Serialize("***", JsonOptions) : existing.ValueJson,
            existing.UpdatedAt,
            definition.IsSecret);
    }

    public async Task<IReadOnlyList<RuntimeCustomFieldDto>> GetRuntimeValuesForAgentAsync(
        Guid agentId,
        Guid? taskId,
        Guid? scriptId,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException("Agent not found.");

        var site = await _siteRepository.GetByIdAsync(agent.SiteId)
            ?? throw new InvalidOperationException("Site not found for agent.");

        var definitions = await _db.CustomFieldDefinitions
            .AsNoTracking()
            .Where(definition =>
                definition.IsActive
                && definition.AllowRuntimeRead
                && definition.RuntimeAccessMode != CustomFieldRuntimeAccessMode.Disabled)
            .Where(definition =>
                definition.ScopeType == CustomFieldScopeType.Server
                || definition.ScopeType == CustomFieldScopeType.Client
                || definition.ScopeType == CustomFieldScopeType.Site
                || definition.ScopeType == CustomFieldScopeType.Agent)
            .OrderBy(definition => definition.ScopeType)
            .ThenBy(definition => definition.Name)
            .ToListAsync(cancellationToken);

        var valuesByKey = await BuildRuntimeValueMapAsync(definitions.Select(definition => definition.Id).ToList(), agentId, site.Id, site.ClientId, cancellationToken);

        var result = new List<RuntimeCustomFieldDto>();
        foreach (var definition in definitions)
        {
            if (!await HasExecutionAccessAsync(definition, taskId, scriptId, requireWrite: false, cancellationToken))
                continue;

            var entityKey = ResolveRuntimeEntityKey(definition.ScopeType, agentId, site.Id, site.ClientId);
            valuesByKey.TryGetValue((definition.Id, entityKey), out var valueJson);

            result.Add(new RuntimeCustomFieldDto(
                definition.Id,
                definition.Name,
                definition.Label,
                definition.ScopeType,
                valueJson ?? "null",
                definition.IsSecret));
        }

        return result;
    }

    public async Task<CustomFieldResolvedValueDto> UpsertAgentCollectedValueAsync(
        Guid agentId,
        AgentCustomFieldCollectedValueInput input,
        CancellationToken cancellationToken = default)
    {
        var definition = await ResolveAgentDefinitionAsync(input.DefinitionId, input.Name, cancellationToken);
        if (!definition.AllowAgentWrite)
            throw new InvalidOperationException("This custom field does not allow writes from agents.");

        if (!await HasExecutionAccessAsync(definition, input.TaskId, input.ScriptId, requireWrite: true, cancellationToken))
            throw new InvalidOperationException("Agent is not allowed to write this custom field in the current execution context.");

        var updatedBy = string.IsNullOrWhiteSpace(input.UpdatedBy) ? "agent" : input.UpdatedBy.Trim();
        return await UpsertValueAsync(
            new UpsertCustomFieldValueInput(
                definition.Id,
                CustomFieldScopeType.Agent,
                agentId,
                NormalizeJson(input.ValueJson),
                updatedBy),
            cancellationToken);
    }

    private async Task<CustomFieldDefinition> ResolveAgentDefinitionAsync(Guid? definitionId, string? name, CancellationToken cancellationToken)
    {
        CustomFieldDefinition? definition = null;

        if (definitionId.HasValue)
        {
            definition = await _db.CustomFieldDefinitions
                .SingleOrDefaultAsync(item => item.Id == definitionId.Value, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            var normalizedName = NormalizeFieldName(name);
            definition = await _db.CustomFieldDefinitions
                .SingleOrDefaultAsync(
                    item => item.ScopeType == CustomFieldScopeType.Agent && item.Name == normalizedName,
                    cancellationToken);
        }

        if (definition is null)
            throw new InvalidOperationException("Custom field definition was not found.");

        if (!definition.IsActive)
            throw new InvalidOperationException("Custom field definition is not active.");

        if (definition.ScopeType != CustomFieldScopeType.Agent)
            throw new InvalidOperationException("Only agent scoped custom fields can be collected by an agent.");

        return definition;
    }

    private async Task ReplaceExecutionAccessAsync(
        Guid definitionId,
        IReadOnlyList<CustomFieldAccessBindingInput>? bindings,
        CancellationToken cancellationToken)
    {
        await _db.CustomFieldExecutionAccesses
            .Where(item => item.DefinitionId == definitionId)
            .ExecuteDeleteAsync(cancellationToken);

        if (bindings is null || bindings.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var normalized = bindings
            .Where(binding => binding.TaskId.HasValue || binding.ScriptId.HasValue)
            .Select(binding => new
            {
                binding.TaskId,
                binding.ScriptId,
                binding.CanRead,
                binding.CanWrite
            })
            .Distinct()
            .ToList();

        foreach (var binding in normalized)
        {
            _db.CustomFieldExecutionAccesses.Add(new CustomFieldExecutionAccess
            {
                Id = IdGenerator.NewId(),
                DefinitionId = definitionId,
                TaskId = binding.TaskId,
                ScriptId = binding.ScriptId,
                CanRead = binding.CanRead,
                CanWrite = binding.CanWrite,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<(Guid DefinitionId, string EntityKey), string>> BuildRuntimeValueMapAsync(
        IReadOnlyCollection<Guid> definitionIds,
        Guid agentId,
        Guid siteId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        var serverKey = BuildEntityKey(CustomFieldScopeType.Server, null);
        var clientKey = BuildEntityKey(CustomFieldScopeType.Client, clientId);
        var siteKey = BuildEntityKey(CustomFieldScopeType.Site, siteId);
        var agentKey = BuildEntityKey(CustomFieldScopeType.Agent, agentId);

        var validKeys = new[] { serverKey, clientKey, siteKey, agentKey };

        return await _db.CustomFieldValues
            .AsNoTracking()
            .Where(value => definitionIds.Contains(value.DefinitionId) && validKeys.Contains(value.EntityKey))
            .ToDictionaryAsync(
                value => (value.DefinitionId, value.EntityKey),
                value => value.ValueJson,
                cancellationToken);
    }

    private async Task<bool> HasExecutionAccessAsync(
        CustomFieldDefinition definition,
        Guid? taskId,
        Guid? scriptId,
        bool requireWrite,
        CancellationToken cancellationToken)
    {
        if (definition.RuntimeAccessMode == CustomFieldRuntimeAccessMode.Public)
            return true;

        if (definition.RuntimeAccessMode == CustomFieldRuntimeAccessMode.Disabled)
            return false;

        if (!taskId.HasValue && !scriptId.HasValue)
            return false;

        var query = _db.CustomFieldExecutionAccesses
            .AsNoTracking()
            .Where(item => item.DefinitionId == definition.Id)
            .Where(item =>
                (taskId.HasValue && item.TaskId == taskId.Value)
                || (scriptId.HasValue && item.ScriptId == scriptId.Value));

        if (requireWrite)
            query = query.Where(item => item.CanWrite);
        else
            query = query.Where(item => item.CanRead);

        return await query.AnyAsync(cancellationToken);
    }

    private static string ResolveRuntimeEntityKey(CustomFieldScopeType scopeType, Guid agentId, Guid siteId, Guid clientId)
    {
        return scopeType switch
        {
            CustomFieldScopeType.Server => BuildEntityKey(CustomFieldScopeType.Server, null),
            CustomFieldScopeType.Client => BuildEntityKey(CustomFieldScopeType.Client, clientId),
            CustomFieldScopeType.Site => BuildEntityKey(CustomFieldScopeType.Site, siteId),
            CustomFieldScopeType.Agent => BuildEntityKey(CustomFieldScopeType.Agent, agentId),
            _ => throw new InvalidOperationException("Invalid custom field scope type.")
        };
    }

    private static void ValidateScopeEntity(CustomFieldScopeType scopeType, Guid? entityId)
    {
        if (scopeType == CustomFieldScopeType.Server)
        {
            if (entityId.HasValue)
                throw new InvalidOperationException("Server scoped custom field values do not accept entityId.");

            return;
        }

        if (!entityId.HasValue)
            throw new InvalidOperationException("entityId is required for client, site and agent scopes.");
    }

    private async Task TriggerLabelRevaluationAsync(CustomFieldScopeType scopeType, Guid? entityId)
    {
        if (!await _autoLabelingService.HasEnabledRulesAsync())
            return;

        try
        {
            switch (scopeType)
            {
                case CustomFieldScopeType.Agent when entityId.HasValue:
                    await _autoLabelingService.EvaluateAgentAsync(
                        entityId.Value,
                        $"custom-field-value-updated:agent:{entityId.Value}");
                    break;

                case CustomFieldScopeType.Site when entityId.HasValue:
                {
                    var agents = await _agentRepository.GetBySiteIdAsync(entityId.Value);
                    foreach (var agent in agents)
                        await _autoLabelingService.EvaluateAgentAsync(
                            agent.Id,
                            $"custom-field-value-updated:site:{entityId.Value}");
                    break;
                }

                case CustomFieldScopeType.Client when entityId.HasValue:
                {
                    var agents = await _agentRepository.GetByClientIdAsync(entityId.Value);
                    foreach (var agent in agents)
                        await _autoLabelingService.EvaluateAgentAsync(
                            agent.Id,
                            $"custom-field-value-updated:client:{entityId.Value}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background label re-evaluation failed after custom field value upsert (scope={Scope}, entityId={EntityId}).", LogSanitizer.Sanitize(scopeType.ToString()), LogSanitizer.Sanitize(entityId?.ToString()));
        }
    }

    private static string BuildEntityKey(CustomFieldScopeType scopeType, Guid? entityId)
    {
        return scopeType == CustomFieldScopeType.Server
            ? "server"
            : entityId!.Value.ToString("D");
    }

    private static void ValidateDefinitionInput(UpsertCustomFieldDefinitionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new InvalidOperationException("Field name is required.");

        if (string.IsNullOrWhiteSpace(input.Label))
            throw new InvalidOperationException("Field label is required.");

        if (input.MinLength.HasValue && input.MinLength.Value < 0)
            throw new InvalidOperationException("minLength cannot be negative.");

        if (input.MaxLength.HasValue && input.MaxLength.Value < 0)
            throw new InvalidOperationException("maxLength cannot be negative.");

        if (input.MinLength.HasValue && input.MaxLength.HasValue && input.MinLength.Value > input.MaxLength.Value)
            throw new InvalidOperationException("minLength cannot be greater than maxLength.");

        if (input.MinValue.HasValue && input.MaxValue.HasValue && input.MinValue.Value > input.MaxValue.Value)
            throw new InvalidOperationException("minValue cannot be greater than maxValue.");

        if (input.RuntimeAccessMode == CustomFieldRuntimeAccessMode.RestrictedTaskScript)
        {
            var hasBindings = input.AccessBindings is { Count: > 0 }
                && input.AccessBindings.Any(binding => binding.TaskId.HasValue || binding.ScriptId.HasValue);
            if (!hasBindings)
                throw new InvalidOperationException("RestrictedTaskScript mode requires at least one task/script binding.");
        }

        if (input.AllowAgentWrite && input.ScopeType != CustomFieldScopeType.Agent)
            throw new InvalidOperationException("Only agent scoped custom fields can enable allowAgentWrite.");

        if ((input.DataType == CustomFieldDataType.Dropdown || input.DataType == CustomFieldDataType.ListBox)
            && (input.Options is null || input.Options.Count == 0))
        {
            throw new InvalidOperationException("Dropdown and ListBox custom fields require options.");
        }

        if (input.Options is { Count: > 0 } && input.Options.Any(option => string.IsNullOrWhiteSpace(option)))
            throw new InvalidOperationException("Custom field options cannot contain empty values.");

        if (!string.IsNullOrWhiteSpace(input.ValidationRegex))
        {
            try
            {
                _ = new Regex(input.ValidationRegex.Trim(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InvalidOperationException("ValidationRegex is too complex and timed out during validation.");
            }
        }
    }

    private static string NormalizeFieldName(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(normalized, "^[a-z0-9_\\-]+$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Field name can only contain lowercase letters, numbers, underscore and hyphen.");

        return normalized;
    }

    private static string? SerializeOptions(IReadOnlyList<string>? options)
    {
        if (options is null || options.Count == 0)
            return null;

        var normalized = options
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Select(option => option.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static IReadOnlyCollection<string> ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            return [];

        return JsonSerializer.Deserialize<List<string>>(optionsJson, JsonOptions) ?? [];
    }

    private static string NormalizeJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            throw new InvalidOperationException("ValueJson is required.");

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return document.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ValueJson must be valid JSON.", ex);
        }
    }

    private static void ValidateFieldValue(CustomFieldDefinition definition, string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        var value = document.RootElement;

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (definition.IsRequired)
                throw new InvalidOperationException("Custom field value is required.");

            return;
        }

        switch (definition.DataType)
        {
            case CustomFieldDataType.Text:
                ValidateText(definition, value);
                break;
            case CustomFieldDataType.Integer:
                ValidateInteger(definition, value);
                break;
            case CustomFieldDataType.Decimal:
                ValidateDecimal(definition, value);
                break;
            case CustomFieldDataType.Boolean:
                if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                    throw new InvalidOperationException("Custom field expects a boolean value.");
                break;
            case CustomFieldDataType.Date:
                ValidateDate(definition, value, requireDateOnly: true);
                break;
            case CustomFieldDataType.DateTime:
                ValidateDate(definition, value, requireDateOnly: false);
                break;
            case CustomFieldDataType.Dropdown:
                ValidateDropdown(definition, value);
                break;
            case CustomFieldDataType.ListBox:
                ValidateListBox(definition, value);
                break;
            default:
                throw new InvalidOperationException("Unsupported custom field data type.");
        }
    }

    private static void ValidateText(CustomFieldDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Custom field expects a string value.");

        var stringValue = value.GetString() ?? string.Empty;
        if (definition.MinLength.HasValue && stringValue.Length < definition.MinLength.Value)
            throw new InvalidOperationException("Custom field value is shorter than minLength.");

        if (definition.MaxLength.HasValue && stringValue.Length > definition.MaxLength.Value)
            throw new InvalidOperationException("Custom field value is longer than maxLength.");

        if (!string.IsNullOrWhiteSpace(definition.ValidationRegex))
        {
            try
            {
                if (!Regex.IsMatch(stringValue, definition.ValidationRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    throw new InvalidOperationException("Custom field value does not match validation regex.");
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InvalidOperationException("Custom field validation regex timed out. The value could not be validated.");
            }
        }
    }

    private static void ValidateInteger(CustomFieldDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new InvalidOperationException("Custom field expects an integer value.");

        if (definition.MinValue.HasValue && number < (double)definition.MinValue.Value)
            throw new InvalidOperationException("Custom field value is lower than minValue.");

        if (definition.MaxValue.HasValue && number > (double)definition.MaxValue.Value)
            throw new InvalidOperationException("Custom field value is greater than maxValue.");
    }

    private static void ValidateDecimal(CustomFieldDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            throw new InvalidOperationException("Custom field expects a decimal value.");

        if (definition.MinValue.HasValue && number < definition.MinValue.Value)
            throw new InvalidOperationException("Custom field value is lower than minValue.");

        if (definition.MaxValue.HasValue && number > definition.MaxValue.Value)
            throw new InvalidOperationException("Custom field value is greater than maxValue.");
    }

    private static void ValidateDate(CustomFieldDefinition definition, JsonElement value, bool requireDateOnly)
    {
        _ = definition;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Custom field expects a date string value.");

        var stringValue = value.GetString();
        if (!DateTime.TryParse(stringValue, out var parsed))
            throw new InvalidOperationException("Custom field contains an invalid date value.");

        if (requireDateOnly && parsed.TimeOfDay != TimeSpan.Zero)
            throw new InvalidOperationException("Custom field expects a date-only value.");
    }

    private static void ValidateDropdown(CustomFieldDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Dropdown custom field expects a string value.");

        var stringValue = value.GetString() ?? string.Empty;
        var options = ParseOptions(definition.OptionsJson);
        if (!options.Contains(stringValue, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Custom field value is not a valid dropdown option.");
    }

    private static void ValidateListBox(CustomFieldDefinition definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ListBox custom field expects an array of strings.");

        var options = ParseOptions(definition.OptionsJson);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("ListBox custom field array items must be strings.");

            var option = item.GetString() ?? string.Empty;
            if (!options.Contains(option, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Custom field value contains an invalid list option.");
        }
    }
}
