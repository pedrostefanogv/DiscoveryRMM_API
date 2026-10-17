using Discovery.Core.Entities;

namespace Discovery.Core.Departments;

/// <summary>Origem de um campo da configuração efetiva de triagem.</summary>
public enum DepartmentSettingOrigin
{
    /// <summary>Valor base/legado do próprio registro (sem override e sem pai).</summary>
    Default = 0,

    /// <summary>Override definido no próprio departamento.</summary>
    Own = 1,

    /// <summary>Valor herdado do departamento GLOBAL pai (um salto).</summary>
    InheritedGlobal = 2
}

/// <summary>Valor efetivo de um campo + de onde ele veio (para o console/auditoria).</summary>
public sealed record DepartmentEffectiveSetting<T>(
    T Value,
    DepartmentSettingOrigin Origin,
    Guid? SourceDepartmentId);

/// <summary>
/// Configuração efetiva de triagem do departamento, campo a campo.
///
/// Precedência: override do próprio > valor do pai global > valor base/legado do
/// próprio. Departamento global NUNCA herda. Um pai não global ou inativo é
/// ignorado defensivamente (dados legados), como na leitura da equipe.
/// </summary>
public sealed record DepartmentEffectiveSettings(
    DepartmentEffectiveSetting<int> AiAssignmentMode,
    DepartmentEffectiveSetting<double> AiAssignmentMinConfidence,
    DepartmentEffectiveSetting<int> AiAssignmentFallbackStrategy,
    DepartmentEffectiveSetting<int> AiAssignmentMaxCandidates,
    DepartmentEffectiveSetting<string?> AiAssignmentInstructions,
    DepartmentEffectiveSetting<string?> AiAssignmentWeightsJson,
    DepartmentEffectiveSetting<bool> AiAssignmentUseAffinity,
    DepartmentEffectiveSetting<int> AiAssignmentMaxOutputTokens,
    DepartmentEffectiveSetting<int> AiSkillLearningMode,
    DepartmentEffectiveSetting<int> AiSkillMinEvidence,
    DepartmentEffectiveSetting<int> AiSkillMaxTags,
    DepartmentEffectiveSetting<int> AiWeightLearningMode,
    DepartmentEffectiveSetting<decimal> AiWeightMaxDeltaPerCycle,
    DepartmentEffectiveSetting<int> AiWeightCycleDays,
    DepartmentEffectiveSetting<decimal> AiWeightMin,
    DepartmentEffectiveSetting<decimal> AiWeightMax);

/// <summary>
/// Resolvedor PURO (sem I/O) da configuração efetiva de triagem. O chamador
/// carrega o departamento e, quando houver vínculo, o pai global; a composição
/// campo a campo fica aqui para ser reutilizada pela triagem, pelo endpoint
/// /effective e pelos testes.
/// </summary>
public static class DepartmentEffectiveSettingsResolver
{
    /// <summary>
    /// Resolve a configuração efetiva. <paramref name="globalParent"/> só é
    /// considerado quando o departamento é de cliente, tem vínculo, e o pai é
    /// global/ativo — qualquer outro caso é ignorado (leitura defensiva).
    /// </summary>
    public static DepartmentEffectiveSettings Resolve(Department department, Department? globalParent)
    {
        var parent = IsUsableParent(department, globalParent) ? globalParent : null;
        var ownId = department.Id;
        var parentId = parent?.Id;

        return new DepartmentEffectiveSettings(
            Int(department.AiAssignmentModeOverride, department.AiAssignmentMode,
                parent, parent?.AiAssignmentModeOverride, parent?.AiAssignmentMode ?? 0, ownId, parentId),
            Double(department.AiAssignmentMinConfidenceOverride, department.AiAssignmentMinConfidence,
                parent, parent?.AiAssignmentMinConfidenceOverride, parent?.AiAssignmentMinConfidence ?? 0.60,
                ownId, parentId),
            Int(department.AiAssignmentFallbackStrategyOverride, department.AiAssignmentFallbackStrategy,
                parent, parent?.AiAssignmentFallbackStrategyOverride, parent?.AiAssignmentFallbackStrategy ?? 1,
                ownId, parentId),
            Int(department.AiAssignmentMaxCandidatesOverride, department.AiAssignmentMaxCandidates,
                parent, parent?.AiAssignmentMaxCandidatesOverride, parent?.AiAssignmentMaxCandidates ?? 8,
                ownId, parentId),
            Text(department.AiAssignmentInstructionsOverride, department.AiAssignmentInstructions,
                parent, parent?.AiAssignmentInstructionsOverride, parent?.AiAssignmentInstructions,
                ownId, parentId),
            Text(department.AiAssignmentWeightsJsonOverride, department.AiAssignmentWeightsJson,
                parent, parent?.AiAssignmentWeightsJsonOverride, parent?.AiAssignmentWeightsJson,
                ownId, parentId),
            Bool(department.AiAssignmentUseAffinityOverride, department.AiAssignmentUseAffinity,
                parent, parent?.AiAssignmentUseAffinityOverride, parent?.AiAssignmentUseAffinity ?? true,
                ownId, parentId),
            Int(department.AiAssignmentMaxOutputTokensOverride, department.AiAssignmentMaxOutputTokens,
                parent, parent?.AiAssignmentMaxOutputTokensOverride, parent?.AiAssignmentMaxOutputTokens ?? 1200,
                ownId, parentId),
            Int(department.AiSkillLearningModeOverride, department.AiSkillLearningMode,
                parent, parent?.AiSkillLearningModeOverride, parent?.AiSkillLearningMode ?? 1,
                ownId, parentId),
            Int(department.AiSkillMinEvidenceOverride, department.AiSkillMinEvidence,
                parent, parent?.AiSkillMinEvidenceOverride, parent?.AiSkillMinEvidence ?? 3,
                ownId, parentId),
            Int(department.AiSkillMaxTagsOverride, department.AiSkillMaxTags,
                parent, parent?.AiSkillMaxTagsOverride, parent?.AiSkillMaxTags ?? 12,
                ownId, parentId),
            Int(department.AiWeightLearningModeOverride, department.AiWeightLearningMode,
                parent, parent?.AiWeightLearningModeOverride, parent?.AiWeightLearningMode ?? 1,
                ownId, parentId),
            Decimal(department.AiWeightMaxDeltaPerCycleOverride, department.AiWeightMaxDeltaPerCycle,
                parent, parent?.AiWeightMaxDeltaPerCycleOverride, parent?.AiWeightMaxDeltaPerCycle ?? 0.10m,
                ownId, parentId),
            Int(department.AiWeightCycleDaysOverride, department.AiWeightCycleDays,
                parent, parent?.AiWeightCycleDaysOverride, parent?.AiWeightCycleDays ?? 7,
                ownId, parentId),
            Decimal(department.AiWeightMinOverride, department.AiWeightMin,
                parent, parent?.AiWeightMinOverride, parent?.AiWeightMin ?? 0.05m,
                ownId, parentId),
            Decimal(department.AiWeightMaxOverride, department.AiWeightMax,
                parent, parent?.AiWeightMaxOverride, parent?.AiWeightMax ?? 0.50m,
                ownId, parentId));
    }

