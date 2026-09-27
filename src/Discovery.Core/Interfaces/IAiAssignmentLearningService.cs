using Discovery.Core.DTOs;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Aprendizado híbrido da triagem por IA: extrai competências do histórico,
/// recalibra pesos a partir da taxa de override e aplica ou sugere conforme o
/// modo configurado no departamento (Off / Sugerir / Automático).
/// </summary>
public interface IAiAssignmentLearningService
{
    /// <summary>Roda um ciclo de aprendizado (todos os departamentos ou um específico).</summary>
    Task<int> RunCycleAsync(Guid? departmentId = null, Guid? actorUserId = null, CancellationToken ct = default);

    /// <summary>Sugestões pendentes de competências e de pesos do departamento.</summary>
    Task<DepartmentLearningSuggestionsDto> GetSuggestionsAsync(Guid departmentId, CancellationToken ct = default);

    Task<TechnicianSkillSuggestionDto?> ApplySkillSuggestionAsync(
        Guid departmentId, Guid suggestionId, Guid? actorUserId, CancellationToken ct = default);

    Task<bool> DiscardSkillSuggestionAsync(
        Guid departmentId, Guid suggestionId, Guid? actorUserId, CancellationToken ct = default);

    Task<AiWeightSuggestionDto?> ApplyWeightSuggestionAsync(
        Guid departmentId, Guid suggestionId, Guid? actorUserId, CancellationToken ct = default);

    Task<bool> DiscardWeightSuggestionAsync(
        Guid departmentId, Guid suggestionId, Guid? actorUserId, CancellationToken ct = default);
}
