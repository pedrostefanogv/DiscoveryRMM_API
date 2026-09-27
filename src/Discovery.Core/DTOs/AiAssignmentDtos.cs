namespace Discovery.Core.DTOs;

// ── Configuração da triagem por IA (departamento) ────────────────────────

/// <summary>Pesos do score determinístico (0..1 cada). Defaults conservadores.</summary>
public sealed record AiAssignmentWeightsDto(
    double Skill = 0.25,
    double Affinity = 0.25,
    double Performance = 0.20,
    double Load = 0.15,
    double Csat = 0.10,
    double SlaQuality = 0.05);

public sealed record AiAssignmentSettingsDto(
    int Mode,
    double MinConfidence,
    int FallbackStrategy,
    int MaxCandidates,
    bool UseAffinity,
    string? Instructions,
    AiAssignmentWeightsDto Weights,
    int MaxOutputTokens = 1200,
    int SkillLearningMode = 1,
    int SkillMinEvidence = 3,
    int SkillMaxTags = 12,
    int WeightLearningMode = 1,
    double WeightMaxDeltaPerCycle = 0.10,
    int WeightCycleDays = 7,
    double WeightMin = 0.05,
    double WeightMax = 0.50);

// ── Métricas por atendente ───────────────────────────────────────────────

public sealed record TechnicianMetricsDto(
    Guid UserId,
    int WindowDays,
    int AssignedTotal,
    int ResolvedTotal,
    int OpenNow,
    double? AvgFirstResponseMinutes,
    double? AvgResolutionMinutes,
    double? P90ResolutionMinutes,
    double SlaBreachRate,
    double ReopenRate,
    double? CsatAverage,
    int CsatRatedCount,
    double? DifficultyAverage,
    IReadOnlyList<string> TopCategories,
    IReadOnlyList<string> TopTags,
    DateTime? ComputedAt);

/// <summary>Perfil do membro (competências/capacidade) + métricas, usado na tela do departamento.</summary>
public sealed record DepartmentMemberProfileDto(
    Guid DepartmentId,
    Guid UserId,
    string? UserName,
    bool IsActive,
    DateTime CreatedAt,
    IReadOnlyList<string> SkillTags,
    int SkillLevel,
    int? MaxOpenTickets,
    decimal Weight,
    bool AcceptsAiAssignment,
    TechnicianMetricsDto? Metrics);

public sealed record UpdateDepartmentMemberProfileRequest(
    IReadOnlyList<string>? SkillTags = null,
    int? SkillLevel = null,
    int? MaxOpenTickets = null,
    decimal? Weight = null,
    bool? AcceptsAiAssignment = null,
    bool ClearMaxOpenTickets = false);

// ── Dificuldade e candidatos ─────────────────────────────────────────────

public sealed record TicketDifficultyDto(int Level, string Source, string? Rationale);

public sealed record AssignmentCandidateDto(
    Guid UserId,
    string? UserName,
    double Score,
    double SkillScore,
    double AffinityScore,
    double PerformanceScore,
    double LoadScore,
    double CsatScore,
    double SlaScore,
    bool OverCapacity,
    int OpenNow,
    string? BestAffinityTicketTitle);

// ── Decisão ──────────────────────────────────────────────────────────────

public sealed record TicketAssignmentDecisionDto(
    Guid Id,
    Guid TicketId,
    Guid DepartmentId,
    int Mode,
    string StrategySource,
    int Difficulty,
    Guid? ChosenUserId,
    string? ChosenUserName,
    double Confidence,
    double Score,
    string? Rationale,
    string? Model,
    int TokensUsed,
    bool Applied,
    string? NotAppliedReason,
    DateTime? OverriddenAt,
    Guid? OverriddenByUserId,
    DateTime CreatedAt,
    IReadOnlyList<AssignmentCandidateDto> Candidates,
    int MaxOutputTokens = 0,
    int PromptChars = 0);

// ── Aprendizado (competências e pesos) ──────────────────────────────────

public sealed record TechnicianSkillSuggestionDto(
    Guid Id,
    Guid DepartmentId,
    Guid UserId,
    string? UserName,
    int WindowDays,
    IReadOnlyList<string> SuggestedTags,
    IReadOnlyList<string> AppliedTags,
    string? EvidenceJson,
    string Status,
    bool AutoApplied,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    Guid? DecidedByUserId);

public sealed record AiWeightSuggestionDto(
    Guid Id,
    Guid DepartmentId,
    int CycleDays,
    DateTime WindowStart,
    DateTime WindowEnd,
    AiAssignmentWeightsDto CurrentWeights,
    AiAssignmentWeightsDto SuggestedWeights,
    string? EvidenceJson,
    string Status,
    bool AutoApplied,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    Guid? DecidedByUserId);

public sealed record DepartmentLearningSuggestionsDto(
    IReadOnlyList<TechnicianSkillSuggestionDto> Skills,
    IReadOnlyList<AiWeightSuggestionDto> Weights);

/// <summary>Resultado de uma execução da triagem (preview/aplicação).</summary>
public sealed record TicketAssignmentResultDto(
    TicketAssignmentDecisionDto Decision,
    Guid? AssignedUserId,
    bool Applied,
    string Result);