    /// <summary>Global não herda (parent ignorado) e o pai só vale se global e ativo.</summary>
    public static bool IsUsableParent(Department department, Department? parent)
        => department.ClientId is not null
           && department.InheritFromGlobalId is not null
           && parent is not null
           && parent.ClientId is null
           && parent.IsActive
           && parent.Id == department.InheritFromGlobalId.Value
           && parent.Id != department.Id;

    private static DepartmentEffectiveSetting<int> Int(
        int? ownOverride, int ownBase, Department? parent,
        int? parentOverride, int parentBase, Guid ownId, Guid? parentId)
    {
        if (ownOverride.HasValue)
            return new(ownOverride.Value, DepartmentSettingOrigin.Own, ownId);
        if (parent is not null)
            return new(parentOverride ?? parentBase, DepartmentSettingOrigin.InheritedGlobal, parentId);
        return new(ownBase, DepartmentSettingOrigin.Default, ownId);
    }

    private static DepartmentEffectiveSetting<double> Double(
        double? ownOverride, double ownBase, Department? parent,
        double? parentOverride, double parentBase, Guid ownId, Guid? parentId)
    {
        if (ownOverride.HasValue)
            return new(ownOverride.Value, DepartmentSettingOrigin.Own, ownId);
        if (parent is not null)
            return new(parentOverride ?? parentBase, DepartmentSettingOrigin.InheritedGlobal, parentId);
        return new(ownBase, DepartmentSettingOrigin.Default, ownId);
    }

    private static DepartmentEffectiveSetting<decimal> Decimal(
        decimal? ownOverride, decimal ownBase, Department? parent,
        decimal? parentOverride, decimal parentBase, Guid ownId, Guid? parentId)
    {
        if (ownOverride.HasValue)
            return new(ownOverride.Value, DepartmentSettingOrigin.Own, ownId);
        if (parent is not null)
            return new(parentOverride ?? parentBase, DepartmentSettingOrigin.InheritedGlobal, parentId);
        return new(ownBase, DepartmentSettingOrigin.Default, ownId);
    }

    private static DepartmentEffectiveSetting<bool> Bool(
        bool? ownOverride, bool ownBase, Department? parent,
        bool? parentOverride, bool parentBase, Guid ownId, Guid? parentId)
    {
        if (ownOverride.HasValue)
            return new(ownOverride.Value, DepartmentSettingOrigin.Own, ownId);
        if (parent is not null)
            return new(parentOverride ?? parentBase, DepartmentSettingOrigin.InheritedGlobal, parentId);
        return new(ownBase, DepartmentSettingOrigin.Default, ownId);
    }

    private static DepartmentEffectiveSetting<string?> Text(
        string? ownOverride, string? ownBase, Department? parent,
        string? parentOverride, string? parentBase, Guid ownId, Guid? parentId)
    {
        if (ownOverride is not null)
            return new(ownOverride, DepartmentSettingOrigin.Own, ownId);
        if (parent is not null)
            return new(parentOverride ?? parentBase, DepartmentSettingOrigin.InheritedGlobal, parentId);
        return new(ownBase, DepartmentSettingOrigin.Default, ownId);
    }
}
