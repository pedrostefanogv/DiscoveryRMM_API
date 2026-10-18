using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Departments.Commands;
using Discovery.Core.Cqrs.Departments.Queries;
using Discovery.Core.Departments;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Departments;

/// <summary>
/// Invariantes da configuração de triagem por IA do departamento. Ficam aqui
/// (e não no handler) para serem reutilizadas por comandos e testes.
/// </summary>
internal static class DepartmentAiSettingsValidator
{
    public static Error? Validate(
        int assignmentStrategy, int mode, double minConfidence,
        int fallbackStrategy, int maxCandidates)
    {
        // Faixa do enum TicketAssignmentStrategy: 0=None, 1=RoundRobin,
        // 2=LeastOpenTickets, 3=AiTriage. Sem esta checagem um valor arbitrário
        // (ex.: 99) era persistido e a atribuição ficava silenciosamente sem efeito.
        if (assignmentStrategy is < 0 or > 3)
            return Error.Validation(
                "AssignmentStrategy",
                "Estratégia de auto-atribuição inválida (0=Nenhuma, 1=Round-robin, 2=Menos chamados abertos, 3=Triagem por IA).");

        if (mode is not ((int)AiAssignmentMode.Suggest) and not ((int)AiAssignmentMode.AutoAssign))
            return Error.Validation("AiAssignmentMode", "Modo da triagem por IA inválido (0=Sugerir, 1=Atribuir automaticamente).");

        if (minConfidence < 0 || minConfidence > 1)
            return Error.Validation("AiAssignmentMinConfidence", "Confiança mínima da IA deve estar entre 0 e 1.");

        if (maxCandidates is < 1 or > 20)
            return Error.Validation("AiAssignmentMaxCandidates", "Número máximo de candidatos deve estar entre 1 e 20.");

        if (assignmentStrategy == (int)TicketAssignmentStrategy.AiTriage
            && fallbackStrategy is not ((int)TicketAssignmentStrategy.RoundRobin)
            && fallbackStrategy is not ((int)TicketAssignmentStrategy.LeastOpenTickets))
        {
            return Error.Validation(
                "AiAssignmentFallbackStrategy",
                "Fallback da triagem por IA deve ser RoundRobin (1) ou LeastOpenTickets (2).");
        }

        return null;
    }

    /// <summary>Invariantes do orçamento de tokens e do aprendizado híbrido.</summary>
    public static Error? ValidateLearning(
        int maxOutputTokens, int skillLearningMode, int skillMinEvidence, int skillMaxTags,
        int weightLearningMode, decimal weightMaxDelta, int weightCycleDays,
        decimal weightMin, decimal weightMax)
    {
        if (maxOutputTokens is < 200 or > 32768)
            return Error.Validation("AiAssignmentMaxOutputTokens", "Teto de tokens da triagem deve estar entre 200 e 32768.");

        if (!IsLearningMode(skillLearningMode))
            return Error.Validation("AiSkillLearningMode", "Modo do aprendizado de competências inválido (0=Off, 1=Sugerir, 2=Automático).");

        if (!IsLearningMode(weightLearningMode))
            return Error.Validation("AiWeightLearningMode", "Modo da recalibração de pesos inválido (0=Off, 1=Sugerir, 2=Automático).");

        if (skillMinEvidence is < 1 or > 100)
            return Error.Validation("AiSkillMinEvidence", "Evidência mínima de competência deve estar entre 1 e 100 chamados.");

        if (skillMaxTags is < 1 or > 30)
            return Error.Validation("AiSkillMaxTags", "Máximo de competências por atendente deve estar entre 1 e 30.");

        if (weightMaxDelta is < 0.01m or > 0.50m)
            return Error.Validation("AiWeightMaxDeltaPerCycle", "Ajuste máximo por ciclo deve estar entre 0,01 e 0,50.");

        if (weightCycleDays is < 1 or > 90)
            return Error.Validation("AiWeightCycleDays", "Ciclo de calibração deve estar entre 1 e 90 dias.");

        if (weightMin < 0.01m || weightMax > 1.0m || weightMin >= weightMax)
            return Error.Validation("AiWeightMin", "Faixa de pesos inválida: min < max, ambos entre 0,01 e 1,0.");

        return null;
    }

    private static bool IsLearningMode(int value) => value is 0 or 1 or 2;
}

