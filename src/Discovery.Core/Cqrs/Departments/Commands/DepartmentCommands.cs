using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Departments.Commands;

public sealed record CreateDepartmentCommand(
    string Name, string? Description, Guid? ClientId,
    Guid? InheritFromGlobalId, int SortOrder, int AssignmentStrategy = 0,
    int AiAssignmentMode = 0, double AiAssignmentMinConfidence = 0.60,
    int AiAssignmentFallbackStrategy = 1, int AiAssignmentMaxCandidates = 8,
    string? AiAssignmentWeightsJson = null, string? AiAssignmentInstructions = null,
    bool AiAssignmentUseAffinity = true,
    int AiAssignmentMaxOutputTokens = 1200,
    int AiSkillLearningMode = 1, int AiSkillMinEvidence = 3, int AiSkillMaxTags = 12,
    int AiWeightLearningMode = 1, decimal AiWeightMaxDeltaPerCycle = 0.10m,
    int AiWeightCycleDays = 7, decimal AiWeightMin = 0.05m, decimal AiWeightMax = 0.50m
) : ICommand<Result<DepartmentDto>>;

public sealed record UpdateDepartmentCommand(
    Guid Id, string? Name, string? Description,
    Guid? InheritFromGlobalId, int? SortOrder, bool? IsActive, int? AssignmentStrategy = null,
    int? AiAssignmentMode = null, double? AiAssignmentMinConfidence = null,
    int? AiAssignmentFallbackStrategy = null, int? AiAssignmentMaxCandidates = null,
    string? AiAssignmentWeightsJson = null, string? AiAssignmentInstructions = null,
    bool? AiAssignmentUseAffinity = null,
    int? AiAssignmentMaxOutputTokens = null,
    int? AiSkillLearningMode = null, int? AiSkillMinEvidence = null, int? AiSkillMaxTags = null,
    int? AiWeightLearningMode = null, decimal? AiWeightMaxDeltaPerCycle = null,
    int? AiWeightCycleDays = null, decimal? AiWeightMin = null, decimal? AiWeightMax = null
) : ICommand<Result<DepartmentDto>>;

public sealed record DeleteDepartmentCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record DepartmentDto(
    Guid Id, Guid? ClientId, string Name, string? Description,
    Guid? InheritFromGlobalId, int SortOrder, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt, int AssignmentStrategy = 0,
    int AiAssignmentMode = 0, double AiAssignmentMinConfidence = 0.60,
    int AiAssignmentFallbackStrategy = 1, int AiAssignmentMaxCandidates = 8,
    string? AiAssignmentWeightsJson = null, string? AiAssignmentInstructions = null,
    bool AiAssignmentUseAffinity = true,
    int AiAssignmentMaxOutputTokens = 1200,
    int AiSkillLearningMode = 1, int AiSkillMinEvidence = 3, int AiSkillMaxTags = 12,
    int AiWeightLearningMode = 1, decimal AiWeightMaxDeltaPerCycle = 0.10m,
    int AiWeightCycleDays = 7, decimal AiWeightMin = 0.05m, decimal AiWeightMax = 0.50m
);
