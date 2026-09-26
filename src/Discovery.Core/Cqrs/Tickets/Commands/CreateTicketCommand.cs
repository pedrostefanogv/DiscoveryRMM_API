using System.Text.Json;
using Discovery.Core.Cqrs.Tickets.Dtos;

namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>
/// Command to create a new ticket.
/// </summary>
public sealed record CreateTicketCommand(
    string Title,
    string Description,
    Enums.TicketPriority Priority,
    Guid ClientId,
    Guid? SiteId,
    Guid? AgentId,
    Guid? DepartmentId,
    Guid? WorkflowProfileId,
    Guid? AssignedToUserId,
    string? Category,
    /// <summary>Template que pré-preenche o chamado (opcional).</summary>
    Guid? TemplateId = null,
    /// <summary>Valores dos campos personalizados do departamento (definitionId → JSON), opcional.</summary>
    IReadOnlyDictionary<Guid, JsonElement>? CustomFieldValues = null,
    /// <summary>Respostas do mini questionário do template (chave da pergunta → JSON), opcional.</summary>
    IReadOnlyDictionary<string, JsonElement>? TemplateAnswers = null
) : ICommand<Result<TicketDetailDto>>;
