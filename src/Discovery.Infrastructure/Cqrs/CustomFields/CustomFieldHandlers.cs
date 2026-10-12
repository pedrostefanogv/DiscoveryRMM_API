using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.CustomFields.Commands;
using Discovery.Core.Cqrs.CustomFields.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.CustomFields;

public sealed class ListCustomFieldsQueryHandler(ICustomFieldService svc, DiscoveryDbContext db) : IRequestHandler<ListCustomFieldsQuery, Result<IReadOnlyList<CustomFieldDto>>>
{
    public async Task<Result<IReadOnlyList<CustomFieldDto>>> Handle(ListCustomFieldsQuery q, CancellationToken ct)
    {
        var defs = await svc.GetDefinitionsAsync(q.ScopeType, q.IncludeInactive, ct);

        // Visão reversa: só os campos vinculados à tarefa/script consultado.
        if (q.TaskId.HasValue || q.ScriptId.HasValue)
        {
            var taskId = q.TaskId;
            var scriptId = q.ScriptId;
            var definitionIds = await db.CustomFieldExecutionAccesses
                .AsNoTracking()
                .Where(binding => (taskId != null && binding.TaskId == taskId)
                                  || (scriptId != null && binding.ScriptId == scriptId))
                .Select(binding => binding.DefinitionId)
                .Distinct()
                .ToListAsync(ct);

            var allowed = definitionIds.ToHashSet();
            defs = defs.Where(definition => allowed.Contains(definition.Id)).ToList();
        }

        var departmentNames = await CustomFieldDtoMapping.ResolveDepartmentNamesAsync(db, defs, ct);
        var bindings = await CustomFieldDtoMapping.ResolveAccessBindingsAsync(db, defs, ct);
        var items = defs
            .Select(d => CustomFieldDtoMapping.ToDto(
                d,
                CustomFieldDtoMapping.DepartmentNameFor(departmentNames, d.DepartmentId),
                bindings.GetValueOrDefault(d.Id, [])))
            .ToList()
            .AsReadOnly();
        return Result<IReadOnlyList<CustomFieldDto>>.Success(items);
    }
}

public sealed class GetCustomFieldByIdQueryHandler(ICustomFieldService svc, DiscoveryDbContext db) : IRequestHandler<GetCustomFieldByIdQuery, Result<CustomFieldDto>>
{
    public async Task<Result<CustomFieldDto>> Handle(GetCustomFieldByIdQuery q, CancellationToken ct)
    {
        var d = await svc.GetDefinitionByIdAsync(q.Id, ct);
        if (d is null) return Result<CustomFieldDto>.Failure(Error.NotFound($"CustomField {q.Id} not found"));
        var departmentNames = await CustomFieldDtoMapping.ResolveDepartmentNamesAsync(db, [d], ct);
        var bindings = await CustomFieldDtoMapping.ResolveAccessBindingsAsync(db, [d], ct);
        return Result<CustomFieldDto>.Success(CustomFieldDtoMapping.ToDto(
            d,
            CustomFieldDtoMapping.DepartmentNameFor(departmentNames, d.DepartmentId),
            bindings.GetValueOrDefault(d.Id, [])));
    }
}

public sealed class CreateCustomFieldCommandHandler(ICustomFieldService svc, DiscoveryDbContext db) : IRequestHandler<CreateCustomFieldCommand, Result<CustomFieldDto>>
{
    public async Task<Result<CustomFieldDto>> Handle(CreateCustomFieldCommand cmd, CancellationToken ct)
    {
        var options = cmd.Options ?? CustomFieldHandlerHelper.ParseOptionsJson(cmd.OptionsJson);
        var input = new UpsertCustomFieldDefinitionInput(
            cmd.Name,
            cmd.Label,
            cmd.Description,
            cmd.ScopeType,
            cmd.DataType,
            cmd.IsRequired,
            true,
            cmd.IsSecret,
            options,
            cmd.ValidationRegex,
            null,
            null,
            null,
            null,
            cmd.AllowRuntimeRead,
            cmd.AllowAgentWrite,
            cmd.RuntimeAccessMode,
            CustomFieldDtoMapping.ToBindingInputs(cmd.AccessBindings),
            cmd.DepartmentId,
            cmd.AllowWebRead,
            cmd.AllowWebEdit,
            cmd.IsPinned,
            cmd.DefaultValueJson);

        try
        {
            var d = await svc.CreateDefinitionAsync(input, cmd.UpdatedBy, ct);
            if (d is null) return Result<CustomFieldDto>.Failure(Error.Internal("Failed to create custom field"));
            var bindings = await CustomFieldDtoMapping.ResolveAccessBindingsAsync(db, [d], ct);
            return Result<CustomFieldDto>.Success(CustomFieldDtoMapping.ToDto(d, null, bindings.GetValueOrDefault(d.Id, [])));
        }
        catch (InvalidOperationException ex)
        {
            return Result<CustomFieldDto>.Failure(Error.Validation("CustomField", ex.Message));
        }
    }
}

