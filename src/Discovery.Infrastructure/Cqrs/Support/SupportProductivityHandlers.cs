using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Support.Channels;
using Discovery.Core.Cqrs.Support.Csat;
using Discovery.Core.Cqrs.Support.Departments;
using Discovery.Core.Cqrs.Support.Macros;
using Discovery.Core.Cqrs.Support.Templates;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Support;

// ── Macros (respostas rápidas) ───────────────────────────────────────────

public sealed class CreateTicketMacroCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<CreateTicketMacroCommand, Result<TicketMacroDto>>
{
    public async Task<Result<TicketMacroDto>> Handle(CreateTicketMacroCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name) || string.IsNullOrWhiteSpace(cmd.Content))
            return Result<TicketMacroDto>.Failure(Error.Validation("Name", "Nome e conteúdo da macro são obrigatórios."));

        var macro = new TicketMacro
        {
            Id = Guid.NewGuid(),
            ClientId = cmd.ClientId,
            DepartmentId = cmd.DepartmentId,
            Name = cmd.Name.Trim(),
            Description = cmd.Description,
            Content = cmd.Content,
            IsActive = cmd.IsActive,
            CreatedBy = cmd.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.TicketMacros.Add(macro);
        await db.SaveChangesAsync(ct);
        return Result<TicketMacroDto>.Success(MapMacro(macro));
    }

    internal static TicketMacroDto MapMacro(TicketMacro m) => new(
        m.Id, m.ClientId, m.DepartmentId, m.Name, m.Description, m.Content, m.IsActive,
        m.CreatedBy, m.CreatedAt, m.UpdatedAt);
}

public sealed class UpdateTicketMacroCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<UpdateTicketMacroCommand, Result<TicketMacroDto>>
{
    public async Task<Result<TicketMacroDto>> Handle(UpdateTicketMacroCommand cmd, CancellationToken ct)
    {
        var macro = await db.TicketMacros.FirstOrDefaultAsync(m => m.Id == cmd.Id, ct);
        if (macro is null)
            return Result<TicketMacroDto>.Failure(Error.NotFound("Macro não encontrada."));

        macro.ClientId = cmd.ClientId;
        macro.DepartmentId = cmd.DepartmentId;
        macro.Name = cmd.Name.Trim();
        macro.Description = cmd.Description;
        macro.Content = cmd.Content;
        macro.IsActive = cmd.IsActive;
        macro.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result<TicketMacroDto>.Success(CreateTicketMacroCommandHandler.MapMacro(macro));
    }
}

