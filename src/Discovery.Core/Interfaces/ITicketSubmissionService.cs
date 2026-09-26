using System.Text.Json;
using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Orquestra o pré-preenchimento por template, o mini questionário, os campos
/// personalizados do departamento e o snapshot markdown (somente leitura).
/// </summary>
public interface ITicketSubmissionService
{
    Task<TicketSubmissionResult> PrepareAsync(
        TicketSubmissionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Persiste as respostas estruturadas do questionário do template.</summary>
    Task SaveTemplateAnswersAsync(
        Guid ticketId,
        Guid? templateId,
        IReadOnlyList<TicketAnswerDraft> answers,
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
    IReadOnlyDictionary<Guid, JsonElement>? CustomFieldValues,
    /// <summary>Respostas do mini questionário do template (chave da pergunta → valor).</summary>
    IReadOnlyDictionary<string, JsonElement>? TemplateAnswers = null);

public sealed record TicketAnswerDraft(
    string QuestionKey,
    string QuestionLabel,
    string? ValueText,
    string ValueJson);

public sealed record TicketSubmissionResult(
    Guid? DepartmentId,
    string Title,
    string Description,
    string? Category,
    string? Priority,
    IReadOnlyDictionary<Guid, string> CustomFieldValues,
    IReadOnlyList<DepartmentFieldValidationError> Errors,
    string? SnapshotMarkdown,
    Guid? TemplateId = null,
    IReadOnlyList<TicketAnswerDraft>? Answers = null,
    string? TemplateName = null)
{
    public bool IsValid => Errors.Count == 0;
}