public sealed class CreateDepartmentCommandHandler(
    IDepartmentService service,
    IConfigurationAuditService? audit = null
) : IRequestHandler<CreateDepartmentCommand, Result<DepartmentDto>>
{
    public async Task<Result<DepartmentDto>> Handle(CreateDepartmentCommand cmd, CancellationToken ct)
    {
        var validation = DepartmentAiSettingsValidator.Validate(
            cmd.AssignmentStrategy, cmd.AiAssignmentMode, cmd.AiAssignmentMinConfidence,
            cmd.AiAssignmentFallbackStrategy, cmd.AiAssignmentMaxCandidates)
            ?? DepartmentAiSettingsValidator.ValidateLearning(
                cmd.AiAssignmentMaxOutputTokens, cmd.AiSkillLearningMode, cmd.AiSkillMinEvidence,
                cmd.AiSkillMaxTags, cmd.AiWeightLearningMode, cmd.AiWeightMaxDeltaPerCycle,
                cmd.AiWeightCycleDays, cmd.AiWeightMin, cmd.AiWeightMax);
        if (validation is not null)
            return Result<DepartmentDto>.Failure(validation);

        var inheritance = await DepartmentInheritanceValidator.ValidateAsync(
            departmentId: null,
            cmd.InheritFromGlobalId,
            service.GetByIdAsync,
            ct);
        if (inheritance is not null)
            return Result<DepartmentDto>.Failure(inheritance);

        var dept = new Department
        {
            Name = cmd.Name,
            Description = cmd.Description,
            ClientId = cmd.ClientId,
            // Departamento global NUNCA herda: um vínculo informado no create é
            // descartado (a leitura já o ignorava; gravá-lo deixava dado inválido).
            InheritFromGlobalId = cmd.ClientId is null ? null : cmd.InheritFromGlobalId,
            SortOrder = cmd.SortOrder,
            IsActive = true,
            AssignmentStrategy = cmd.AssignmentStrategy,
            // Criação não grava override: departamento novo herda do pai ativo.
            AssignmentStrategyOverride = null,
            AiAssignmentMode = cmd.AiAssignmentMode,
            AiAssignmentMinConfidence = cmd.AiAssignmentMinConfidence,
            AiAssignmentFallbackStrategy = cmd.AiAssignmentFallbackStrategy,
            AiAssignmentMaxCandidates = cmd.AiAssignmentMaxCandidates,
            AiAssignmentWeightsJson = cmd.AiAssignmentWeightsJson,
            AiAssignmentInstructions = cmd.AiAssignmentInstructions,
            AiAssignmentUseAffinity = cmd.AiAssignmentUseAffinity,
            AiAssignmentMaxOutputTokens = cmd.AiAssignmentMaxOutputTokens,
            AiSkillLearningMode = cmd.AiSkillLearningMode,
            AiSkillMinEvidence = cmd.AiSkillMinEvidence,
            AiSkillMaxTags = cmd.AiSkillMaxTags,
            AiWeightLearningMode = cmd.AiWeightLearningMode,
            AiWeightMaxDeltaPerCycle = cmd.AiWeightMaxDeltaPerCycle,
            AiWeightCycleDays = cmd.AiWeightCycleDays,
            AiWeightMin = cmd.AiWeightMin,
            AiWeightMax = cmd.AiWeightMax,
            // Criação NÃO grava overrides (nenhum dos 16): o departamento novo
            // herda do global pai por padrão. Os valores acima ficam como
            // base/legado até o primeiro PUT explícito de cada campo.
            AiAssignmentModeOverride = null,
            AiAssignmentMinConfidenceOverride = null,
            AiAssignmentFallbackStrategyOverride = null,
            AiAssignmentMaxCandidatesOverride = null,
            AiAssignmentWeightsJsonOverride = null,
            AiAssignmentInstructionsOverride = null,
            AiAssignmentUseAffinityOverride = null,
            AiAssignmentMaxOutputTokensOverride = null,
            AiSkillLearningModeOverride = null,
            AiSkillMinEvidenceOverride = null,
            AiSkillMaxTagsOverride = null,
            AiWeightLearningModeOverride = null,
            AiWeightMaxDeltaPerCycleOverride = null,
            AiWeightCycleDaysOverride = null,
            AiWeightMinOverride = null,
            AiWeightMaxOverride = null
        };
        var created = await service.CreateAsync(dept, ct);

        if (audit is not null)
        {
            await audit.LogChangeAsync(
                "Department", created.Id, "InheritFromGlobalId",
                null, created.InheritFromGlobalId?.ToString(),
                reason: "department_create", changedBy: cmd.ChangedBy);
        }

        return Result<DepartmentDto>.Success(Map(created));
    }

    internal static DepartmentDto Map(Department d) => new(
        d.Id, d.ClientId, d.Name, d.Description, d.InheritFromGlobalId,
        d.SortOrder, d.IsActive, d.CreatedAt, d.UpdatedAt, d.AssignmentStrategy,
        d.AiAssignmentMode, d.AiAssignmentMinConfidence, d.AiAssignmentFallbackStrategy,
        d.AiAssignmentMaxCandidates, d.AiAssignmentWeightsJson, d.AiAssignmentInstructions,
        d.AiAssignmentUseAffinity, d.AiAssignmentMaxOutputTokens,
        d.AiSkillLearningMode, d.AiSkillMinEvidence, d.AiSkillMaxTags,
        d.AiWeightLearningMode, d.AiWeightMaxDeltaPerCycle, d.AiWeightCycleDays,
        d.AiWeightMin, d.AiWeightMax,
        d.AiAssignmentModeOverride, d.AiAssignmentMinConfidenceOverride,
        d.AiAssignmentFallbackStrategyOverride, d.AiAssignmentMaxCandidatesOverride,
        d.AiAssignmentWeightsJsonOverride, d.AiAssignmentInstructionsOverride,
        d.AiAssignmentUseAffinityOverride, d.AiAssignmentMaxOutputTokensOverride,
        d.AiSkillLearningModeOverride, d.AiSkillMinEvidenceOverride,
        d.AiSkillMaxTagsOverride, d.AiWeightLearningModeOverride,
        d.AiWeightMaxDeltaPerCycleOverride, d.AiWeightCycleDaysOverride,
        d.AiWeightMinOverride, d.AiWeightMaxOverride, d.AssignmentStrategyOverride);
}

