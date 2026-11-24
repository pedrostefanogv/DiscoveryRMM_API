using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Users.Queries;
using Discovery.Core.DTOs.Users;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Users;

/// <summary>
/// Consolida o histórico de acessos e atividades de um usuário em múltiplas fontes
/// do sistema (autenticação, identidade, sessões remotas, chamados, configurações,
/// automação, aprovações e relatórios).
///
/// Todas as consultas sobre <see cref="DiscoveryDbContext"/> são executadas
/// sequencialmente (DbContext não é thread-safe).
/// </summary>
public sealed class GetUserActivityQueryHandler(
    DiscoveryDbContext db
) : IRequestHandler<GetUserActivityQuery, Result<UserActivityResponseDto>>
{
    public async Task<Result<UserActivityResponseDto>> Handle(GetUserActivityQuery q, CancellationToken ct)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == q.UserId, ct);

        if (user is null)
            return Result<UserActivityResponseDto>.Failure(Error.NotFound($"User {q.UserId} not found"));

        var actorKeys = new[] { user.Login, user.Email, user.Id.ToString() }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        var category = q.Category?.Trim().ToLowerInvariant();
        var includeAll = string.IsNullOrEmpty(category) || category == "all";
        var limit = Math.Clamp(q.Limit, 1, 200);
        var perSourceLimit = includeAll ? Math.Min(limit, 50) : limit;

        var items = new List<UserActivityItemDto>();

        if (includeAll || category == "auth")
        {
            var authLogs = await db.AuthAuditLogs
                .AsNoTracking()
                .Where(l => l.UserId == user.Id && !l.EventType.StartsWith("identity."))
                .OrderByDescending(l => l.OccurredAt)
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var l in authLogs)
            {
                items.Add(new UserActivityItemDto
                {
                    Id = $"auth:{l.Id}",
                    Category = "auth",
                    EventType = l.EventType,
                    Title = DescribeAuthEvent(l.EventType, l.Success),
                    Summary = l.Detail ?? l.FailureReason,
                    Success = l.Success,
                    FailureReason = l.FailureReason,
                    IpAddress = l.IpAddress,
                    UserAgent = l.UserAgent,
                    ActorLogin = l.ActorLogin ?? user.Login,
                    Role = "self",
                    TargetType = "User",
                    TargetId = user.Id.ToString(),
                    Details = l.Detail,
                    OccurredAt = l.OccurredAt,
                });
            }
        }

        if (includeAll || category == "identity")
        {
            var identityLogs = await db.AuthAuditLogs
                .AsNoTracking()
                .Where(l => l.EventType.StartsWith("identity.")
                            && (l.ActorUserId == user.Id
                                || l.UserId == user.Id
                                || (l.TargetType == "User" && l.TargetId == user.Id)))
                .OrderByDescending(l => l.OccurredAt)
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var l in identityLogs)
            {
                var isActor = l.ActorUserId == user.Id;
                items.Add(new UserActivityItemDto
                {
                    Id = $"identity:{l.Id}",
                    Category = "identity",
                    EventType = l.EventType,
                    Title = DescribeIdentityEvent(l.EventType),
                    Summary = l.FailureReason ?? (!string.IsNullOrWhiteSpace(l.ActorLogin)
                        ? (isActor ? "Executado pelo usuário" : $"Alterado por {l.ActorLogin}")
                        : null),
                    Success = l.Success,
                    FailureReason = l.FailureReason,
                    IpAddress = l.IpAddress,
                    UserAgent = l.UserAgent,
                    ActorLogin = l.ActorLogin,
                    Role = isActor ? "actor" : "target",
                    TargetType = l.TargetType,
                    TargetId = l.TargetId?.ToString(),
                    Details = FormatBeforeAfter(l.BeforeState, l.AfterState),
                    OccurredAt = l.OccurredAt,
                });
            }
        }

        if (includeAll || category == "remote")
        {
            var remoteRows = await (
                from rs in db.RemoteSessions.AsNoTracking()
                join a in db.Agents.AsNoTracking() on rs.AgentId equals a.Id into agentGroup
                from agent in agentGroup.DefaultIfEmpty()
                where rs.UserId == user.Id
                orderby rs.StartedAt descending
                select new
                {
                    rs.Id,
                    rs.AgentId,
                    rs.Kind,
                    rs.Status,
                    rs.StartedAt,
                    rs.ClosedAt,
                    rs.DurationSeconds,
                    rs.ConsentOutcome,
                    rs.Note,
                    AgentName = agent != null ? (agent.DisplayName ?? agent.Hostname) : null,
                })
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var r in remoteRows)
            {
                var machineLabel = !string.IsNullOrWhiteSpace(r.AgentName)
                    ? r.AgentName
                    : r.AgentId.ToString()[..8];
                var summaryParts = new List<string> { $"Tipo: {r.Kind}", $"Status: {r.Status}" };
                if (r.DurationSeconds.HasValue)
                    summaryParts.Add($"Duração: {r.DurationSeconds.Value}s");
                if (!string.IsNullOrWhiteSpace(r.ConsentOutcome))
                    summaryParts.Add($"Consentimento: {r.ConsentOutcome}");

                items.Add(new UserActivityItemDto
                {
                    Id = $"remote:{r.Id}",
                    Category = "remote",
                    EventType = $"remote.{r.Kind.ToString().ToLowerInvariant()}",
                    Title = $"Sessão remota ({r.Kind}) em {machineLabel}",
                    Summary = string.Join(" · ", summaryParts),
                    Success = !string.Equals(r.ConsentOutcome, "denied", StringComparison.OrdinalIgnoreCase),
                    ActorLogin = user.Login,
                    Role = "actor",
                    TargetType = "Agent",
                    TargetId = r.AgentId.ToString(),
                    LinkPath = $"/agents/{r.AgentId}",
                    Details = !string.IsNullOrWhiteSpace(r.Note) ? $"Nota: {r.Note}" : null,
                    OccurredAt = r.StartedAt,
                });
            }
        }

        if (includeAll || category == "tickets")
        {
            var ticketRows = await (
                from t in db.TicketActivityLogs.AsNoTracking()
                join ticket in db.Tickets.AsNoTracking() on t.TicketId equals ticket.Id into ticketGroup
                from tk in ticketGroup.DefaultIfEmpty()
                where t.ChangedByUserId == user.Id
                orderby t.CreatedAt descending
                select new
                {
                    t.Id,
                    t.TicketId,
                    t.Type,
                    t.OldValue,
                    t.NewValue,
                    t.Comment,
                    t.CreatedAt,
                    TicketTitle = tk != null ? tk.Title : null,
                })
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var t in ticketRows)
            {
                var titleLabel = !string.IsNullOrWhiteSpace(t.TicketTitle)
                    ? t.TicketTitle
                    : t.TicketId.ToString()[..8];
                var changeText = !string.IsNullOrWhiteSpace(t.OldValue) || !string.IsNullOrWhiteSpace(t.NewValue)
                    ? $"{t.OldValue ?? "—"} → {t.NewValue ?? "—"}"
                    : t.Comment;

                items.Add(new UserActivityItemDto
                {
                    Id = $"ticket:{t.Id}",
                    Category = "tickets",
                    EventType = $"ticket.{t.Type}",
                    Title = $"Chamado ({t.Type}): {titleLabel}",
                    Summary = changeText,
                    Success = true,
                    ActorLogin = user.Login,
                    Role = "actor",
                    TargetType = "Ticket",
                    TargetId = t.TicketId.ToString(),
                    LinkPath = $"/tickets/{t.TicketId}",
                    Details = t.Comment,
                    OccurredAt = t.CreatedAt,
                });
            }
        }

        if (includeAll || category == "configuration")
        {
            var configRows = await db.ConfigurationAudits
                .AsNoTracking()
                .Where(c => c.ChangedBy != null && actorKeys.Contains(c.ChangedBy.ToLower()))
                .OrderByDescending(c => c.ChangedAt)
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var c in configRows)
            {
                items.Add(new UserActivityItemDto
                {
                    Id = $"config:{c.Id}",
                    Category = "configuration",
                    EventType = $"configuration.{c.EntityType.ToString().ToLowerInvariant()}",
                    Title = $"Configuração ({c.EntityType}): {c.FieldName}",
                    Summary = !string.IsNullOrWhiteSpace(c.Reason)
                        ? c.Reason
                        : $"{c.OldValue ?? "—"} → {c.NewValue ?? "—"}",
                    Success = true,
                    IpAddress = c.IpAddress,
                    ActorLogin = c.ChangedBy,
                    Role = "actor",
                    TargetType = c.EntityType.ToString(),
                    TargetId = c.EntityId.ToString(),
                    Details = FormatBeforeAfter(c.OldValue, c.NewValue),
                    OccurredAt = c.ChangedAt,
                });
            }
        }

        if (includeAll || category == "automation")
        {
            var scriptRows = await db.AutomationScriptAudits
                .AsNoTracking()
                .Where(a => a.ChangedBy != null && actorKeys.Contains(a.ChangedBy.ToLower()))
                .OrderByDescending(a => a.ChangedAt)
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var a in scriptRows)
            {
                items.Add(new UserActivityItemDto
                {
                    Id = $"script-audit:{a.Id}",
                    Category = "automation",
                    EventType = $"automation.script.{a.ChangeType.ToString().ToLowerInvariant()}",
                    Title = $"Script de automação ({a.ChangeType})",
                    Summary = a.Reason,
                    Success = true,
                    IpAddress = a.IpAddress,
                    ActorLogin = a.ChangedBy,
                    Role = "actor",
                    TargetType = "AutomationScript",
                    TargetId = a.ScriptId.ToString(),
                    LinkPath = $"/automation/scripts/{a.ScriptId}",
                    Details = FormatBeforeAfter(a.OldValueJson, a.NewValueJson),
                    OccurredAt = a.ChangedAt,
                });
            }

            var taskRows = await db.AutomationTaskAudits
                .AsNoTracking()
                .Where(a => a.ChangedBy != null && actorKeys.Contains(a.ChangedBy.ToLower()))
                .OrderByDescending(a => a.ChangedAt)
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var a in taskRows)
            {
                items.Add(new UserActivityItemDto
                {
                    Id = $"task-audit:{a.Id}",
                    Category = "automation",
                    EventType = $"automation.task.{a.ChangeType.ToString().ToLowerInvariant()}",
                    Title = $"Tarefa de automação ({a.ChangeType})",
                    Summary = a.Reason,
                    Success = true,
                    IpAddress = a.IpAddress,
                    ActorLogin = a.ChangedBy,
                    Role = "actor",
                    TargetType = "AutomationTask",
                    TargetId = a.TaskId.ToString(),
                    Details = FormatBeforeAfter(a.OldValueJson, a.NewValueJson),
                    OccurredAt = a.ChangedAt,
                });
            }
        }

        if (includeAll || category == "approvals")
        {
            var patchRows = await db.PatchApprovalAudits
                .AsNoTracking()
                .Where(p => p.ChangedBy != null && actorKeys.Contains(p.ChangedBy.ToLower()))
                .OrderByDescending(p => p.ChangedAt)
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var p in patchRows)
            {
                var transition = p.OldAction.HasValue || p.NewAction.HasValue
                    ? $"{p.OldAction?.ToString() ?? "—"} → {p.NewAction?.ToString() ?? "—"}"
                    : null;

                items.Add(new UserActivityItemDto
                {
                    Id = $"patch-approval:{p.Id}",
                    Category = "approvals",
                    EventType = $"patch.{p.ChangeType.ToString().ToLowerInvariant()}",
                    Title = $"Aprovação de patch ({p.ChangeType}): {p.KbId ?? p.UpdateKey}",
                    Summary = !string.IsNullOrWhiteSpace(p.Reason) ? p.Reason : transition,
                    Success = true,
                    IpAddress = p.IpAddress,
                    ActorLogin = p.ChangedBy,
                    Role = "actor",
                    TargetType = "PatchApproval",
                    TargetId = (p.RuleId ?? p.Id).ToString(),
                    Details = transition,
                    OccurredAt = p.ChangedAt,
                });
            }

            var appRows = await db.AppApprovalAudits
                .AsNoTracking()
                .Where(a => a.ChangedBy != null && actorKeys.Contains(a.ChangedBy.ToLower()))
                .OrderByDescending(a => a.ChangedAt)
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var a in appRows)
            {
                var transition = a.OldAction.HasValue || a.NewAction.HasValue
                    ? $"{a.OldAction?.ToString() ?? "—"} → {a.NewAction?.ToString() ?? "—"}"
                    : null;

                items.Add(new UserActivityItemDto
                {
                    Id = $"app-approval:{a.Id}",
                    Category = "approvals",
                    EventType = $"app.{a.ChangeType.ToString().ToLowerInvariant()}",
                    Title = $"Aprovação de aplicativo ({a.ChangeType}): {a.PackageId}",
                    Summary = !string.IsNullOrWhiteSpace(a.Reason) ? a.Reason : transition,
                    Success = true,
                    IpAddress = a.IpAddress,
                    ActorLogin = a.ChangedBy,
                    Role = "actor",
                    TargetType = "AppApproval",
                    TargetId = (a.RuleId ?? a.Id).ToString(),
                    Details = transition,
                    OccurredAt = a.ChangedAt,
                });
            }
        }

        if (includeAll || category == "reports")
        {
            var reportRows = await db.ReportExecutions
                .AsNoTracking()
                .Where(r => r.CreatedBy != null && actorKeys.Contains(r.CreatedBy.ToLower()))
                .OrderByDescending(r => r.CreatedAt)
                .Take(perSourceLimit)
                .ToListAsync(ct);

            foreach (var r in reportRows)
            {
                var summaryParts = new List<string> { $"Formato: {r.Format}", $"Status: {r.Status}" };
                if (r.RowCount.HasValue)
                    summaryParts.Add($"Linhas: {r.RowCount.Value}");

                items.Add(new UserActivityItemDto
                {
                    Id = $"report:{r.Id}",
                    Category = "reports",
                    EventType = "report.executed",
                    Title = $"Execução de relatório ({r.Format})",
                    Summary = string.Join(" · ", summaryParts),
                    Success = string.IsNullOrWhiteSpace(r.ErrorMessage),
                    FailureReason = r.ErrorMessage,
                    ActorLogin = r.CreatedBy,
                    Role = "actor",
                    TargetType = "ReportExecution",
                    TargetId = r.Id.ToString(),
                    LinkPath = "/reports/executions",
                    Details = r.ErrorMessage,
                    OccurredAt = r.CreatedAt,
                });
            }
        }

        var categoryCounts = items
            .GroupBy(i => i.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        var ordered = items
            .OrderByDescending(i => i.OccurredAt)
            .Take(limit)
            .ToList()
            .AsReadOnly();

        return Result<UserActivityResponseDto>.Success(new UserActivityResponseDto
        {
            Items = ordered,
            CategoryCounts = categoryCounts,
            TotalReturned = ordered.Count,
        });
    }

    private static string DescribeAuthEvent(string eventType, bool success) => eventType switch
    {
        "login_success" => "Login realizado com sucesso",
        "login_failed" => "Tentativa de login falha",
        "logout" => "Logout realizado",
        "step_up_granted" => "Verificação MFA concluída",
        "step_up_failed" => "Falha na verificação MFA",
        "session_created" => "Sessão autenticada criada",
        "account_locked" => "Conta bloqueada por tentativas inválidas",
        _ => success ? $"Evento de autenticação: {eventType}" : $"Falha de autenticação: {eventType}",
    };

    private static string DescribeIdentityEvent(string eventType) => eventType switch
    {
        "identity.user_created" => "Usuário criado",
        "identity.user_updated" => "Dados do usuário atualizados",
        "identity.user_deactivated" => "Usuário desativado",
        "identity.user_password_reset" => "Senha redefinida por administrador",
        "identity.user_force_password_reset" => "Troca de senha forçada no próximo login",
        "identity.user_mfa_revoked" => "Todas as chaves MFA revogadas",
        "identity.user_mfa_key_revoked" => "Chave MFA removida",
        "identity.user_session_revoked" => "Sessão encerrada por administrador",
        "identity.user_sessions_revoked_all" => "Todas as sessões encerradas por administrador",
        "identity.self_password_changed" => "Senha alterada pelo próprio usuário",
        "identity.self_profile_updated" => "Perfil atualizado pelo próprio usuário",
        "identity.group_member_added" => "Adicionado a um grupo de acesso",
        "identity.group_member_removed" => "Removido de um grupo de acesso",
        "identity.role_scope_granted" => "Papel e escopo concedidos",
        "identity.role_scope_revoked" => "Papel e escopo revogados",
        _ => eventType,
    };

    private static string? FormatBeforeAfter(string? before, string? after)
    {
        if (string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
            return null;

        try
        {
            return JsonSerializer.Serialize(new
            {
                before = string.IsNullOrWhiteSpace(before) ? null : before,
                after = string.IsNullOrWhiteSpace(after) ? null : after,
            });
        }
        catch
        {
            return $"{before} -> {after}";
        }
    }
}
