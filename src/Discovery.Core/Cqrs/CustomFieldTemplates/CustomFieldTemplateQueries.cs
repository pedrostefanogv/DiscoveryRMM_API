using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.CustomFieldTemplates;

/// <param name="AllScopes">
/// Tela de gestão: ignora o recorte de cliente/departamento e devolve todos os
/// modelos (mantém o filtro de ativos). O recorte por escopo é o comportamento
/// usado pelos seletores de "modelo de campo".
/// </param>
public sealed record ListCustomFieldTemplatesQuery(
    Guid? ClientId = null,
    Guid? DepartmentId = null,
    bool IncludeGlobal = true,
    bool IncludeInactive = false,
    bool AllScopes = false) : IQuery<Result<IReadOnlyList<CustomFieldTemplateDto>>>;