public sealed class UpdateDepartmentCommandHandler(
    IDepartmentService service,
    IConfigurationAuditService? audit = null
) : IRequestHandler<UpdateDepartmentCommand, Result<DepartmentDto>>
{
    public async Task<Result<DepartmentDto>> Handle(UpdateDepartmentCommand cmd, CancellationToken ct)
    {
        var dept = await service.GetByIdAsync(cmd.Id, ct);
        if (dept is null)
            return Result<DepartmentDto>.Failure(Error.NotFound($"Department {cmd.Id} not found"));

        // D5 — patch semântico: só mexe no vínculo quando o campo veio no corpo;
        // Set(null) desfaz a herança. D1–D4 — o mesmo validador do create.
        // Departamento GLOBAL (ClientId null) nunca herda: o vínculo é ignorado
        // (normalizado para null abaixo) e não passa pelo validador.
        if (cmd.InheritFromGlobalId.IsSet && dept.ClientId is not null)
        {
            var inheritance = await DepartmentInheritanceValidator.ValidateAsync(
                dept.Id, cmd.InheritFromGlobalId.Value, service.GetByIdAsync, ct);
            if (inheritance is not null)
                return Result<DepartmentDto>.Failure(inheritance);
        }

        // D6 — desativar um pai com filhos ativos deixaria a leitura órfã.
        if (cmd.IsActive == false && dept.IsActive
            && await service.HasActiveChildrenAsync(dept.Id, ct))
        {
            return Result<DepartmentDto>.Failure(Error.Validation(
                "IsActive",
                "Existem departamentos ativos herdando deste; desative ou remova a herança dos filhos antes."));
        }

        var oldParent = dept.InheritFromGlobalId;
        var oldActive = dept.IsActive;
        var parentChanged = false;

        if (cmd.Name is not null) dept.Name = cmd.Name;
        if (cmd.Description is not null) dept.Description = cmd.Description;
        if (cmd.InheritFromGlobalId.IsSet)
        {
            dept.InheritFromGlobalId = dept.ClientId is null
                ? null
                : cmd.InheritFromGlobalId.Value;
            parentChanged = oldParent != dept.InheritFromGlobalId;
        }
        if (cmd.SortOrder.HasValue) dept.SortOrder = cmd.SortOrder.Value;
        if (cmd.IsActive.HasValue) dept.IsActive = cmd.IsActive.Value;

        // Estratégia de auto-atribuição com patch semântico (valor = próprio,
        // null explícito = volta a herdar do global pai, ausente = mantém).
        if (cmd.AssignmentStrategy.IsSet)
            ApplyInt(cmd.AssignmentStrategy, v => dept.AssignmentStrategy = v,
                () => dept.AssignmentStrategyOverride = null,
                v => dept.AssignmentStrategyOverride = v);

        // ── 16 campos com o NOME BASE (console) ──────────────────────────
        // Valor -> próprio (base + override). Null explícito -> limpa o
        // override e volta a herdar. Ausente -> não mexe em nada.
        if (cmd.AiAssignmentMode.IsSet)
            ApplyInt(cmd.AiAssignmentMode, v => dept.AiAssignmentMode = v,
                () => dept.AiAssignmentModeOverride = null, v => dept.AiAssignmentModeOverride = v);
        if (cmd.AiAssignmentMinConfidence.IsSet)
            ApplyDouble(cmd.AiAssignmentMinConfidence, v => dept.AiAssignmentMinConfidence = v,
                () => dept.AiAssignmentMinConfidenceOverride = null,
                v => dept.AiAssignmentMinConfidenceOverride = v);
        if (cmd.AiAssignmentFallbackStrategy.IsSet)
            ApplyInt(cmd.AiAssignmentFallbackStrategy, v => dept.AiAssignmentFallbackStrategy = v,
                () => dept.AiAssignmentFallbackStrategyOverride = null,
                v => dept.AiAssignmentFallbackStrategyOverride = v);
        if (cmd.AiAssignmentMaxCandidates.IsSet)
            ApplyInt(cmd.AiAssignmentMaxCandidates, v => dept.AiAssignmentMaxCandidates = v,
                () => dept.AiAssignmentMaxCandidatesOverride = null,
                v => dept.AiAssignmentMaxCandidatesOverride = v);
        if (cmd.AiAssignmentUseAffinity.IsSet)
            ApplyBool(cmd.AiAssignmentUseAffinity, v => dept.AiAssignmentUseAffinity = v,
                () => dept.AiAssignmentUseAffinityOverride = null,
                v => dept.AiAssignmentUseAffinityOverride = v);
        if (cmd.AiAssignmentMaxOutputTokens.IsSet)
            ApplyInt(cmd.AiAssignmentMaxOutputTokens, v => dept.AiAssignmentMaxOutputTokens = v,
                () => dept.AiAssignmentMaxOutputTokensOverride = null,
                v => dept.AiAssignmentMaxOutputTokensOverride = v);
        if (cmd.AiSkillLearningMode.IsSet)
            ApplyInt(cmd.AiSkillLearningMode, v => dept.AiSkillLearningMode = v,
                () => dept.AiSkillLearningModeOverride = null,
                v => dept.AiSkillLearningModeOverride = v);
        if (cmd.AiSkillMinEvidence.IsSet)
            ApplyInt(cmd.AiSkillMinEvidence, v => dept.AiSkillMinEvidence = v,
                () => dept.AiSkillMinEvidenceOverride = null,
                v => dept.AiSkillMinEvidenceOverride = v);
        if (cmd.AiSkillMaxTags.IsSet)
            ApplyInt(cmd.AiSkillMaxTags, v => dept.AiSkillMaxTags = v,
                () => dept.AiSkillMaxTagsOverride = null,
                v => dept.AiSkillMaxTagsOverride = v);
        if (cmd.AiWeightLearningMode.IsSet)
            ApplyInt(cmd.AiWeightLearningMode, v => dept.AiWeightLearningMode = v,
                () => dept.AiWeightLearningModeOverride = null,
                v => dept.AiWeightLearningModeOverride = v);
        if (cmd.AiWeightMaxDeltaPerCycle.IsSet)
            ApplyDecimal(cmd.AiWeightMaxDeltaPerCycle, v => dept.AiWeightMaxDeltaPerCycle = v,
                () => dept.AiWeightMaxDeltaPerCycleOverride = null,
                v => dept.AiWeightMaxDeltaPerCycleOverride = v);
        if (cmd.AiWeightCycleDays.IsSet)
            ApplyInt(cmd.AiWeightCycleDays, v => dept.AiWeightCycleDays = v,
                () => dept.AiWeightCycleDaysOverride = null,
                v => dept.AiWeightCycleDaysOverride = v);
        if (cmd.AiWeightMin.IsSet)
            ApplyDecimal(cmd.AiWeightMin, v => dept.AiWeightMin = v,
                () => dept.AiWeightMinOverride = null,
                v => dept.AiWeightMinOverride = v);
        if (cmd.AiWeightMax.IsSet)
            ApplyDecimal(cmd.AiWeightMax, v => dept.AiWeightMax = v,
                () => dept.AiWeightMaxOverride = null,
                v => dept.AiWeightMaxOverride = v);
        if (cmd.AiAssignmentWeightsJson.IsSet)
            ApplyText(cmd.AiAssignmentWeightsJson, v => dept.AiAssignmentWeightsJson = v,
                () => dept.AiAssignmentWeightsJsonOverride = null,
                v => dept.AiAssignmentWeightsJsonOverride = v);
        if (cmd.AiAssignmentInstructions.IsSet)
            ApplyText(cmd.AiAssignmentInstructions, v => dept.AiAssignmentInstructions = v,
                () => dept.AiAssignmentInstructionsOverride = null,
                v => dept.AiAssignmentInstructionsOverride = v);

        // ── Aliases "*Override" (mesmas colunas; o alias vence) ─────────
        if (cmd.AssignmentStrategyOverride.IsSet) dept.AssignmentStrategyOverride = cmd.AssignmentStrategyOverride.Value;
        if (cmd.AiAssignmentModeOverride.IsSet) dept.AiAssignmentModeOverride = cmd.AiAssignmentModeOverride.Value;
        if (cmd.AiAssignmentMinConfidenceOverride.IsSet) dept.AiAssignmentMinConfidenceOverride = cmd.AiAssignmentMinConfidenceOverride.Value;
        if (cmd.AiAssignmentFallbackStrategyOverride.IsSet) dept.AiAssignmentFallbackStrategyOverride = cmd.AiAssignmentFallbackStrategyOverride.Value;
        if (cmd.AiAssignmentMaxCandidatesOverride.IsSet) dept.AiAssignmentMaxCandidatesOverride = cmd.AiAssignmentMaxCandidatesOverride.Value;
        if (cmd.AiAssignmentUseAffinityOverride.IsSet) dept.AiAssignmentUseAffinityOverride = cmd.AiAssignmentUseAffinityOverride.Value;
        if (cmd.AiAssignmentMaxOutputTokensOverride.IsSet) dept.AiAssignmentMaxOutputTokensOverride = cmd.AiAssignmentMaxOutputTokensOverride.Value;
        if (cmd.AiAssignmentWeightsJsonOverride.IsSet)
            dept.AiAssignmentWeightsJsonOverride = string.IsNullOrWhiteSpace(cmd.AiAssignmentWeightsJsonOverride.Value)
                ? null : cmd.AiAssignmentWeightsJsonOverride.Value;
        if (cmd.AiAssignmentInstructionsOverride.IsSet)
            dept.AiAssignmentInstructionsOverride = string.IsNullOrWhiteSpace(cmd.AiAssignmentInstructionsOverride.Value)
                ? null : cmd.AiAssignmentInstructionsOverride.Value;
        if (cmd.AiSkillLearningModeOverride.IsSet) dept.AiSkillLearningModeOverride = cmd.AiSkillLearningModeOverride.Value;
        if (cmd.AiSkillMinEvidenceOverride.IsSet) dept.AiSkillMinEvidenceOverride = cmd.AiSkillMinEvidenceOverride.Value;
        if (cmd.AiSkillMaxTagsOverride.IsSet) dept.AiSkillMaxTagsOverride = cmd.AiSkillMaxTagsOverride.Value;
        if (cmd.AiWeightLearningModeOverride.IsSet) dept.AiWeightLearningModeOverride = cmd.AiWeightLearningModeOverride.Value;
        if (cmd.AiWeightMaxDeltaPerCycleOverride.IsSet) dept.AiWeightMaxDeltaPerCycleOverride = cmd.AiWeightMaxDeltaPerCycleOverride.Value;
        if (cmd.AiWeightCycleDaysOverride.IsSet) dept.AiWeightCycleDaysOverride = cmd.AiWeightCycleDaysOverride.Value;
        if (cmd.AiWeightMinOverride.IsSet) dept.AiWeightMinOverride = cmd.AiWeightMinOverride.Value;
        if (cmd.AiWeightMaxOverride.IsSet) dept.AiWeightMaxOverride = cmd.AiWeightMaxOverride.Value;

        // Valida o que será EFETIVO (próprio ou herdado), não apenas a coluna base.
        var validationParent = await LoadUsableParentAsync(service, dept, ct);
        var effective = DepartmentEffectiveSettingsResolver.Resolve(dept, validationParent);
        var validation = DepartmentAiSettingsValidator.Validate(
            effective.AssignmentStrategy.Value, effective.AiAssignmentMode.Value,
            effective.AiAssignmentMinConfidence.Value,
            effective.AiAssignmentFallbackStrategy.Value, effective.AiAssignmentMaxCandidates.Value)
            ?? DepartmentAiSettingsValidator.ValidateLearning(
                effective.AiAssignmentMaxOutputTokens.Value,
                effective.AiSkillLearningMode.Value, effective.AiSkillMinEvidence.Value,
                effective.AiSkillMaxTags.Value, effective.AiWeightLearningMode.Value,
                effective.AiWeightMaxDeltaPerCycle.Value, effective.AiWeightCycleDays.Value,
                effective.AiWeightMin.Value, effective.AiWeightMax.Value);
        if (validation is not null)
            return Result<DepartmentDto>.Failure(validation);

        var updated = await service.UpdateAsync(dept, ct);

        if (audit is not null)
        {
            if (parentChanged)
            {
                await audit.LogChangeAsync(
                    "Department", updated.Id, "InheritFromGlobalId",
                    oldParent?.ToString(), updated.InheritFromGlobalId?.ToString(),
                    reason: "department_update", changedBy: cmd.ChangedBy);
            }

            // B1 — a transição de IsActive é auditada nos DOIS sentidos, com
            // antes/depois explícitos (antes só o desligamento era registrado).
            if (oldActive != updated.IsActive)
            {
                await audit.LogChangeAsync(
                    "Department", updated.Id, "IsActive",
                    oldActive ? "true" : "false",
                    updated.IsActive ? "true" : "false",
                    reason: updated.IsActive ? "department_activate" : "department_deactivate",
                    changedBy: cmd.ChangedBy);
            }
        }

        return Result<DepartmentDto>.Success(CreateDepartmentCommandHandler.Map(updated));
    }

    private static async Task<Department?> LoadUsableParentAsync(
        IDepartmentService service, Department department, CancellationToken ct)
    {
        if (department.ClientId is null || department.InheritFromGlobalId is not { } parentId)
            return null;

        if (parentId == department.Id)
            return null;

        var parent = await service.GetByIdAsync(parentId, ct);
        return parent is not null && parent.ClientId is null && parent.IsActive ? parent : null;
    }

    private static void ApplyInt(
        OptionalValue<int> patch, Action<int> setBase, Action clearOverride, Action<int> setOverride)
    {
        if (patch.Value is { } value)
        {
            setBase(value);
            setOverride(value);
        }
        else
        {
            clearOverride();
        }
    }

    private static void ApplyDouble(
        OptionalValue<double> patch, Action<double> setBase, Action clearOverride, Action<double> setOverride)
    {
        if (patch.Value is { } value)
        {
            setBase(value);
            setOverride(value);
        }
        else
        {
            clearOverride();
        }
    }

    private static void ApplyDecimal(
        OptionalValue<decimal> patch, Action<decimal> setBase, Action clearOverride, Action<decimal> setOverride)
    {
        if (patch.Value is { } value)
        {
            setBase(value);
            setOverride(value);
        }
        else
        {
            clearOverride();
        }
    }

    private static void ApplyBool(
        OptionalValue<bool> patch, Action<bool> setBase, Action clearOverride, Action<bool> setOverride)
    {
        if (patch.Value is { } value)
        {
            setBase(value);
            setOverride(value);
        }
        else
        {
            clearOverride();
        }
    }

    private static void ApplyText(
        OptionalText patch, Action<string?> setBase, Action clearOverride, Action<string?> setOverride)
    {
        // String vazia/branca equivale a null (= herdar), como no contrato antigo.
        var value = string.IsNullOrWhiteSpace(patch.Value) ? null : patch.Value;
        if (value is not null)
        {
            setBase(value);
            setOverride(value);
        }
        else
        {
            clearOverride();
        }
    }
}

