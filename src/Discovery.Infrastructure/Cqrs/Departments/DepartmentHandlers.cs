using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Departments.Commands;
using Discovery.Core.Cqrs.Departments.Queries;
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
    IDepartmentService service
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

        var dept = new Department
        {
            Name = cmd.Name,
            Description = cmd.Description,
            ClientId = cmd.ClientId,
            InheritFromGlobalId = cmd.InheritFromGlobalId,
            SortOrder = cmd.SortOrder,
            IsActive = true,
            AssignmentStrategy = cmd.AssignmentStrategy,
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
            AiWeightMax = cmd.AiWeightMax
        };
        var created = await service.CreateAsync(dept, ct);
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
        d.AiWeightMin, d.AiWeightMax);
}

public sealed class UpdateDepartmentCommandHandler(
    IDepartmentService service
) : IRequestHandler<UpdateDepartmentCommand, Result<DepartmentDto>>
{
    public async Task<Result<DepartmentDto>> Handle(UpdateDepartmentCommand cmd, CancellationToken ct)
    {
        var dept = await service.GetByIdAsync(cmd.Id, ct);
        if (dept is null)
            return Result<DepartmentDto>.Failure(Error.NotFound($"Department {cmd.Id} not found"));

        if (cmd.Name is not null) dept.Name = cmd.Name;
        if (cmd.Description is not null) dept.Description = cmd.Description;
        if (cmd.InheritFromGlobalId is not null) dept.InheritFromGlobalId = cmd.InheritFromGlobalId;
        if (cmd.SortOrder.HasValue) dept.SortOrder = cmd.SortOrder.Value;
        if (cmd.IsActive.HasValue) dept.IsActive = cmd.IsActive.Value;
        if (cmd.AssignmentStrategy.HasValue) dept.AssignmentStrategy = cmd.AssignmentStrategy.Value;
        if (cmd.AiAssignmentMode.HasValue) dept.AiAssignmentMode = cmd.AiAssignmentMode.Value;
        if (cmd.AiAssignmentMinConfidence.HasValue) dept.AiAssignmentMinConfidence = cmd.AiAssignmentMinConfidence.Value;
        if (cmd.AiAssignmentFallbackStrategy.HasValue) dept.AiAssignmentFallbackStrategy = cmd.AiAssignmentFallbackStrategy.Value;
        if (cmd.AiAssignmentMaxCandidates.HasValue) dept.AiAssignmentMaxCandidates = cmd.AiAssignmentMaxCandidates.Value;
        if (cmd.AiAssignmentUseAffinity.HasValue) dept.AiAssignmentUseAffinity = cmd.AiAssignmentUseAffinity.Value;
        if (cmd.AiAssignmentMaxOutputTokens.HasValue) dept.AiAssignmentMaxOutputTokens = cmd.AiAssignmentMaxOutputTokens.Value;
        if (cmd.AiSkillLearningMode.HasValue) dept.AiSkillLearningMode = cmd.AiSkillLearningMode.Value;
        if (cmd.AiSkillMinEvidence.HasValue) dept.AiSkillMinEvidence = cmd.AiSkillMinEvidence.Value;
        if (cmd.AiSkillMaxTags.HasValue) dept.AiSkillMaxTags = cmd.AiSkillMaxTags.Value;
        if (cmd.AiWeightLearningMode.HasValue) dept.AiWeightLearningMode = cmd.AiWeightLearningMode.Value;
        if (cmd.AiWeightMaxDeltaPerCycle.HasValue) dept.AiWeightMaxDeltaPerCycle = cmd.AiWeightMaxDeltaPerCycle.Value;
        if (cmd.AiWeightCycleDays.HasValue) dept.AiWeightCycleDays = cmd.AiWeightCycleDays.Value;
        if (cmd.AiWeightMin.HasValue) dept.AiWeightMin = cmd.AiWeightMin.Value;
        if (cmd.AiWeightMax.HasValue) dept.AiWeightMax = cmd.AiWeightMax.Value;
        if (cmd.AiAssignmentWeightsJson is not null)
            dept.AiAssignmentWeightsJson = string.IsNullOrWhiteSpace(cmd.AiAssignmentWeightsJson) ? null : cmd.AiAssignmentWeightsJson;
        if (cmd.AiAssignmentInstructions is not null)
            dept.AiAssignmentInstructions = string.IsNullOrWhiteSpace(cmd.AiAssignmentInstructions) ? null : cmd.AiAssignmentInstructions;

        var validation = DepartmentAiSettingsValidator.Validate(
            dept.AssignmentStrategy, dept.AiAssignmentMode, dept.AiAssignmentMinConfidence,
            dept.AiAssignmentFallbackStrategy, dept.AiAssignmentMaxCandidates)
            ?? DepartmentAiSettingsValidator.ValidateLearning(
                dept.AiAssignmentMaxOutputTokens, dept.AiSkillLearningMode, dept.AiSkillMinEvidence,
                dept.AiSkillMaxTags, dept.AiWeightLearningMode, dept.AiWeightMaxDeltaPerCycle,
                dept.AiWeightCycleDays, dept.AiWeightMin, dept.AiWeightMax);
        if (validation is not null)
            return Result<DepartmentDto>.Failure(validation);

        var updated = await service.UpdateAsync(dept, ct);
        return Result<DepartmentDto>.Success(CreateDepartmentCommandHandler.Map(updated));
    }
}

public sealed class DeleteDepartmentCommandHandler(
    IDepartmentService service
) : IRequestHandler<DeleteDepartmentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteDepartmentCommand cmd, CancellationToken ct)
    {
        var deleted = await service.DeleteAsync(cmd.Id, ct);
        return deleted
            ? Result<VoidResult>.Success(VoidResult.Value)
            : Result<VoidResult>.Failure(Error.NotFound($"Department {cmd.Id} not found"));
    }
}

public sealed class ListDepartmentsQueryHandler(
    IDepartmentService service
) : IRequestHandler<ListDepartmentsQuery, Result<IReadOnlyList<DepartmentDto>>>
{
    public async Task<Result<IReadOnlyList<DepartmentDto>>> Handle(ListDepartmentsQuery q, CancellationToken ct)
    {
        var deps = q.ClientId.HasValue
            ? await service.GetByClientAsync(q.ClientId.Value, q.IncludeGlobal, ct)
            : await service.GetGlobalAsync(ct);
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
