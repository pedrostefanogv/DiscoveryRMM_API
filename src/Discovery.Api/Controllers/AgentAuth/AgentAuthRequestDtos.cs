using System.Text.Json;
using Discovery.Core.Enums;

namespace Discovery.Api.Controllers;

// ── Agent-facing request DTOs (used by AgentAuthController partial files) ──
//
// These records were originally nested in the monolithic AgentAuthController
// and re-extracted here when the controller was split into partials.

public sealed record AgentCreateTicketRequest(
    string Title,
    string? Description,
    TicketPriority? Priority,
    string? Category,
    Guid? DepartmentId,
    Guid? WorkflowProfileId);

public sealed record AgentMeshCentralEmbedRequest(
    int? ViewMode,
    int? HideMask,
    string? MeshNodeId,
    string? GotoDeviceName);

public sealed record AgentUpsertCustomFieldValueRequest(
    Guid? DefinitionId,
    string? Name,
    JsonElement Value,
    Guid? TaskId,
    Guid? ScriptId);

public sealed record AgentTlsMismatchReport(
    string Target,
    string? ObservedHash);
