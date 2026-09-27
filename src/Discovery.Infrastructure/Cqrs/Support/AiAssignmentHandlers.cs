using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Support.Assignments;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Support;

/// <summary>
/// Mapeamento das decisões persistidas para o DTO exposto pela API.
/// </summary>
internal static class AssignmentDecisionMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static TicketAssignmentDecisionDto ToDto(
        TicketAssignmentDecision decision, IReadOnlyList<AssignmentCandidateDto> candidates)
        => new(
            decision.Id, decision.TicketId, decision.DepartmentId, decision.Mode, decision.StrategySource,
            decision.Difficulty, decision.ChosenUserId,
            candidates.FirstOrDefault(c => c.UserId == decision.ChosenUserId)?.UserName,
            decision.Confidence, decision.Score, decision.Rationale, decision.Model, decision.TokensUsed,
            decision.Applied, decision.NotAppliedReason, decision.OverriddenAt, decision.OverriddenByUserId,
            decision.CreatedAt, candidates, decision.MaxOutputTokens, decision.PromptChars);

    public static TicketAssignmentDecisionDto ToDto(TicketAssignmentDecision decision)
        => ToDto(decision, DeserializeCandidates(decision.CandidatesJson));

    public static IReadOnlyList<AssignmentCandidateDto> DeserializeCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<AssignmentCandidateDto>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed class GetDepartmentAssignmentTeamMetricsQueryHandler(
    DiscoveryDbContext db,
    ITechnicianMetricsService metricsService)
    : IRequestHandler<GetDepartmentAssignmentTeamMetricsQuery, Result<IReadOnlyList<DepartmentMemberProfileDto>>>
{
    public async Task<Result<IReadOnlyList<DepartmentMemberProfileDto>>> Handle(
        GetDepartmentAssignmentTeamMetricsQuery q, CancellationToken ct)
    {
        var exists = await db.Departments.AnyAsync(d => d.Id == q.DepartmentId, ct);
        if (!exists)
            return Result<IReadOnlyList<DepartmentMemberProfileDto>>.Failure(
                Error.NotFound("Departamento não encontrado."));

        var members = await db.DepartmentMembers.AsNoTracking()
            .Where(m => m.DepartmentId == q.DepartmentId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                m.UserId,
                m.IsActive,
                m.CreatedAt,
                m.SkillTagsJson,
                m.SkillLevel,
                m.MaxOpenTickets,
                m.Weight,
                m.AcceptsAiAssignment
            })
            .ToListAsync(ct);

        var userIds = members.Select(m => m.UserId).ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Login })
            .ToListAsync(ct);
        var names = users.ToDictionary(
            u => u.Id,
            u => string.IsNullOrWhiteSpace(u.FullName) ? u.Login : u.FullName);

        var metrics = (await metricsService.GetMetricsForUsersAsync(userIds, ct))
            .ToDictionary(m => m.UserId);

        var dtos = members.Select(m => new DepartmentMemberProfileDto(
            q.DepartmentId,
            m.UserId,
            names.TryGetValue(m.UserId, out var name) ? name : null,
            m.IsActive,
            m.CreatedAt,
            ParseSkillTags(m.SkillTagsJson),
            m.SkillLevel,
            m.MaxOpenTickets,
            m.Weight,
            m.AcceptsAiAssignment,
            metrics.TryGetValue(m.UserId, out var found) ? found : null)).ToList();

        return Result<IReadOnlyList<DepartmentMemberProfileDto>>.Success(dtos);
    }

    internal static IReadOnlyList<string> ParseSkillTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed class UpdateDepartmentMemberProfileCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<UpdateDepartmentMemberProfileCommand, Result<DepartmentMemberProfileDto>>
{
    public async Task<Result<DepartmentMemberProfileDto>> Handle(
        UpdateDepartmentMemberProfileCommand cmd, CancellationToken ct)
    {
        if (cmd.SkillLevel.HasValue && cmd.SkillLevel is < 1 or > 5)
            return Result<DepartmentMemberProfileDto>.Failure(
                Error.Validation("SkillLevel", "Nível de competência deve estar entre 1 e 5."));

        if (cmd.Weight.HasValue && (cmd.Weight < 0.1m || cmd.Weight > 3.0m))
            return Result<DepartmentMemberProfileDto>.Failure(
                Error.Validation("Weight", "Peso do atendente deve estar entre 0,1 e 3,0."));

        if (cmd.MaxOpenTickets.HasValue && cmd.MaxOpenTickets < 0)
            return Result<DepartmentMemberProfileDto>.Failure(
                Error.Validation("MaxOpenTickets", "Teto de chamados abertos não pode ser negativo."));

        var member = await db.DepartmentMembers
            .FirstOrDefaultAsync(m => m.DepartmentId == cmd.DepartmentId && m.UserId == cmd.UserId, ct);
        if (member is null)
            return Result<DepartmentMemberProfileDto>.Failure(Error.NotFound("Membro não encontrado."));

        if (cmd.SkillTags is not null)
        {
            var tags = cmd.SkillTags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim().ToLowerInvariant())
                .Distinct()
                .Take(20)
                .ToList();
            member.SkillTagsJson = tags.Count == 0 ? null : JsonSerializer.Serialize(tags);
        }

        if (cmd.SkillLevel.HasValue) member.SkillLevel = cmd.SkillLevel.Value;
        if (cmd.ClearMaxOpenTickets) member.MaxOpenTickets = null;
        else if (cmd.MaxOpenTickets.HasValue) member.MaxOpenTickets = cmd.MaxOpenTickets.Value;
        if (cmd.Weight.HasValue) member.Weight = cmd.Weight.Value;
        if (cmd.AcceptsAiAssignment.HasValue) member.AcceptsAiAssignment = cmd.AcceptsAiAssignment.Value;

        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == cmd.UserId)
            .Select(u => new { u.FullName, u.Login })
            .FirstOrDefaultAsync(ct);

        return Result<DepartmentMemberProfileDto>.Success(new DepartmentMemberProfileDto(
            member.DepartmentId,
            member.UserId,
            user is null ? null : (string.IsNullOrWhiteSpace(user.FullName) ? user.Login : user.FullName),
            member.IsActive,
            member.CreatedAt,
            GetDepartmentAssignmentTeamMetricsQueryHandler.ParseSkillTags(member.SkillTagsJson),
            member.SkillLevel,
            member.MaxOpenTickets,
            member.Weight,
            member.AcceptsAiAssignment,
            null));
    }
}

