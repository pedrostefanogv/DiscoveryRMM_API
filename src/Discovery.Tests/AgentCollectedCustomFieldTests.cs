using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.AgentAuth.Handlers;

namespace Discovery.Tests;

/// <summary>
/// Regressão do bug de contrato do endpoint coletado por agent:
/// o agent Go envia o corpo PLANO
/// ({"definitionId":..,"value":"..","taskId":..,"scriptId":..}).
/// A API já exigiu um wrapper {"request":{...}} e respondia
/// 400 "The Request field is required.", descartando toda coleta.
/// </summary>
public class AgentCollectedCustomFieldTests
{
    /// <summary>Payload exato do agent (CollectedValueRequest em Go).</summary>
    private const string AgentFlatPayload =
        "{\"definitionId\":\"019f665d-06e6-7345-8b86-e82c4ba4a1fb\",\"value\":\"1602799292\"," +
        "\"taskId\":\"01a1310e-f95e-7eb7-924b-fee259163163\"," +
        "\"scriptId\":\"01a130fa-8490-788f-8037-63aecea49beb\"}";

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    [Test]
    public void FlatPayload_DeserializesIntoRequestDto()
    {
        var request = JsonSerializer.Deserialize<AgentCollectedCustomFieldValueRequest>(AgentFlatPayload, CaseInsensitive);

        Assert.That(request, Is.Not.Null);
        Assert.That(request!.DefinitionId, Is.EqualTo(Guid.Parse("019f665d-06e6-7345-8b86-e82c4ba4a1fb")));
        Assert.That(request.TaskId, Is.EqualTo(Guid.Parse("01a1310e-f95e-7eb7-924b-fee259163163")));
        Assert.That(request.ScriptId, Is.EqualTo(Guid.Parse("01a130fa-8490-788f-8037-63aecea49beb")));
        // O valor precisa chegar como JSON bruto (string), não como número.
        Assert.That(request.Value?.GetRawText(), Is.EqualTo("\"1602799292\""));
    }

    [Test]
    public async Task Handler_MapsFlatPayloadToServiceInput()
    {
        var request = JsonSerializer.Deserialize<AgentCollectedCustomFieldValueRequest>(AgentFlatPayload, CaseInsensitive)!;
        var agentId = Guid.NewGuid();
        var service = new CapturingCustomFieldService();
        var handler = new UpsertCollectedCustomFieldHandler(service);

        var result = await handler.Handle(new UpsertCollectedCustomFieldCommand(agentId, request), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(service.AgentId, Is.EqualTo(agentId));
        Assert.That(service.Input, Is.Not.Null);
        Assert.That(service.Input!.ValueJson, Is.EqualTo("\"1602799292\""));
        Assert.That(service.Input.DefinitionId, Is.EqualTo(request.DefinitionId));
        Assert.That(service.Input.TaskId, Is.EqualTo(request.TaskId));
        Assert.That(service.Input.ScriptId, Is.EqualTo(request.ScriptId));
    }

    [Test]
    public async Task Handler_RejectsMissingValue()
    {
        var request = new AgentCollectedCustomFieldValueRequest(Guid.NewGuid(), null, null, null, null, null);
        var handler = new UpsertCollectedCustomFieldHandler(new CapturingCustomFieldService());

        var result = await handler.Handle(new UpsertCollectedCustomFieldCommand(Guid.NewGuid(), request), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
    }

    private sealed class CapturingCustomFieldService : ICustomFieldService
    {
        public Guid AgentId { get; private set; }
        public AgentCustomFieldCollectedValueInput? Input { get; private set; }

        public Task<CustomFieldResolvedValueDto> UpsertAgentCollectedValueAsync(
            Guid agentId, AgentCustomFieldCollectedValueInput input, CancellationToken cancellationToken = default)
        {
            AgentId = agentId;
            Input = input;
            return Task.FromResult(new CustomFieldResolvedValueDto(
                input.DefinitionId ?? Guid.Empty,
                input.Name ?? string.Empty,
                string.Empty,
                CustomFieldScopeType.Agent,
                agentId,
                input.ValueJson,
                DateTime.UtcNow,
                false));
        }

        public Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsAsync(
            CustomFieldScopeType? scopeType, bool includeInactive = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomFieldDefinition>>([]);

        public Task<CustomFieldDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<CustomFieldDefinition?>(null);

        public Task<CustomFieldDefinition> CreateDefinitionAsync(UpsertCustomFieldDefinitionInput input, string? updatedBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CustomFieldDefinition?> UpdateDefinitionAsync(Guid id, UpsertCustomFieldDefinitionInput input, string? updatedBy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeactivateDefinitionAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomFieldResolvedValueDto>> GetValuesAsync(
            CustomFieldScopeType scopeType, Guid? entityId, bool includeSecrets = true, CancellationToken cancellationToken = default, bool includeHidden = true)
            => throw new NotSupportedException();

        public Task<CursorPageDto<CustomFieldResolvedValueDto>> GetValuesPageAsync(
            CustomFieldScopeType scopeType, Guid? entityId, string? cursor, int limit, bool includeSecrets = true, CancellationToken cancellationToken = default, bool includeHidden = true)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomFieldSchemaItemDto>> GetSchemaAsync(
            CustomFieldScopeType scopeType, Guid? entityId, bool includeInactive = false, bool includeSecrets = true, CancellationToken cancellationToken = default, bool visibleOnly = false)
            => throw new NotSupportedException();

        public Task<CustomFieldResolvedValueDto> UpsertValueAsync(UpsertCustomFieldValueInput input, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RuntimeCustomFieldDto>> GetRuntimeValuesForAgentAsync(Guid agentId, Guid? taskId, Guid? scriptId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