public sealed class DeleteDepartmentCommandHandler(
    IDepartmentService service,
    IConfigurationAuditService? audit = null
) : IRequestHandler<DeleteDepartmentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteDepartmentCommand cmd, CancellationToken ct)
    {
        var dept = await service.GetByIdAsync(cmd.Id, ct);
        if (dept is null)
            return Result<VoidResult>.Failure(Error.NotFound($"Department {cmd.Id} not found"));

        // D6 — soft delete não pode deixar filhos ativos apontando para um pai inativo.
        if (dept.IsActive && await service.HasActiveChildrenAsync(cmd.Id, ct))
        {
            return Result<VoidResult>.Failure(Error.Validation(
                "Children",
                "Existem departamentos ativos herdando deste; remova a herança dos filhos antes de excluir."));
        }

        var deleted = await service.DeleteAsync(cmd.Id, ct);
        if (!deleted)
            return Result<VoidResult>.Failure(Error.NotFound($"Department {cmd.Id} not found"));

        if (audit is not null)
        {
            await audit.LogChangeAsync(
                "Department", cmd.Id, "InheritFromGlobalId",
                dept.InheritFromGlobalId?.ToString(), null,
                reason: "department_delete", changedBy: cmd.ChangedBy);
            if (dept.IsActive)
            {
                await audit.LogChangeAsync(
                    "Department", cmd.Id, "IsActive", "true", "false",
                    reason: "department_delete", changedBy: cmd.ChangedBy);
            }
        }

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ListDepartmentsQueryHandler(
    IDepartmentService service
) : IRequestHandler<ListDepartmentsQuery, Result<IReadOnlyList<DepartmentDto>>>
{
    public async Task<Result<IReadOnlyList<DepartmentDto>>> Handle(ListDepartmentsQuery q, CancellationToken ct)
    {
        // D11 — sem clientId a listagem devolvia só os globais; o filtro de
        // escopo "todos" do console pede explicitamente a união de todos.
        var deps = q.AllClients
            ? await service.GetAllAsync(ct)
            : q.ClientId.HasValue
                ? await service.GetByClientAsync(q.ClientId.Value, q.IncludeGlobal, ct)
                : await service.GetGlobalAsync(ct);

        // D11 — o filtro activeOnly era aceito na query e ignorado na listagem.
        if (q.ActiveOnly)
            deps = deps.Where(d => d.IsActive).ToList();

        return Result<IReadOnlyList<DepartmentDto>>.Success(
            deps.Select(CreateDepartmentCommandHandler.Map).ToList().AsReadOnly());
    }
}

public sealed class GetDepartmentByIdQueryHandler(
    IDepartmentService service
) : IRequestHandler<GetDepartmentByIdQuery, Result<DepartmentDto>>
{
    public async Task<Result<DepartmentDto>> Handle(GetDepartmentByIdQuery q, CancellationToken ct)
    {
        var dept = await service.GetByIdAsync(q.Id, ct);
        return dept is null
            ? Result<DepartmentDto>.Failure(Error.NotFound($"Department {q.Id} not found"))
            : Result<DepartmentDto>.Success(CreateDepartmentCommandHandler.Map(dept));
    }
}

/// <summary>
/// Configuração efetiva de triagem + origem por campo + equipe efetiva e
/// avisos de herança, para o console exibir o selo de herdado
/// (GET /api/v1/departments/{id}/effective).
/// </summary>
public sealed class GetDepartmentEffectiveConfigQueryHandler(
    IDepartmentService service,
    IDepartmentTeamResolver? teamResolver = null
) : IRequestHandler<GetDepartmentEffectiveConfigQuery, Result<DepartmentEffectiveConfigDto>>
{
    public async Task<Result<DepartmentEffectiveConfigDto>> Handle(
        GetDepartmentEffectiveConfigQuery q, CancellationToken ct)
    {
        var dept = await service.GetByIdAsync(q.Id, ct);
        if (dept is null)
            return Result<DepartmentEffectiveConfigDto>.Failure(Error.NotFound($"Department {q.Id} not found"));

        // Diagnóstico defensivo do vínculo (pai inativo/não global/ausente ou
        // ciclo em dados legados) + pai realmente utilizável.
        var diagnosis = await DepartmentInheritanceValidator.DiagnoseAsync(
            dept, service.GetByIdAsync, ct);
        var parent = diagnosis.UsableParent;

        var settings = DepartmentEffectiveSettingsResolver.Resolve(dept, parent);
        var (config, origins) = BuildConfig(settings);

        IReadOnlyList<DepartmentTeamMember> members = teamResolver is null
            ? []
            : await teamResolver.ResolveMembersAsync(dept.Id, ct);
        var team = MapTeam(members, dept, parent);

        return Result<DepartmentEffectiveConfigDto>.Success(new DepartmentEffectiveConfigDto
        {
            DepartmentId = dept.Id,
            ClientId = dept.ClientId,
            InheritFromGlobalId = dept.InheritFromGlobalId,
            GlobalParentId = parent?.Id,
            InheritFromGlobalName = parent?.Name,
            ResolvedAt = DateTime.UtcNow,
            AiAssignmentMode = MapSetting(settings.AiAssignmentMode),
            AiAssignmentMinConfidence = MapSetting(settings.AiAssignmentMinConfidence),
            AiAssignmentFallbackStrategy = MapSetting(settings.AiAssignmentFallbackStrategy),
            AiAssignmentMaxCandidates = MapSetting(settings.AiAssignmentMaxCandidates),
            AiAssignmentInstructions = MapSetting(settings.AiAssignmentInstructions),
            AiAssignmentWeightsJson = MapSetting(settings.AiAssignmentWeightsJson),
            AiAssignmentUseAffinity = MapSetting(settings.AiAssignmentUseAffinity),
            AiAssignmentMaxOutputTokens = MapSetting(settings.AiAssignmentMaxOutputTokens),
            AiSkillLearningMode = MapSetting(settings.AiSkillLearningMode),
            AiSkillMinEvidence = MapSetting(settings.AiSkillMinEvidence),
            AiSkillMaxTags = MapSetting(settings.AiSkillMaxTags),
            AiWeightLearningMode = MapSetting(settings.AiWeightLearningMode),
            AiWeightMaxDeltaPerCycle = MapSetting(settings.AiWeightMaxDeltaPerCycle),
            AiWeightCycleDays = MapSetting(settings.AiWeightCycleDays),
            AiWeightMin = MapSetting(settings.AiWeightMin),
            AiWeightMax = MapSetting(settings.AiWeightMax),
            AssignmentStrategy = MapSetting(settings.AssignmentStrategy),
            Config = config,
            Origins = origins,
            Team = team,
            Warnings = diagnosis.Warnings.ToList()
        });
    }

    /// <summary>Monta os objetos camelCase do console a partir dos 17 campos.</summary>
    private static (IReadOnlyDictionary<string, object?> Config, IReadOnlyDictionary<string, string> Origins)
        BuildConfig(DepartmentEffectiveSettings s)
    {
        var config = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["aiAssignmentMode"] = s.AiAssignmentMode.Value,
            ["aiAssignmentMinConfidence"] = s.AiAssignmentMinConfidence.Value,
            ["aiAssignmentFallbackStrategy"] = s.AiAssignmentFallbackStrategy.Value,
            ["aiAssignmentMaxCandidates"] = s.AiAssignmentMaxCandidates.Value,
            ["aiAssignmentInstructions"] = s.AiAssignmentInstructions.Value,
            ["aiAssignmentWeightsJson"] = s.AiAssignmentWeightsJson.Value,
            ["aiAssignmentUseAffinity"] = s.AiAssignmentUseAffinity.Value,
            ["aiAssignmentMaxOutputTokens"] = s.AiAssignmentMaxOutputTokens.Value,
            ["aiSkillLearningMode"] = s.AiSkillLearningMode.Value,
            ["aiSkillMinEvidence"] = s.AiSkillMinEvidence.Value,
            ["aiSkillMaxTags"] = s.AiSkillMaxTags.Value,
            ["aiWeightLearningMode"] = s.AiWeightLearningMode.Value,
            ["aiWeightMaxDeltaPerCycle"] = s.AiWeightMaxDeltaPerCycle.Value,
            ["aiWeightCycleDays"] = s.AiWeightCycleDays.Value,
            ["aiWeightMin"] = s.AiWeightMin.Value,
            ["aiWeightMax"] = s.AiWeightMax.Value,
            ["assignmentStrategy"] = s.AssignmentStrategy.Value
        };

        var origins = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aiAssignmentMode"] = OriginOf(s.AiAssignmentMode),
            ["aiAssignmentMinConfidence"] = OriginOf(s.AiAssignmentMinConfidence),
            ["aiAssignmentFallbackStrategy"] = OriginOf(s.AiAssignmentFallbackStrategy),
            ["aiAssignmentMaxCandidates"] = OriginOf(s.AiAssignmentMaxCandidates),
            ["aiAssignmentInstructions"] = OriginOf(s.AiAssignmentInstructions),
            ["aiAssignmentWeightsJson"] = OriginOf(s.AiAssignmentWeightsJson),
            ["aiAssignmentUseAffinity"] = OriginOf(s.AiAssignmentUseAffinity),
            ["aiAssignmentMaxOutputTokens"] = OriginOf(s.AiAssignmentMaxOutputTokens),
            ["aiSkillLearningMode"] = OriginOf(s.AiSkillLearningMode),
            ["aiSkillMinEvidence"] = OriginOf(s.AiSkillMinEvidence),
            ["aiSkillMaxTags"] = OriginOf(s.AiSkillMaxTags),
            ["aiWeightLearningMode"] = OriginOf(s.AiWeightLearningMode),
            ["aiWeightMaxDeltaPerCycle"] = OriginOf(s.AiWeightMaxDeltaPerCycle),
            ["aiWeightCycleDays"] = OriginOf(s.AiWeightCycleDays),
            ["aiWeightMin"] = OriginOf(s.AiWeightMin),
            ["aiWeightMax"] = OriginOf(s.AiWeightMax),
            ["assignmentStrategy"] = OriginOf(s.AssignmentStrategy)
        };

        return (config, origins);
    }

    private static DepartmentEffectiveMemberDto[] MapTeam(
        IReadOnlyList<DepartmentTeamMember> members, Department department, Department? parent)
    {
        var result = new List<DepartmentEffectiveMemberDto>(members.Count);
        foreach (var member in members)
        {
            var inherited = member.Origin.StartsWith("inherited", StringComparison.OrdinalIgnoreCase);
            var sourceId = inherited ? parent?.Id ?? ParseOriginDepartmentId(member.Origin) : department.Id;
            var sourceName = inherited ? parent?.Name : department.Name;

            result.Add(new DepartmentEffectiveMemberDto(
                member.UserId,
                member.DisplayName,
                IsActive: true, // o resolvedor já filtra vínculos e usuários inativos
                Inherited: inherited,
                Origin: inherited ? "inherited-global" : "own",
                SourceDepartmentId: sourceId,
                SourceDepartmentName: sourceName));
        }

        return [.. result];
    }

    private static Guid? ParseOriginDepartmentId(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return null;

        var separator = origin.IndexOf(':');
        return separator >= 0
               && Guid.TryParse(origin[(separator + 1)..], out var id)
            ? id
            : null;
    }

    private static string OriginOf<T>(DepartmentEffectiveSetting<T> setting)
        => setting.Origin switch
        {
            DepartmentSettingOrigin.Own => "own",
            DepartmentSettingOrigin.InheritedGlobal => "inherited-global",
            _ => "default"
        };

    private static DepartmentEffectiveSettingDto<T> MapSetting<T>(DepartmentEffectiveSetting<T> setting)
        => new(setting.Value, OriginOf(setting), setting.SourceDepartmentId);
}