public sealed class RefreshTechnicianMetricsCommandHandler(ITechnicianMetricsService metricsService)
    : IRequestHandler<RefreshTechnicianMetricsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RefreshTechnicianMetricsCommand cmd, CancellationToken ct)
        => Result<int>.Success(await metricsService.RefreshSnapshotsAsync(null, cmd.DepartmentId, ct));
}

public sealed class GetTicketAssignmentDecisionQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<GetTicketAssignmentDecisionQuery, Result<TicketAssignmentDecisionDto>>
{
    public async Task<Result<TicketAssignmentDecisionDto>> Handle(
        GetTicketAssignmentDecisionQuery q, CancellationToken ct)
    {
        var decision = await db.TicketAssignmentDecisions.AsNoTracking()
            .Where(d => d.TicketId == q.TicketId)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return decision is null
            ? Result<TicketAssignmentDecisionDto>.Failure(
                Error.NotFound("Nenhuma decisão de triagem por IA registrada para este chamado."))
            : Result<TicketAssignmentDecisionDto>.Success(AssignmentDecisionMapper.ToDto(decision));
    }
}

public sealed class ListTicketAssignmentDecisionsQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<ListTicketAssignmentDecisionsQuery, Result<IReadOnlyList<TicketAssignmentDecisionDto>>>
{
    public async Task<Result<IReadOnlyList<TicketAssignmentDecisionDto>>> Handle(
        ListTicketAssignmentDecisionsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);

        var query = db.TicketAssignmentDecisions.AsNoTracking();

        if (q.DepartmentId.HasValue) query = query.Where(d => d.DepartmentId == q.DepartmentId.Value);
        if (q.TicketId.HasValue) query = query.Where(d => d.TicketId == q.TicketId.Value);
        if (q.ChosenUserId.HasValue) query = query.Where(d => d.ChosenUserId == q.ChosenUserId.Value);
        if (q.From.HasValue) query = query.Where(d => d.CreatedAt >= q.From.Value);
        if (q.To.HasValue) query = query.Where(d => d.CreatedAt <= q.To.Value);

        var decisions = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Result<IReadOnlyList<TicketAssignmentDecisionDto>>.Success(
            decisions.Select(AssignmentDecisionMapper.ToDto).ToList());
    }
}

public sealed class PreviewTicketAssignmentCommandHandler(IAiTicketTriageService triageService)
    : IRequestHandler<PreviewTicketAssignmentCommand, Result<TicketAssignmentResultDto>>
{
    public async Task<Result<TicketAssignmentResultDto>> Handle(
        PreviewTicketAssignmentCommand cmd, CancellationToken ct)
    {
        var result = await triageService.PreviewAsync(cmd.TicketId, ct);
        return result.Decision.TicketId == Guid.Empty
            ? Result<TicketAssignmentResultDto>.Failure(Error.NotFound("Chamado não encontrado."))
            : Result<TicketAssignmentResultDto>.Success(result);
    }
}

public sealed class ApplyTicketAssignmentCommandHandler(IAiTicketTriageService triageService)
    : IRequestHandler<ApplyTicketAssignmentCommand, Result<TicketAssignmentResultDto>>
{
    public async Task<Result<TicketAssignmentResultDto>> Handle(
        ApplyTicketAssignmentCommand cmd, CancellationToken ct)
    {
        var result = await triageService.ApplyAsync(cmd.TicketId, cmd.TriggeredByUserId, ct);
        return result.Decision.TicketId == Guid.Empty
            ? Result<TicketAssignmentResultDto>.Failure(Error.NotFound("Chamado não encontrado."))
            : Result<TicketAssignmentResultDto>.Success(result);
    }
}
