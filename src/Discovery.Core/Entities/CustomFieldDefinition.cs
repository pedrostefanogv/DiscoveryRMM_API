using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

public class CustomFieldDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public CustomFieldScopeType ScopeType { get; set; } = CustomFieldScopeType.Agent;
    public CustomFieldDataType DataType { get; set; } = CustomFieldDataType.Text;
    public bool IsRequired { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSecret { get; set; }
    public string? OptionsJson { get; set; }
    public string? ValidationRegex { get; set; }

    /// <summary>Máscara de entrada (9=dígito, A=letra, *=alfanumérico, literais fixos).</summary>
    public string? InputMask { get; set; }

    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public bool AllowRuntimeRead { get; set; }
    public bool AllowAgentWrite { get; set; }
    public CustomFieldRuntimeAccessMode RuntimeAccessMode { get; set; } = CustomFieldRuntimeAccessMode.Disabled;

    /// <summary>Se o valor aparece no portal (detalhe do agente/cliente/site).</summary>
    public bool AllowWebRead { get; set; } = true;

    /// <summary>Se o suporte pode editar o valor pelo portal. Exige AllowWebRead.</summary>
    public bool AllowWebEdit { get; set; } = true;

    /// <summary>Destaque no card do detalhe (aparece antes dos demais).</summary>
    public bool IsPinned { get; set; }

    /// <summary>Valor padrão em JSON, usado quando a entidade ainda não tem valor.</summary>
    public string? DefaultValueJson { get; set; }
    
    /// <summary>
    /// Departamento ao qual este campo pertence (quando ScopeType = Department).
    /// </summary>
    public Guid? DepartmentId { get; set; }
    
    /// <summary>
    /// Se true, o campo é interno e visível apenas para atendentes do departamento.
    /// Se false, o campo aparece no formulário público de abertura de chamado.
    /// </summary>
    public bool IsInternal { get; set; }

    // ── Report fields ───────────────────────────────────────────────────
    public Guid? ClientId { get; set; }
    public string? EntityName => ScopeType.ToString();
    public CustomFieldDataType ValueType => DataType;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