public sealed class UpdateCustomFieldCommandHandler(ICustomFieldService svc, DiscoveryDbContext db) : IRequestHandler<UpdateCustomFieldCommand, Result<CustomFieldDto>>
{
    public async Task<Result<CustomFieldDto>> Handle(UpdateCustomFieldCommand cmd, CancellationToken ct)
    {
        var d = await svc.GetDefinitionByIdAsync(cmd.Id, ct);
        if (d is null) return Result<CustomFieldDto>.Failure(Error.NotFound($"CustomField {cmd.Id} not found"));

        // Patch: o que não veio no payload preserva o valor atual. Sem isso, editar o
        // título de um campo zerava a configuração de automação (flags + vínculos).
        var options = cmd.Options ?? CustomFieldHandlerHelper.ParseOptionsJson(cmd.OptionsJson ?? d.OptionsJson);

        IReadOnlyList<CustomFieldAccessBindingInput>? accessBindings;
        if (cmd.AccessBindings is null)
        {
            var existing = await db.CustomFieldExecutionAccesses
                .AsNoTracking()
                .Where(binding => binding.DefinitionId == cmd.Id)
                .ToListAsync(ct);
            accessBindings = existing
                .Select(binding => new CustomFieldAccessBindingInput(binding.TaskId, binding.ScriptId, binding.CanRead, binding.CanWrite))
                .ToList();
        }
        else
        {
            accessBindings = CustomFieldDtoMapping.ToBindingInputs(cmd.AccessBindings);
        }

        var input = new UpsertCustomFieldDefinitionInput(
            cmd.Name ?? d.Name,
            cmd.Label ?? d.Label,
            cmd.Description ?? d.Description,
            d.ScopeType,
            d.DataType,
            cmd.IsRequired ?? d.IsRequired,
            cmd.IsActive ?? d.IsActive,
            cmd.IsSecret ?? d.IsSecret,
            options,
            cmd.ValidationRegex ?? d.ValidationRegex,
            d.MinLength,
            d.MaxLength,
            d.MinValue,
            d.MaxValue,
            cmd.AllowRuntimeRead ?? d.AllowRuntimeRead,
            cmd.AllowAgentWrite ?? d.AllowAgentWrite,
            cmd.RuntimeAccessMode ?? d.RuntimeAccessMode,
            accessBindings,
            cmd.DepartmentId ?? d.DepartmentId,
            cmd.AllowWebRead ?? d.AllowWebRead,
            cmd.AllowWebEdit ?? d.AllowWebEdit,
            cmd.IsPinned ?? d.IsPinned,
            cmd.DefaultValueJson ?? d.DefaultValueJson);

        try
        {
            var updated = await svc.UpdateDefinitionAsync(cmd.Id, input, cmd.UpdatedBy, ct);
            if (updated is null) return Result<CustomFieldDto>.Failure(Error.NotFound($"CustomField {cmd.Id} not found"));
            var bindings = await CustomFieldDtoMapping.ResolveAccessBindingsAsync(db, [updated], ct);
            return Result<CustomFieldDto>.Success(CustomFieldDtoMapping.ToDto(updated, null, bindings.GetValueOrDefault(updated.Id, [])));
        }
        catch (InvalidOperationException ex)
        {
            return Result<CustomFieldDto>.Failure(Error.Validation("CustomField", ex.Message));
        }
    }
}

public sealed class DeactivateCustomFieldCommandHandler(ICustomFieldService svc) : IRequestHandler<DeactivateCustomFieldCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeactivateCustomFieldCommand cmd, CancellationToken ct)
    {
        var ok = await svc.DeactivateDefinitionAsync(cmd.Id, ct);
        return ok ? Result<VoidResult>.Success(VoidResult.Value) : Result<VoidResult>.Failure(Error.NotFound($"CustomField {cmd.Id} not found"));
    }
}

public sealed class ListCustomFieldValuesQueryHandler(ICustomFieldService svc) : IRequestHandler<ListCustomFieldValuesQuery, Result<CursorPageDto<CustomFieldResolvedValueDto>>>
{
    public async Task<Result<CursorPageDto<CustomFieldResolvedValueDto>>> Handle(ListCustomFieldValuesQuery q, CancellationToken ct)
    {
        var page = await svc.GetValuesPageAsync(q.ScopeType, q.EntityId, q.Cursor, q.Limit, q.IncludeSecrets, ct);
        return Result<CursorPageDto<CustomFieldResolvedValueDto>>.Success(page);
    }
}

