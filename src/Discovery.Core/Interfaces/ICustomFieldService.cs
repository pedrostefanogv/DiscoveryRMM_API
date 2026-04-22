using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

public interface ICustomFieldService
{
    Task<IReadOnlyList<CustomFieldDefinition>> GetDefinitionsAsync(CustomFieldScopeType? scopeType, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<CustomFieldDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CustomFieldDefinition> CreateDefinitionAsync(UpsertCustomFieldDefinitionInput input, string? updatedBy, CancellationToken cancellationToken = default);
    Task<CustomFieldDefinition?> UpdateDefinitionAsync(Guid id, UpsertCustomFieldDefinitionInput input, string? updatedBy, CancellationToken cancellationToken = default);
    Task<bool> DeactivateDefinitionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomFieldResolvedValueDto>> GetValuesAsync(CustomFieldScopeType scopeType, Guid? entityId, bool includeSecrets = true, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomFieldSchemaItemDto>> GetSchemaAsync(CustomFieldScopeType scopeType, Guid? entityId, bool includeInactive = false, bool includeSecrets = true, CancellationToken cancellationToken = default);
    Task<CustomFieldResolvedValueDto> UpsertValueAsync(UpsertCustomFieldValueInput input, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimeCustomFieldDto>> GetRuntimeValuesForAgentAsync(Guid agentId, Guid? taskId, Guid? scriptId, CancellationToken cancellationToken = default);
    Task<CustomFieldResolvedValueDto> UpsertAgentCollectedValueAsync(Guid agentId, AgentCustomFieldCollectedValueInput input, CancellationToken cancellationToken = default);
}
