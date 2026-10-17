using Discovery.Core.Cqrs;

namespace Discovery.Core.Cqrs.Departments.Queries;

/// <summary>
/// Valor efetivo de um campo da triagem + origem ("own", "inherited-global",
/// "default") para o console exibir o selo de herdado.
/// </summary>
public sealed record DepartmentEffectiveSettingDto<T>(
    T Value,
    string Origin,
    Guid? SourceDepartmentId);

/// <summary>
/// Membro da equipe efetiva do departamento: próprios + herdados do global pai.
/// <paramref name="Inherited"/> é true quando o vínculo vem do global pai;
/// <paramref name="Origin"/> é "own" ou "inherited-global".
/// </summary>
public sealed record DepartmentEffectiveMemberDto(
    Guid UserId,
    string? UserName,
    bool IsActive,
    bool Inherited,
    string Origin,
    Guid? SourceDepartmentId,
    string? SourceDepartmentName);

/// <summary>
/// Configuração efetiva do departamento (GET /departments/{id}/effective).
///
/// Shape ADITIVO: os 16 campos flat (<see cref="AiAssignmentMode"/>, ...,
/// <see cref="AiWeightMax"/>) continuam para compatibilidade; o console novo
/// consome também <see cref="Config"/> (16 valores camelCase),
/// <see cref="Origins"/> (origem por campo como string), <see cref="Team"/>
/// (equipe efetiva), <see cref="Warnings"/> e os metadados de herança.
/// </summary>
public sealed record DepartmentEffectiveConfigDto
{
    public Guid DepartmentId { get; init; }
    public Guid? ClientId { get; init; }
    public Guid? InheritFromGlobalId { get; init; }
    public Guid? GlobalParentId { get; init; }

    /// <summary>Nome do departamento GLOBAL pai quando o vínculo é utilizável.</summary>
    public string? InheritFromGlobalName { get; init; }

    /// <summary>Momento (UTC) em que a configuração foi resolvida.</summary>
    public DateTime ResolvedAt { get; init; }

    // ── 16 campos flat (compatibilidade com o contrato anterior) ─────────
    public DepartmentEffectiveSettingDto<int> AiAssignmentMode { get; init; } = null!;
    public DepartmentEffectiveSettingDto<double> AiAssignmentMinConfidence { get; init; } = null!;
    public DepartmentEffectiveSettingDto<int> AiAssignmentFallbackStrategy { get; init; } = null!;
    public DepartmentEffectiveSettingDto<int> AiAssignmentMaxCandidates { get; init; } = null!;
    public DepartmentEffectiveSettingDto<string?> AiAssignmentInstructions { get; init; } = null!;
    public DepartmentEffectiveSettingDto<string?> AiAssignmentWeightsJson { get; init; } = null!;
    public DepartmentEffectiveSettingDto<bool> AiAssignmentUseAffinity { get; init; } = null!;
    public DepartmentEffectiveSettingDto<int> AiAssignmentMaxOutputTokens { get; init; } = null!;
    public DepartmentEffectiveSettingDto<int> AiSkillLearningMode { get; init; } = null!;
    public DepartmentEffectiveSettingDto<int> AiSkillMinEvidence { get; init; } = null!;
    public DepartmentEffectiveSettingDto<int> AiSkillMaxTags { get; init; } = null!;
    public DepartmentEffectiveSettingDto<int> AiWeightLearningMode { get; init; } = null!;
    public DepartmentEffectiveSettingDto<decimal> AiWeightMaxDeltaPerCycle { get; init; } = null!;
    public DepartmentEffectiveSettingDto<int> AiWeightCycleDays { get; init; } = null!;
    public DepartmentEffectiveSettingDto<decimal> AiWeightMin { get; init; } = null!;
    public DepartmentEffectiveSettingDto<decimal> AiWeightMax { get; init; } = null!;

    // ── Shape novo consumido pelo console ────────────────────────────────
    /// <summary>16 valores efetivos em camelCase (chaves iguais aos nomes do PUT).</summary>
    public IReadOnlyDictionary<string, object?> Config { get; init; }
        = new Dictionary<string, object?>();

    /// <summary>Origem por campo: "own", "inherited-global" ou "default".</summary>
    public IReadOnlyDictionary<string, string> Origins { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Equipe efetiva: próprios primeiro e depois herdados do global pai.</summary>
    public IReadOnlyList<DepartmentEffectiveMemberDto> Team { get; init; } = [];

    /// <summary>Avisos de herança (pai inativo/não global, ciclo, pai ausente).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record GetDepartmentEffectiveConfigQuery(Guid Id)
    : IQuery<Result<DepartmentEffectiveConfigDto>>;