public sealed class UpsertCustomFieldValueCommandHandler(ICustomFieldService svc) : IRequestHandler<UpsertCustomFieldValueCommand, Result<CustomFieldResolvedValueDto>>
{
    public async Task<Result<CustomFieldResolvedValueDto>> Handle(UpsertCustomFieldValueCommand cmd, CancellationToken ct)
    {
        var input = new UpsertCustomFieldValueInput(cmd.DefinitionId, cmd.ScopeType, cmd.EntityId, cmd.ValueJson, cmd.UpdatedBy);
        var result = await svc.UpsertValueAsync(input, ct);
        return Result<CustomFieldResolvedValueDto>.Success(result);
    }
}

// ── Helpers ──────────────────────────────────────────────────────────

/// <summary>
/// Mapeia definições para o DTO de listagem. O nome do departamento é resolvido
/// junto (não vem no agregado) para a tela de Definições conseguir mostrar a qual
/// departamento um campo está vinculado, em vez de exibir só o escopo.
/// </summary>
internal static class CustomFieldDtoMapping
{
    public static CustomFieldDto ToDto(
        CustomFieldDefinition definition,
        string? departmentName = null,
        IReadOnlyList<CustomFieldAccessBindingDto>? accessBindings = null) => new(
        definition.Id,
        definition.Name,
        definition.Label,
        definition.Description,
        definition.ScopeType.ToString(),
        definition.DataType.ToString(),
        definition.IsRequired,
        definition.IsSecret,
        definition.IsActive,
        definition.DepartmentId,
        definition.CreatedAt,
        definition.UpdatedAt,
        departmentName,
        definition.AllowRuntimeRead,
        definition.AllowAgentWrite,
        definition.RuntimeAccessMode,
        accessBindings ?? [],
        definition.AllowWebRead,
        definition.AllowWebEdit,
        definition.IsPinned,
        CustomFieldHandlerHelper.ParseOptionsJson(definition.OptionsJson),
        definition.DefaultValueJson);

    /// <summary>Vínculos de execução (tarefa/script) por definição, para exibição na tela.</summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomFieldAccessBindingDto>>> ResolveAccessBindingsAsync(
        DiscoveryDbContext db,
        IReadOnlyCollection<CustomFieldDefinition> definitions,
        CancellationToken ct)
    {
        var definitionIds = definitions.Select(definition => definition.Id).Distinct().ToList();
        if (definitionIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<CustomFieldAccessBindingDto>>();

        var rows = await db.CustomFieldExecutionAccesses
            .AsNoTracking()
            .Where(binding => definitionIds.Contains(binding.DefinitionId))
            .OrderBy(binding => binding.TaskId)
            .ThenBy(binding => binding.ScriptId)
            .ToListAsync(ct);

        return rows
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
    }

    /// <summary>Converte o vínculo vindo da API no input do serviço. null permanece null (preservar).</summary>
    public static IReadOnlyList<CustomFieldAccessBindingInput>? ToBindingInputs(
        IReadOnlyList<CustomFieldAccessBindingCommand>? bindings)
        => bindings?
            .Where(binding => binding.TaskId.HasValue || binding.ScriptId.HasValue)
            .Select(binding => new CustomFieldAccessBindingInput(binding.TaskId, binding.ScriptId, binding.CanRead, binding.CanWrite))
            .ToList();

    public static async Task<IReadOnlyDictionary<Guid, string>> ResolveDepartmentNamesAsync(
        DiscoveryDbContext db,
        IReadOnlyCollection<CustomFieldDefinition> definitions,
        CancellationToken ct)
    {
        var departmentIds = definitions
            .Where(definition => definition.DepartmentId.HasValue)
            .Select(definition => definition.DepartmentId!.Value)
            .Distinct()
            .ToList();

        if (departmentIds.Count == 0)
            return new Dictionary<Guid, string>();

        return await db.Departments
            .AsNoTracking()
            .Where(department => departmentIds.Contains(department.Id))
            .ToDictionaryAsync(department => department.Id, department => department.Name, ct);
    }

    public static string? DepartmentNameFor(IReadOnlyDictionary<Guid, string> departmentNames, Guid? departmentId)
        => departmentId.HasValue && departmentNames.TryGetValue(departmentId.Value, out var name) ? name : null;
}

/// <summary>
/// Converte uma string JSON de opções (ex: "opcao1,opcao2") ou array JSON em IReadOnlyList&lt;string&gt;.
/// </summary>
internal static class CustomFieldHandlerHelper
{
    public static IReadOnlyList<string>? ParseOptionsJson(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
            return null;

        // Tenta parse como array JSON primeiro: ["a","b"]
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(optionsJson);
            if (arr is { Count: > 0 })
                return arr.AsReadOnly();
        }
        catch { /* fallback para split por vírgula */ }

        // Fallback: string separada por vírgula
        return optionsJson
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList()
            .AsReadOnly();
    }
}
