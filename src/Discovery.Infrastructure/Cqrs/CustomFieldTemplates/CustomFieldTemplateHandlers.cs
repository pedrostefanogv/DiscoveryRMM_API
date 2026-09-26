using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.CustomFieldTemplates;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.CustomFieldTemplates;

/// <summary>
/// CRUD dos modelos de campos personalizados pré-configurados. Modelos built-in
/// podem ser editados (ex.: máscara) mas nunca excluídos — apenas desativados.
/// </summary>
internal static class CustomFieldTemplateMapping
{
    public static UpsertCustomFieldTemplateInput ToInput(CreateCustomFieldTemplateCommand cmd) => new(
        cmd.ClientId, cmd.DepartmentId, cmd.Name, cmd.Label, cmd.Description,
        cmd.DataType, cmd.Options, cmd.ValidationRegex, cmd.InputMask,
        cmd.MinLength, cmd.MaxLength, cmd.MinValue, cmd.MaxValue,
        cmd.DefaultIsRequired, cmd.IsActive, cmd.SortOrder);

    public static UpsertCustomFieldTemplateInput ToInput(UpdateCustomFieldTemplateCommand cmd) => new(
        cmd.ClientId, cmd.DepartmentId, cmd.Name, cmd.Label, cmd.Description,
        cmd.DataType, cmd.Options, cmd.ValidationRegex, cmd.InputMask,
        cmd.MinLength, cmd.MaxLength, cmd.MinValue, cmd.MaxValue,
        cmd.DefaultIsRequired, cmd.IsActive, cmd.SortOrder);

    public static IReadOnlyList<string> ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(optionsJson)?
                .Select(o => (o ?? string.Empty).Trim())
                .Where(o => o.Length > 0)
                .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static string? SerializeOptions(IReadOnlyList<string>? options)
    {
        if (options is null) return null;
        var clean = options.Select(o => (o ?? string.Empty).Trim()).Where(o => o.Length > 0).ToList();
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    public static CustomFieldTemplateDto Map(CustomFieldTemplate t) => new(
        t.Id, t.ClientId, t.DepartmentId, t.Name, t.Label, t.Description,
        t.DataType, ParseOptions(t.OptionsJson), t.ValidationRegex, t.InputMask,
        t.MinLength, t.MaxLength, t.MinValue, t.MaxValue, t.DefaultIsRequired,
        t.IsBuiltIn, t.IsActive, t.SortOrder, t.CreatedBy, t.CreatedAt, t.UpdatedAt);

    public static Error? Validate(UpsertCustomFieldTemplateInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length < 2)
            return Error.Validation("Name", "Nome do modelo é obrigatório (mínimo 2 caracteres).");
        if (string.IsNullOrWhiteSpace(input.Label))
            return Error.Validation("Label", "Rótulo do modelo é obrigatório.");

        var needsOptions = input.DataType is CustomFieldDataType.Dropdown or CustomFieldDataType.ListBox;
        if (needsOptions && (input.Options is null || input.Options.All(o => string.IsNullOrWhiteSpace(o))))
            return Error.Validation("Options", "Dropdown e ListBox exigem pelo menos uma opção.");

        if (!string.IsNullOrWhiteSpace(input.ValidationRegex))
        {
            try { _ = new Regex(input.ValidationRegex); }
            catch (ArgumentException) { return Error.Validation("ValidationRegex", "Regex de validação inválida."); }
        }

        if (input.MinLength is < 0 || input.MaxLength is < 0)
            return Error.Validation("MinLength", "Tamanho mínimo/máximo não pode ser negativo.");
        if (input.MinLength.HasValue && input.MaxLength.HasValue && input.MinLength > input.MaxLength)
            return Error.Validation("MinLength", "Tamanho mínimo não pode ser maior que o máximo.");
        if (input.MinValue.HasValue && input.MaxValue.HasValue && input.MinValue > input.MaxValue)
            return Error.Validation("MinValue", "Valor mínimo não pode ser maior que o máximo.");

        return null;
    }

    public static void ApplyInput(CustomFieldTemplate t, UpsertCustomFieldTemplateInput input)
    {
        t.ClientId = input.ClientId;
        t.DepartmentId = input.DepartmentId;
        t.Name = input.Name.Trim();
        t.Label = input.Label.Trim();
        t.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        t.DataType = input.DataType;
        t.OptionsJson = SerializeOptions(input.Options);
        t.ValidationRegex = string.IsNullOrWhiteSpace(input.ValidationRegex) ? null : input.ValidationRegex.Trim();
        t.InputMask = string.IsNullOrWhiteSpace(input.InputMask) ? null : input.InputMask.Trim();
        t.MinLength = input.MinLength;
        t.MaxLength = input.MaxLength;
        t.MinValue = input.MinValue;
        t.MaxValue = input.MaxValue;
        t.DefaultIsRequired = input.DefaultIsRequired;
        t.IsActive = input.IsActive;
        t.SortOrder = input.SortOrder;
        t.UpdatedAt = DateTime.UtcNow;
    }
}

public sealed class CreateCustomFieldTemplateCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<CreateCustomFieldTemplateCommand, Result<CustomFieldTemplateDto>>
{
    public async Task<Result<CustomFieldTemplateDto>> Handle(CreateCustomFieldTemplateCommand cmd, CancellationToken ct)
    {
        var input = CustomFieldTemplateMapping.ToInput(cmd);
        var validation = CustomFieldTemplateMapping.Validate(input);
        if (validation is not null) return Result<CustomFieldTemplateDto>.Failure(validation);

        var name = input.Name.Trim();
        var duplicate = await db.CustomFieldTemplates.AnyAsync(
            t => t.Name == name && t.ClientId == input.ClientId && t.DepartmentId == input.DepartmentId, ct);
        if (duplicate)
            return Result<CustomFieldTemplateDto>.Failure(Error.Conflict("Já existe um modelo de campo com esse nome no mesmo escopo."));

        var template = new CustomFieldTemplate
        {
            Id = Guid.NewGuid(),
            CreatedBy = cmd.CreatedBy,
            CreatedAt = DateTime.UtcNow,
        };
        CustomFieldTemplateMapping.ApplyInput(template, input);

        db.CustomFieldTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return Result<CustomFieldTemplateDto>.Success(CustomFieldTemplateMapping.Map(template));
    }
}

public sealed class UpdateCustomFieldTemplateCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<UpdateCustomFieldTemplateCommand, Result<CustomFieldTemplateDto>>
{
    public async Task<Result<CustomFieldTemplateDto>> Handle(UpdateCustomFieldTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.CustomFieldTemplates.FirstOrDefaultAsync(t => t.Id == cmd.Id, ct);
        if (template is null) return Result<CustomFieldTemplateDto>.Failure(Error.NotFound("Modelo de campo não encontrado."));

        var input = CustomFieldTemplateMapping.ToInput(cmd);
        var validation = CustomFieldTemplateMapping.Validate(input);
        if (validation is not null) return Result<CustomFieldTemplateDto>.Failure(validation);

        if (template.IsBuiltIn && input.DataType != template.DataType)
            return Result<CustomFieldTemplateDto>.Failure(Error.Validation("DataType", "Não é possível alterar o tipo de um modelo built-in."));

        var name = input.Name.Trim();
        var duplicate = await db.CustomFieldTemplates.AnyAsync(
            t => t.Id != cmd.Id && t.Name == name && t.ClientId == input.ClientId && t.DepartmentId == input.DepartmentId, ct);
        if (duplicate)
            return Result<CustomFieldTemplateDto>.Failure(Error.Conflict("Já existe um modelo de campo com esse nome no mesmo escopo."));

        CustomFieldTemplateMapping.ApplyInput(template, input);
        await db.SaveChangesAsync(ct);
        return Result<CustomFieldTemplateDto>.Success(CustomFieldTemplateMapping.Map(template));
    }
}

public sealed class DeleteCustomFieldTemplateCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<DeleteCustomFieldTemplateCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteCustomFieldTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.CustomFieldTemplates.FirstOrDefaultAsync(t => t.Id == cmd.Id, ct);
        if (template is null) return Result<VoidResult>.Failure(Error.NotFound("Modelo de campo não encontrado."));
        if (template.IsBuiltIn)
            return Result<VoidResult>.Failure(Error.Validation("Id", "Modelos built-in não podem ser excluídos — apenas desativados."));

        template.IsActive = false;
        template.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ListCustomFieldTemplatesQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<ListCustomFieldTemplatesQuery, Result<IReadOnlyList<CustomFieldTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<CustomFieldTemplateDto>>> Handle(ListCustomFieldTemplatesQuery q, CancellationToken ct)
    {
        var query = db.CustomFieldTemplates.AsNoTracking();
        if (!q.IncludeInactive) query = query.Where(t => t.IsActive);

        if (q.AllScopes)
        {
            // Gestão: sem recorte de escopo.
            var all = await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Label).ToListAsync(ct);
            return Result<IReadOnlyList<CustomFieldTemplateDto>>.Success(
                all.Select(CustomFieldTemplateMapping.Map).ToList());
        }

        if (q.ClientId.HasValue)
            query = q.IncludeGlobal
                ? query.Where(t => t.ClientId == q.ClientId || t.ClientId == null)
                : query.Where(t => t.ClientId == q.ClientId);
        else
            query = query.Where(t => t.ClientId == null);

        if (q.DepartmentId.HasValue)
            query = query.Where(t => t.DepartmentId == q.DepartmentId || t.DepartmentId == null);
        else
            query = query.Where(t => t.DepartmentId == null);

        var items = await query
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Label)
            .ToListAsync(ct);

        return Result<IReadOnlyList<CustomFieldTemplateDto>>.Success(
            items.Select(CustomFieldTemplateMapping.Map).ToList());
    }
}