public sealed class DeleteTicketMacroCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<DeleteTicketMacroCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteTicketMacroCommand cmd, CancellationToken ct)
    {
        var macro = await db.TicketMacros.FirstOrDefaultAsync(m => m.Id == cmd.Id, ct);
        if (macro is null)
            return Result<VoidResult>.Failure(Error.NotFound("Macro não encontrada."));
        db.TicketMacros.Remove(macro);
        await db.SaveChangesAsync(ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ListTicketMacrosQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<ListTicketMacrosQuery, Result<IReadOnlyList<TicketMacroDto>>>
{
    public async Task<Result<IReadOnlyList<TicketMacroDto>>> Handle(ListTicketMacrosQuery q, CancellationToken ct)
    {
        var query = db.TicketMacros.AsNoTracking().Where(m => m.IsActive);
        if (q.ClientId.HasValue)
            query = q.IncludeGlobal
                ? query.Where(m => m.ClientId == q.ClientId || m.ClientId == null)
                : query.Where(m => m.ClientId == q.ClientId);
        else
            query = query.Where(m => m.ClientId == null);
        if (q.DepartmentId.HasValue)
            query = query.Where(m => m.DepartmentId == q.DepartmentId || m.DepartmentId == null);

        var items = await query.OrderBy(m => m.Name).ToListAsync(ct);
        return Result<IReadOnlyList<TicketMacroDto>>.Success(
            items.Select(CreateTicketMacroCommandHandler.MapMacro).ToList());
    }
}

// ── Templates de chamado ─────────────────────────────────────────────────

public sealed class CreateTicketTemplateCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<CreateTicketTemplateCommand, Result<TicketTemplateDto>>
{
    public async Task<Result<TicketTemplateDto>> Handle(CreateTicketTemplateCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name) || string.IsNullOrWhiteSpace(cmd.Title))
            return Result<TicketTemplateDto>.Failure(Error.Validation("Name", "Nome e título do template são obrigatórios."));

        var questionErrors = TicketTemplateQuestions.ValidateDefinitions(TicketTemplateQuestions.Parse(cmd.QuestionsJson));
        if (questionErrors.Count > 0)
            return Result<TicketTemplateDto>.Failure(Error.Validation(questionErrors[0].Key, questionErrors[0].Message));

        var template = new TicketTemplate
        {
            Id = Guid.NewGuid(),
            ClientId = cmd.ClientId,
            DepartmentId = cmd.DepartmentId,
            Name = cmd.Name.Trim(),
            Title = cmd.Title,
            Description = cmd.Description,
            Priority = ParsePriority(cmd.Priority),
            Category = cmd.Category,
            CustomFieldDefaultsJson = string.IsNullOrWhiteSpace(cmd.CustomFieldDefaultsJson) ? "{}" : cmd.CustomFieldDefaultsJson,
            QuestionsJson = TicketTemplateQuestions.Serialize(TicketTemplateQuestions.Parse(cmd.QuestionsJson)),
            IsActive = cmd.IsActive,
            CreatedBy = cmd.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.TicketTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return Result<TicketTemplateDto>.Success(MapTemplate(template));
    }

    internal static TicketPriority? ParsePriority(string? value)
        => Enum.TryParse<TicketPriority>(value, ignoreCase: true, out var p) ? p : null;

    internal static TicketTemplateDto MapTemplate(TicketTemplate t) => new(
        t.Id, t.ClientId, t.DepartmentId, t.Name, t.Title, t.Description,
        t.Priority?.ToString(), t.Category, t.CustomFieldDefaultsJson, t.QuestionsJson, t.IsActive,
        t.CreatedBy, t.CreatedAt, t.UpdatedAt, t.DeletedAt, t.DeletedBy);
}

public sealed class UpdateTicketTemplateCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<UpdateTicketTemplateCommand, Result<TicketTemplateDto>>
{
    public async Task<Result<TicketTemplateDto>> Handle(UpdateTicketTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.TicketTemplates.FirstOrDefaultAsync(t => t.Id == cmd.Id, ct);
        if (template is null)
            return Result<TicketTemplateDto>.Failure(Error.NotFound("Template não encontrado."));

        var questionErrors = TicketTemplateQuestions.ValidateDefinitions(TicketTemplateQuestions.Parse(cmd.QuestionsJson));
        if (questionErrors.Count > 0)
            return Result<TicketTemplateDto>.Failure(Error.Validation(questionErrors[0].Key, questionErrors[0].Message));

        template.ClientId = cmd.ClientId;
        template.DepartmentId = cmd.DepartmentId;
        template.Name = cmd.Name.Trim();
        template.Title = cmd.Title;
        template.Description = cmd.Description;
        template.Priority = CreateTicketTemplateCommandHandler.ParsePriority(cmd.Priority);
        template.Category = cmd.Category;
        template.CustomFieldDefaultsJson = string.IsNullOrWhiteSpace(cmd.CustomFieldDefaultsJson) ? "{}" : cmd.CustomFieldDefaultsJson;
        template.QuestionsJson = TicketTemplateQuestions.Serialize(TicketTemplateQuestions.Parse(cmd.QuestionsJson));
        template.IsActive = cmd.IsActive;
        template.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result<TicketTemplateDto>.Success(CreateTicketTemplateCommandHandler.MapTemplate(template));
    }
}

public sealed class DeleteTicketTemplateCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<DeleteTicketTemplateCommand, Result<VoidResult>>
{
    // Soft delete: sai da listagem, mas permanece na lixeira para restauração.
    // O histórico dos chamados não depende desta linha (snapshot template_name).
    public async Task<Result<VoidResult>> Handle(DeleteTicketTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.TicketTemplates.FirstOrDefaultAsync(t => t.Id == cmd.Id, ct);
        if (template is null)
            return Result<VoidResult>.Failure(Error.NotFound("Template não encontrado."));

        if (template.DeletedAt is null)
        {
            template.DeletedAt = DateTime.UtcNow;
            template.DeletedBy = cmd.DeletedBy;
            template.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class PurgeTicketTemplateCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<PurgeTicketTemplateCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(PurgeTicketTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.TicketTemplates.FirstOrDefaultAsync(t => t.Id == cmd.Id, ct);
        if (template is null)
            return Result<VoidResult>.Failure(Error.NotFound("Template não encontrado."));

        // Proteção da exclusão física: template já usado exige confirmação
        // explícita. A FK ON DELETE SET NULL preserva o histórico
        // (tickets.template_name).
        if (!cmd.Force)
        {
            var inUse = await db.Tickets.CountAsync(t => t.TemplateId == cmd.Id, ct);
            if (inUse > 0)
                return Result<VoidResult>.Failure(Error.Conflict(
                    $"Template usado por {inUse} chamado(s). Confirme a exclusão para continuar."));
        }

        db.TicketTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class RestoreTicketTemplateCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<RestoreTicketTemplateCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RestoreTicketTemplateCommand cmd, CancellationToken ct)
    {
        var template = await db.TicketTemplates.FirstOrDefaultAsync(t => t.Id == cmd.Id, ct);
        if (template is null)
            return Result<VoidResult>.Failure(Error.NotFound("Template não encontrado."));

        // Idempotente: template fora da lixeira já está restaurado.
        if (template.DeletedAt is not null)
        {
            template.DeletedAt = null;
            template.DeletedBy = null;
            template.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ListTicketTemplatesQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<ListTicketTemplatesQuery, Result<IReadOnlyList<TicketTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<TicketTemplateDto>>> Handle(ListTicketTemplatesQuery q, CancellationToken ct)
    {
        // Padrão: ativos e não excluídos. A lixeira entra por includeDeleted;
        // inativos entram por includeInactive (página de administração).
        var query = db.TicketTemplates.AsNoTracking();
        if (!q.IncludeDeleted)
            query = query.Where(t => t.DeletedAt == null);
        if (!q.IncludeInactive)
            query = query.Where(t => t.IsActive);

        if (q.ClientId.HasValue)
            query = q.IncludeGlobal
                ? query.Where(t => t.ClientId == q.ClientId || t.ClientId == null)
                : query.Where(t => t.ClientId == q.ClientId);
        else if (!q.AllClients)
            // Sem cliente informado: apenas globais. A página de administração
            // usa allClients=true para gerenciar também templates por cliente
            // (antes eles eram invisíveis na listagem).
            query = query.Where(t => t.ClientId == null);
        if (q.DepartmentId.HasValue)
            query = query.Where(t => t.DepartmentId == q.DepartmentId || t.DepartmentId == null);

        var items = await query.OrderBy(t => t.Name).ToListAsync(ct);
        return Result<IReadOnlyList<TicketTemplateDto>>.Success(
            items.Select(CreateTicketTemplateCommandHandler.MapTemplate).ToList());
    }
}

// ── Canais de notificação ────────────────────────────────────────────────

public sealed class CreateNotificationChannelCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<CreateNotificationChannelCommand, Result<NotificationChannelDto>>
{
    public async Task<Result<NotificationChannelDto>> Handle(CreateNotificationChannelCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Name))
            return Result<NotificationChannelDto>.Failure(Error.Validation("Name", "Nome do canal é obrigatório."));
        if (!Enum.TryParse<NotificationChannelType>(cmd.Type, ignoreCase: true, out var type))
            return Result<NotificationChannelDto>.Failure(Error.Validation("Type", "Tipo de canal inválido (Webhook ou Email)."));

        var channel = new NotificationChannel
        {
            Id = Guid.NewGuid(),
            Name = cmd.Name.Trim(),
            Type = type,
            IsActive = cmd.IsActive,
            EventsJson = string.IsNullOrWhiteSpace(cmd.EventsJson) ? "[]" : cmd.EventsJson,
            ConfigJson = string.IsNullOrWhiteSpace(cmd.ConfigJson) ? "{}" : cmd.ConfigJson,
            CreatedBy = cmd.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        return Result<NotificationChannelDto>.Success(MapChannel(channel));
    }

    internal static NotificationChannelDto MapChannel(NotificationChannel c) => new(
        c.Id, c.Name, c.Type.ToString(), c.IsActive, c.EventsJson, c.ConfigJson,
        c.CreatedBy, c.CreatedAt, c.UpdatedAt);
}

public sealed class UpdateNotificationChannelCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<UpdateNotificationChannelCommand, Result<NotificationChannelDto>>
{
    public async Task<Result<NotificationChannelDto>> Handle(UpdateNotificationChannelCommand cmd, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == cmd.Id, ct);
        if (channel is null)
            return Result<NotificationChannelDto>.Failure(Error.NotFound("Canal não encontrado."));
        if (!Enum.TryParse<NotificationChannelType>(cmd.Type, ignoreCase: true, out var type))
            return Result<NotificationChannelDto>.Failure(Error.Validation("Type", "Tipo de canal inválido."));

        channel.Name = cmd.Name.Trim();
        channel.Type = type;
        channel.IsActive = cmd.IsActive;
        channel.EventsJson = string.IsNullOrWhiteSpace(cmd.EventsJson) ? "[]" : cmd.EventsJson;
        channel.ConfigJson = string.IsNullOrWhiteSpace(cmd.ConfigJson) ? "{}" : cmd.ConfigJson;
        channel.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Result<NotificationChannelDto>.Success(CreateNotificationChannelCommandHandler.MapChannel(channel));
    }
}

public sealed class DeleteNotificationChannelCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<DeleteNotificationChannelCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteNotificationChannelCommand cmd, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == cmd.Id, ct);
        if (channel is null)
            return Result<VoidResult>.Failure(Error.NotFound("Canal não encontrado."));
        db.NotificationChannels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ListNotificationChannelsQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<ListNotificationChannelsQuery, Result<IReadOnlyList<NotificationChannelDto>>>
{
    public async Task<Result<IReadOnlyList<NotificationChannelDto>>> Handle(ListNotificationChannelsQuery q, CancellationToken ct)
    {
        var items = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        return Result<IReadOnlyList<NotificationChannelDto>>.Success(
            items.Select(CreateNotificationChannelCommandHandler.MapChannel).ToList());
    }
}

// ── Membros de departamento ──────────────────────────────────────────────

public sealed class AddDepartmentMemberCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<AddDepartmentMemberCommand, Result<DepartmentMemberDto>>
{
    public async Task<Result<DepartmentMemberDto>> Handle(AddDepartmentMemberCommand cmd, CancellationToken ct)
    {
        if (!await db.Departments.AnyAsync(d => d.Id == cmd.DepartmentId, ct))
            return Result<DepartmentMemberDto>.Failure(Error.NotFound("Departamento não encontrado."));
        if (!await db.Users.AnyAsync(u => u.Id == cmd.UserId, ct))
            return Result<DepartmentMemberDto>.Failure(Error.NotFound("Usuário não encontrado."));

        var existing = await db.DepartmentMembers
            .FirstOrDefaultAsync(m => m.DepartmentId == cmd.DepartmentId && m.UserId == cmd.UserId, ct);
        if (existing is not null)
        {
            existing.IsActive = true;
            await db.SaveChangesAsync(ct);
            return Result<DepartmentMemberDto>.Success(await MapMemberAsync(db, existing, ct));
        }

        var member = new DepartmentMember
        {
            Id = Guid.NewGuid(),
            DepartmentId = cmd.DepartmentId,
            UserId = cmd.UserId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.DepartmentMembers.Add(member);
        await db.SaveChangesAsync(ct);
        return Result<DepartmentMemberDto>.Success(await MapMemberAsync(db, member, ct));
    }

    internal static async Task<DepartmentMemberDto> MapMemberAsync(DiscoveryDbContext db, DepartmentMember m, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == m.UserId, ct);
        var name = user is null ? null : (string.IsNullOrWhiteSpace(user.FullName) ? user.Login : user.FullName);
        return new DepartmentMemberDto(m.Id, m.DepartmentId, m.UserId, name, m.IsActive, m.CreatedAt);
    }
}

public sealed class RemoveDepartmentMemberCommandHandler(DiscoveryDbContext db)
    : IRequestHandler<RemoveDepartmentMemberCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RemoveDepartmentMemberCommand cmd, CancellationToken ct)
    {
        var member = await db.DepartmentMembers
            .FirstOrDefaultAsync(m => m.DepartmentId == cmd.DepartmentId && m.UserId == cmd.UserId, ct);
        if (member is null)
            return Result<VoidResult>.Failure(Error.NotFound("Membro não encontrado."));
        db.DepartmentMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class ListDepartmentMembersQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<ListDepartmentMembersQuery, Result<IReadOnlyList<DepartmentMemberDto>>>
{
    public async Task<Result<IReadOnlyList<DepartmentMemberDto>>> Handle(ListDepartmentMembersQuery q, CancellationToken ct)
    {
        var members = await db.DepartmentMembers.AsNoTracking()
            .Where(m => m.DepartmentId == q.DepartmentId)
            .ToListAsync(ct);
        var userIds = members.Select(m => m.UserId).ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Login })
            .ToListAsync(ct);
        var map = users.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Login : u.FullName);

        var dtos = members
            .Select(m => new DepartmentMemberDto(m.Id, m.DepartmentId, m.UserId,
                map.TryGetValue(m.UserId, out var n) ? n : null, m.IsActive, m.CreatedAt))
            .ToList();
        return Result<IReadOnlyList<DepartmentMemberDto>>.Success(dtos);
    }
}

