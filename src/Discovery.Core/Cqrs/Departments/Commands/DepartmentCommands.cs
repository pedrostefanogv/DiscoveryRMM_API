using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Departments.Commands;

/// <summary>
/// Criação de departamento. Os valores de triagem/aprendizado são CONCRETOS:
/// a criação continua gravando as colunas base exatamente como antes e NÃO
/// cria overrides (todos nascem NULL). Portanto, quando <see cref="InheritFromGlobalId"/>
/// é informado, o departamento novo herda a configuração do global pai até que
/// um PUT marque um campo como próprio. Não há overrides opcionais no create.
/// </summary>
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

/// <summary>
/// Atualização de departamento (patch semântico).
///
/// Os 16 campos de configuração aceitam o NOME BASE usado pelo console:
/// - campo AUSENTE (<see cref="OptionalValue{T}.IsSet"/> = false): mantém o valor atual;
/// - campo com VALOR: grava o valor na coluna base e marca o override (próprio);
/// - campo com NULL explícito: limpa o override e volta a herdar do global pai.
///
/// Os pares "*Override" continuam aceitos como aliases e escrevem exatamente as
/// mesmas colunas *_override (o alias vence quando os dois vêm no mesmo corpo).
/// </summary>
public sealed record UpdateDepartmentCommand(
    Guid Id, string? Name, string? Description,
    // Patch semântico: ausente = mantém; Set(null) = desfaz a herança (D5).
    OptionalGuid InheritFromGlobalId, int? SortOrder, bool? IsActive, int? AssignmentStrategy = null,
    // 8 campos de atribuição (nome base do console).
    OptionalValue<int> AiAssignmentMode = default,
    OptionalValue<double> AiAssignmentMinConfidence = default,
    OptionalValue<int> AiAssignmentFallbackStrategy = default,
    OptionalValue<int> AiAssignmentMaxCandidates = default,
    OptionalText AiAssignmentWeightsJson = default,
    OptionalText AiAssignmentInstructions = default,
    OptionalValue<bool> AiAssignmentUseAffinity = default,
    OptionalValue<int> AiAssignmentMaxOutputTokens = default,
    // 8 campos de aprendizado (competências e pesos).
    OptionalValue<int> AiSkillLearningMode = default,
    OptionalValue<int> AiSkillMinEvidence = default,
    OptionalValue<int> AiSkillMaxTags = default,
    OptionalValue<int> AiWeightLearningMode = default,
    OptionalValue<decimal> AiWeightMaxDeltaPerCycle = default,
    OptionalValue<int> AiWeightCycleDays = default,
    OptionalValue<decimal> AiWeightMin = default,
    OptionalValue<decimal> AiWeightMax = default,
    // Aliases explícitos de override (mesmas colunas; NULL limpa).
    OptionalValue<int> AiAssignmentModeOverride = default,
    OptionalValue<double> AiAssignmentMinConfidenceOverride = default,
    OptionalValue<int> AiAssignmentFallbackStrategyOverride = default,
    OptionalValue<int> AiAssignmentMaxCandidatesOverride = default,
    OptionalText AiAssignmentWeightsJsonOverride = default,
    OptionalText AiAssignmentInstructionsOverride = default,
    OptionalValue<bool> AiAssignmentUseAffinityOverride = default,
    OptionalValue<int> AiAssignmentMaxOutputTokensOverride = default,
    OptionalValue<int> AiSkillLearningModeOverride = default,
    OptionalValue<int> AiSkillMinEvidenceOverride = default,
    OptionalValue<int> AiSkillMaxTagsOverride = default,
    OptionalValue<int> AiWeightLearningModeOverride = default,
    OptionalValue<decimal> AiWeightMaxDeltaPerCycleOverride = default,
    OptionalValue<int> AiWeightCycleDaysOverride = default,
    OptionalValue<decimal> AiWeightMinOverride = default,
    OptionalValue<decimal> AiWeightMaxOverride = default
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
    int AiWeightCycleDays = 7, decimal AiWeightMin = 0.05m, decimal AiWeightMax = 0.50m,
    int? AiAssignmentModeOverride = null, double? AiAssignmentMinConfidenceOverride = null,
    int? AiAssignmentFallbackStrategyOverride = null, int? AiAssignmentMaxCandidatesOverride = null,
    string? AiAssignmentWeightsJsonOverride = null, string? AiAssignmentInstructionsOverride = null,
    bool? AiAssignmentUseAffinityOverride = null, int? AiAssignmentMaxOutputTokensOverride = null,
    // Overrides dos 8 campos de aprendizado (aditivo; null = herda).
    int? AiSkillLearningModeOverride = null, int? AiSkillMinEvidenceOverride = null,
    int? AiSkillMaxTagsOverride = null, int? AiWeightLearningModeOverride = null,
    decimal? AiWeightMaxDeltaPerCycleOverride = null, int? AiWeightCycleDaysOverride = null,
    decimal? AiWeightMinOverride = null, decimal? AiWeightMaxOverride = null
);
