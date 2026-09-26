using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Modelo reutilizável de campo personalizado. Define o tipo do campo, máscara,
/// validador (regex), limites e opções, servindo de ponto de partida para o
/// usuário criar um campo personalizado de departamento sem configurar tudo à mão.
/// Escopo: `ClientId`/`DepartmentId` nulos = modelo global do servidor.
/// </summary>
public class CustomFieldTemplate
{
    public Guid Id { get; set; }

    /// <summary>Cliente dono do modelo (null = global).</summary>
    public Guid? ClientId { get; set; }

    /// <summary>Departamento dono do modelo (null = qualquer departamento).</summary>
    public Guid? DepartmentId { get; set; }

    /// <summary>Identificador estável do modelo (ex: `cpf`). Único por escopo.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Rótulo exibido no seletor de modelos.</summary>
    public string Label { get; set; } = string.Empty;

    public string? Description { get; set; }

    public CustomFieldDataType DataType { get; set; } = CustomFieldDataType.Text;

    /// <summary>Opções de Dropdown/ListBox serializadas como JSON array.</summary>
    public string? OptionsJson { get; set; }

    public string? ValidationRegex { get; set; }

    /// <summary>Máscara de entrada (9=dígito, A=letra, *=alfanumérico, literais fixos).</summary>
    public string? InputMask { get; set; }

    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }

    /// <summary>Sugere que o campo criado a partir deste modelo nasça obrigatório.</summary>
    public bool DefaultIsRequired { get; set; }

    /// <summary>Modelos built-in não podem ser excluídos (apenas desativados).</summary>
    public bool IsBuiltIn { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