// ── CSAT ─────────────────────────────────────────────────────────────────

public sealed class GetTicketCsatSummaryQueryHandler(DiscoveryDbContext db)
    : IRequestHandler<GetTicketCsatSummaryQuery, Result<TicketCsatSummaryDto>>
{
    public async Task<Result<TicketCsatSummaryDto>> Handle(GetTicketCsatSummaryQuery q, CancellationToken ct)
    {
        var to = q.To ?? DateTime.UtcNow;
        var from = q.From ?? to.AddDays(-30);

        var closedQuery = db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.ClosedAt != null && t.ClosedAt >= from && t.ClosedAt <= to);
        if (q.ClientId.HasValue) closedQuery = closedQuery.Where(t => t.ClientId == q.ClientId.Value);
        if (q.DepartmentId.HasValue) closedQuery = closedQuery.Where(t => t.DepartmentId == q.DepartmentId.Value);

        var total = await closedQuery.CountAsync(ct);

        var ratedRows = await closedQuery
            .Where(t => t.Rating != null)
            .Select(t => new { Rating = t.Rating!.Value, t.DepartmentId, t.AssignedToUserId })
            .ToListAsync(ct);

        var distribution = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0, [4] = 0, [5] = 0 };
        foreach (var row in ratedRows)
        {
            if (distribution.ContainsKey(row.Rating))
                distribution[row.Rating]++;
        }

        var average = ratedRows.Count > 0 ? Math.Round(ratedRows.Average(r => r.Rating), 2) : 0d;

        var deptIds = ratedRows.Where(r => r.DepartmentId.HasValue).Select(r => r.DepartmentId!.Value).Distinct().ToList();
        var deptNames = await db.Departments.AsNoTracking()
            .Where(d => deptIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        var byDepartment = ratedRows
            .GroupBy(r => r.DepartmentId)
            .Select(g => new TicketCsatGroupDto(
                g.Key?.ToString(),
                g.Key.HasValue && deptNames.TryGetValue(g.Key.Value, out var name) ? name : "Sem departamento",
                g.Count(),
                Math.Round(g.Average(r => r.Rating), 2)))
            .OrderByDescending(g => g.Count)
            .ToList();

        var userIds = ratedRows.Where(r => r.AssignedToUserId.HasValue).Select(r => r.AssignedToUserId!.Value).Distinct().ToList();
        var userNames = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Login })
            .ToListAsync(ct);
        var userMap = userNames.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Login : u.FullName);

        var byTechnician = ratedRows
            .GroupBy(r => r.AssignedToUserId)
            .Select(g => new TicketCsatGroupDto(
                g.Key?.ToString(),
                g.Key.HasValue && userMap.TryGetValue(g.Key.Value, out var name) ? name : "Não atribuído",
                g.Count(),
                Math.Round(g.Average(r => r.Rating), 2)))
            .OrderByDescending(g => g.Count)
            .ToList();

        return Result<TicketCsatSummaryDto>.Success(new TicketCsatSummaryDto(
            total, ratedRows.Count, average, distribution, byDepartment, byTechnician));
    }
}
