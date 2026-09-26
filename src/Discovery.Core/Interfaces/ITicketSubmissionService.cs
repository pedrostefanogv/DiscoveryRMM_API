using System.Text.Json;
using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Orquestra o pré-preenchimento por template, a validação dos campos
/// personalizados e o snapshot markdown (somente leitura) gerado na abertura do
/// chamado. Usado tanto pelo portal web quanto pelo agent de chat.
/// </summary>
public interface ITicketSubmissionService
{
    Task<TicketSubmissionResult> PrepareAsync(
        TicketSubmissionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TicketSubmissionRequest(
    Guid ClientId,
    Guid? DepartmentId,
    Guid? TemplateId,
    string? Title,
    string? Description,
    string? Category,
    string? Priority,
    IReadOnlyDictionary<Guid, JsonElement>? CustomFieldValues);

public sealed record TicketSubmissionResult(
    Guid? DepartmentId,
    string Title,
    string Description,
    string? Category,
    string? Priority,
    IReadOnlyDictionary<Guid, string> CustomFieldValues,
    IReadOnlyList<DepartmentFieldValidationError> Errors,
    string? SnapshotMarkdown)
{
    public bool IsValid => Errors.Count == 0;
}
