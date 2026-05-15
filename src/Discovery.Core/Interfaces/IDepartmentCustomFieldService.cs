using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de campos customizados vinculados a departamentos para uso em chamados (Tickets).
/// Orquestra definições, validações e persistência de valores no contexto de departamento.
/// </summary>
public interface IDepartmentCustomFieldService
{
    /// <summary>
    /// Obtém todas as definições de campos customizados de um departamento.
    /// </summary>
    Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsByDepartmentAsync(
        Guid departmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cria um novo campo customizado vinculado ao departamento.
    /// </summary>
    Task<CustomFieldDefinition> CreateDepartmentFieldAsync(
        Guid departmentId,
        CreateDepartmentCustomFieldInput input,
        string? updatedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atualiza um campo customizado existente do departamento.
    /// </summary>
    Task<CustomFieldDefinition?> UpdateDepartmentFieldAsync(
        Guid fieldId,
        Guid departmentId,
        UpdateDepartmentCustomFieldInput input,
        string? updatedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove (soft-delete) um campo customizado do departamento.
    /// </summary>
    Task<bool> DeleteDepartmentFieldAsync(
        Guid fieldId,
        Guid departmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna o schema público do departamento para o formulário de abertura de chamado.
    /// Inclui apenas campos com IsInternal = false e IsActive = true.
    /// </summary>
    Task<IReadOnlyList<DepartmentFieldSchemaItemDto>> GetPublicSchemaForDepartmentAsync(
        Guid departmentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retorna o schema completo do departamento (públicos + internos).
    /// Se ticketId for informado, inclui os valores já preenchidos do ticket.
    /// </summary>
    Task<IReadOnlyList<DepartmentFieldSchemaItemDto>> GetFullSchemaForDepartmentAsync(
        Guid departmentId,
        Guid? ticketId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Valida os campos enviados no momento da criação do ticket.
    /// Verifica obrigatoriedade e regras de validação.
    /// Retorna uma lista de erros; lista vazia = sucesso.
    /// </summary>
    Task<IReadOnlyList<DepartmentFieldValidationError>> ValidateTicketFieldsAsync(
        Guid departmentId,
        IReadOnlyDictionary<Guid, string> fieldValues,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persiste os valores dos campos customizados para um ticket recém-criado.
    /// </summary>
    Task SaveTicketFieldValuesAsync(
        Guid ticketId,
        Guid departmentId,
        IReadOnlyDictionary<Guid, string> fieldValues,
        string? updatedBy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Input para criação de campo customizado de departamento.
/// </summary>
public sealed record CreateDepartmentCustomFieldInput(
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsInternal,
    bool IsActive,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue);

/// <summary>
/// Input para atualização de campo customizado de departamento.
/// </summary>
public sealed record UpdateDepartmentCustomFieldInput(
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsInternal,
    bool IsActive,
    IReadOnlyList<string>? Options,
    string? ValidationRegex,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue);

/// <summary>
/// Item de schema de campo de departamento retornado para o frontend.
/// </summary>
public sealed record DepartmentFieldSchemaItemDto(
    Guid DefinitionId,
    string Name,
    string Label,
    string? Description,
    CustomFieldDataType DataType,
    bool IsRequired,
    bool IsInternal,
    bool IsActive,
    IReadOnlyList<string> Options,
    string? ValidationRegex,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    string? CurrentValueJson);

/// <summary>
/// Erro de validação de campo customizado de departamento.
/// </summary>
public sealed record DepartmentFieldValidationError(
    Guid DefinitionId,
    string FieldName,
    string ErrorMessage);
