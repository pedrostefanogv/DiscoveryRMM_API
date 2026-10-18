using System.Text.Json;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Datasets de relatório do gerenciador de patches (Windows Update) e do
/// inventário/estado dos agentes.
///
/// Mantidos em arquivo parcial para não engordar o switch principal: as regras de
/// filtro/ordenação seguem o mesmo padrão dos demais datasets (filtros JSON,
/// limite com teto duro e enriquecimento de labels automáticas).
/// </summary>
public partial class ReportDatasetQueryService
{
    private const int PatchDatasetCandidateMultiplier = 4;

    // ── Windows Updates (inventário por agente/atualização) ─────────────────

    private async Task<ReportQueryResult> QueryWindowsUpdatesAsync(
        Guid? clientId,
        JsonElement filters,
        CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var updateKey = GetString(filters, "updateKey");
        var kb = GetString(filters, "kb");
        var severity = GetString(filters, "severity");
        var status = ParsePatchUpdateStatus(GetString(filters, "status"));
        var approvalState = ParsePatchApprovalState(GetString(filters, "approvalState"));
        var onlyPending = GetBool(filters, "onlyPending");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var orderBy = GetEnum(filters, "orderBy", WindowsUpdatesOrderBy.AgentHostname);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query =
            from update in _db.AgentWindowsUpdates.AsNoTracking()
            join agent in _db.Agents.AsNoTracking() on update.AgentId equals agent.Id
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in _db.Clients.AsNoTracking() on site.ClientId equals client.Id
            where update.IsPresent
                  && agent.DeletedAt == null
                  && (!clientId.HasValue || site.ClientId == clientId.Value)
            select new
            {
                ClientId = client.Id,
                ClientName = client.Name,
                SiteId = site.Id,
                SiteName = site.Name,
                AgentId = agent.Id,
                AgentHostname = agent.Hostname,
                update.UpdateKey,
                update.GroupKey,
                update.KbIds,
                update.Title,
                update.Classification,
                update.Severity,
                update.IsPending,
                update.IsInstalled,
                update.IsHidden,
                update.RequiresReboot,
                update.ApprovalState,
                update.ApprovalSource,
                update.Status,
                update.PublishedAt,
                update.ReportedAt,
                update.InstalledAt,
                update.InstallResultCode,
                update.InstallError,
                update.SupportUrl
            };

        if (siteId.HasValue)
            query = query.Where(row => row.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(row => row.AgentId == agentId.Value);
        // ToLower/Contains em vez de ILike: mantém a consulta traduzível no
        // Npgsql e no provider InMemory (testes).
        if (!string.IsNullOrWhiteSpace(updateKey))
        {
            var needle = updateKey.Trim().ToLowerInvariant();
            query = query.Where(row => row.UpdateKey.ToLower().Contains(needle));
        }
        if (!string.IsNullOrWhiteSpace(kb))
        {
            var needle = kb.Trim().ToLowerInvariant();
            query = query.Where(row => row.KbIds != null && row.KbIds.ToLower().Contains(needle));
        }
        if (!string.IsNullOrWhiteSpace(severity))
        {
            var needle = severity.Trim().ToLowerInvariant();
            query = query.Where(row => row.Severity != null && row.Severity.ToLower() == needle);
        }
        if (status.HasValue)
            query = query.Where(row => row.Status == status.Value);
        if (approvalState.HasValue)
            query = query.Where(row => row.ApprovalState == approvalState.Value);
        if (onlyPending == true)
            query = query.Where(row => row.IsPending);
        if (from.HasValue)
            query = query.Where(row => row.ReportedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(row => row.ReportedAt <= to.Value);

        var ordered = orderBy switch
        {
            WindowsUpdatesOrderBy.Severity => descending
                ? query.OrderByDescending(row => row.Severity).ThenByDescending(row => row.AgentHostname)
                : query.OrderBy(row => row.Severity).ThenBy(row => row.AgentHostname),
            WindowsUpdatesOrderBy.Title => descending
                ? query.OrderByDescending(row => row.Title).ThenByDescending(row => row.AgentHostname)
                : query.OrderBy(row => row.Title).ThenBy(row => row.AgentHostname),
            WindowsUpdatesOrderBy.ReportedAt => descending
                ? query.OrderByDescending(row => row.ReportedAt).ThenByDescending(row => row.AgentHostname)
                : query.OrderBy(row => row.ReportedAt).ThenBy(row => row.AgentHostname),
            WindowsUpdatesOrderBy.PublishedAt => descending
                ? query.OrderByDescending(row => row.PublishedAt).ThenByDescending(row => row.AgentHostname)
                : query.OrderBy(row => row.PublishedAt).ThenBy(row => row.AgentHostname),
            WindowsUpdatesOrderBy.SiteName => descending
                ? query.OrderByDescending(row => row.SiteName).ThenByDescending(row => row.AgentHostname)
                : query.OrderBy(row => row.SiteName).ThenBy(row => row.AgentHostname),
            _ => descending
                ? query.OrderByDescending(row => row.AgentHostname).ThenByDescending(row => row.Title)
                : query.OrderBy(row => row.AgentHostname).ThenBy(row => row.Title)
        };

        var rowsRaw = await ordered.Take(limit).ToListAsync(cancellationToken);
        var labelsByAgent = await GetAutomaticLabelsByAgentAsync(
            rowsRaw.Select(row => row.AgentId), cancellationToken);

        var rows = rowsRaw
            .Select(row =>
            {
                var kbIds = PatchJson.ParseStringArray(row.KbIds);
                return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["clientId"] = row.ClientId,
                    ["clientName"] = row.ClientName,
                    ["siteId"] = row.SiteId,
                    ["siteName"] = row.SiteName,
                    ["agentId"] = row.AgentId,
                    ["agentHostname"] = row.AgentHostname,
                    ["automaticLabels"] = labelsByAgent.TryGetValue(row.AgentId, out var labels) ? labels : string.Empty,
                    ["updateKey"] = row.UpdateKey,
                    ["kbIds"] = string.Join(", ", kbIds),
                    ["kbId"] = kbIds.Count > 0 ? kbIds[0] : null,
                    ["title"] = row.Title,
                    ["classification"] = row.Classification,
                    ["severity"] = row.Severity,
                    ["isPending"] = row.IsPending,
                    ["isInstalled"] = row.IsInstalled,
                    ["isHidden"] = row.IsHidden,
                    ["requiresReboot"] = row.RequiresReboot,
                    ["approvalState"] = row.ApprovalState.ToString(),
                    ["approvalSource"] = row.ApprovalSource,
                    ["status"] = row.Status.ToString(),
                    ["publishedAt"] = row.PublishedAt,
                    ["reportedAt"] = row.ReportedAt,
                    ["installedAt"] = row.InstalledAt,
                    ["installResultCode"] = row.InstallResultCode,
                    ["installError"] = row.InstallError,
                    ["supportUrl"] = row.SupportUrl
                };
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns =
            [
                "clientName", "siteName", "agentId", "agentHostname", "automaticLabels",
                "updateKey", "kbIds", "title", "classification", "severity",
                "isPending", "isInstalled", "isHidden", "requiresReboot",
                "approvalState", "approvalSource", "status",
                "publishedAt", "reportedAt", "installedAt",
                "installResultCode", "installError", "supportUrl"
            ],
            Rows = rows
        };
    }

    // ── Conformidade de patches por agente ──────────────────────────────────

    private async Task<ReportQueryResult> QueryPatchComplianceAsync(
        Guid? clientId,
        JsonElement filters,
        CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var pendingReboot = GetBool(filters, "pendingReboot");
        var scanStatus = GetString(filters, "scanStatus");
        var onlyWithPending = GetBool(filters, "onlyWithPending");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var orderBy = GetEnum(filters, "orderBy", PatchComplianceOrderBy.AgentHostname);
        var descending = GetSortDescending(filters, defaultValue: false);

        // Ordenação por contadores é aplicada em memória (após calcular os
        // contadores); demais ordens vão para o SQL.
        var sqlOrderable = orderBy is PatchComplianceOrderBy.AgentHostname
            or PatchComplianceOrderBy.SiteName or PatchComplianceOrderBy.LastScanAt;

        var query =
            from agent in _db.Agents.AsNoTracking()
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in _db.Clients.AsNoTracking() on site.ClientId equals client.Id
            join snapshot in _db.AgentWindowsUpdateSnapshots.AsNoTracking()
                on agent.Id equals snapshot.AgentId into snapshots
            from snapshot in snapshots.DefaultIfEmpty()
            where agent.DeletedAt == null
                  && (!clientId.HasValue || site.ClientId == clientId.Value)
            select new
            {
                ClientId = client.Id,
                ClientName = client.Name,
                SiteId = site.Id,
                SiteName = site.Name,
                AgentId = agent.Id,
                AgentHostname = agent.Hostname,
                AgentDisplayName = agent.DisplayName,
                OsName = agent.OperatingSystem,
                OsVersion = agent.OsVersion,
                AgentVersion = agent.AgentVersion,
                agent.LastSeenAt,
                MaintenanceEnabled = agent.MaintenanceEnabled,
                AgentStatus = agent.Status,
                PendingReboot = snapshot != null && snapshot.PendingReboot,
                RebootReasons = snapshot == null ? null : snapshot.PendingRebootReasons,
                ScanStatus = snapshot == null ? "unknown" : snapshot.ScanStatus,
                LastScanAt = snapshot == null ? null : snapshot.CollectedAt,
                InstalledSnapshotCount = snapshot == null ? 0 : snapshot.InstalledCount
            };

        if (siteId.HasValue)
            query = query.Where(row => row.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(row => row.AgentId == agentId.Value);
        if (pendingReboot.HasValue)
            query = query.Where(row => row.PendingReboot == pendingReboot.Value);
        if (!string.IsNullOrWhiteSpace(scanStatus))
        {
            var needle = scanStatus.Trim().ToLowerInvariant();
            query = query.Where(row => row.ScanStatus.ToLower() == needle);
        }
        if (from.HasValue)
            query = query.Where(row => row.LastScanAt >= from.Value);
        if (to.HasValue)
            query = query.Where(row => row.LastScanAt <= to.Value);

        if (sqlOrderable)
        {
            query = orderBy switch
            {
                PatchComplianceOrderBy.SiteName => descending
                    ? query.OrderByDescending(row => row.SiteName).ThenByDescending(row => row.AgentHostname)
                    : query.OrderBy(row => row.SiteName).ThenBy(row => row.AgentHostname),
                PatchComplianceOrderBy.LastScanAt => descending
                    ? query.OrderByDescending(row => row.LastScanAt).ThenByDescending(row => row.AgentHostname)
                    : query.OrderBy(row => row.LastScanAt).ThenBy(row => row.AgentHostname),
                _ => descending
                    ? query.OrderByDescending(row => row.AgentHostname)
                    : query.OrderBy(row => row.AgentHostname)
            };
        }
        else
        {
            query = query.OrderBy(row => row.AgentHostname);
        }

        var candidateLimit = onlyWithPending == true
            ? Math.Min(GetLimit(filters) * PatchDatasetCandidateMultiplier, MaxLimit)
            : limit;

        var rowsRaw = await query.Take(candidateLimit).ToListAsync(cancellationToken);
        var agentIds = rowsRaw.Select(row => row.AgentId).ToList();
        var labelsByAgent = await GetAutomaticLabelsByAgentAsync(agentIds, cancellationToken);

        var counts = await GetPatchCountsByAgentAsync(agentIds, cancellationToken);

        var rows = rowsRaw.Select(row =>
        {
            counts.TryGetValue(row.AgentId, out var agentCounts);
            agentCounts ??= new PatchCounts(0, 0, 0, 0, row.InstalledSnapshotCount);
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["clientId"] = row.ClientId,
                ["clientName"] = row.ClientName,
                ["siteId"] = row.SiteId,
                ["siteName"] = row.SiteName,
                ["agentId"] = row.AgentId,
                ["agentHostname"] = row.AgentHostname,
                ["agentDisplayName"] = row.AgentDisplayName,
                ["automaticLabels"] = labelsByAgent.TryGetValue(row.AgentId, out var labels) ? labels : string.Empty,
                ["status"] = row.MaintenanceEnabled
                    ? "Maintenance"
                    : row.AgentStatus == AgentStatus.Online ? "Online" : row.AgentStatus.ToString(),
                ["osName"] = row.OsName,
                ["osVersion"] = row.OsVersion,
                ["agentVersion"] = row.AgentVersion,
                ["pendingCount"] = agentCounts.Pending,
                ["criticalCount"] = agentCounts.Critical,
                ["deniedCount"] = agentCounts.Denied,
                ["approvedCount"] = agentCounts.Approved,
                ["installedCount"] = agentCounts.Installed,
                ["pendingReboot"] = row.PendingReboot,
                ["pendingRebootReasons"] = FormatRebootReasons(row.RebootReasons),
                ["scanStatus"] = row.ScanStatus,
                ["lastScanAt"] = row.LastScanAt,
                ["lastSeenAt"] = row.LastSeenAt
            };
        }).ToList();

        if (onlyWithPending == true)
        {
            rows = rows
                .Where(row => Convert.ToInt32(row["pendingCount"] ?? 0) > 0)
                .ToList();
        }

        if (!sqlOrderable)
        {
            rows = orderBy switch
            {
                PatchComplianceOrderBy.PendingCount => descending
                    ? rows.OrderByDescending(row => Convert.ToInt32(row["pendingCount"] ?? 0)).ToList()
                    : rows.OrderBy(row => Convert.ToInt32(row["pendingCount"] ?? 0)).ToList(),
                PatchComplianceOrderBy.CriticalCount => descending
                    ? rows.OrderByDescending(row => Convert.ToInt32(row["criticalCount"] ?? 0)).ToList()
                    : rows.OrderBy(row => Convert.ToInt32(row["criticalCount"] ?? 0)).ToList(),
                _ => rows
            };
        }

        rows = rows.Take(limit).ToList();

        return new ReportQueryResult
        {
            Columns =
            [
                "clientName", "siteName", "agentId", "agentHostname", "agentDisplayName",
                "automaticLabels", "status", "osName", "osVersion", "agentVersion",
                "pendingCount", "criticalCount", "deniedCount", "approvedCount", "installedCount",
                "pendingReboot", "pendingRebootReasons", "scanStatus", "lastScanAt", "lastSeenAt"
            ],
            Rows = rows
        };
    }

    private async Task<Dictionary<Guid, PatchCounts>> GetPatchCountsByAgentAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
            return new Dictionary<Guid, PatchCounts>();

        var counts = await _db.AgentWindowsUpdates
            .AsNoTracking()
            .Where(update => agentIds.Contains(update.AgentId) && update.IsPresent)
            .GroupBy(update => update.AgentId)
            .Select(group => new
            {
                AgentId = group.Key,
                Pending = group.Count(update => update.IsPending &&
                    update.ApprovalState != PatchApprovalState.Denied),
                // ToLower() em vez de ILike: o agregado precisa ser traduzível
                // tanto no Npgsql quanto no provider InMemory dos testes.
                Critical = group.Count(update => update.IsPending &&
                    update.ApprovalState != PatchApprovalState.Denied &&
                    update.Severity != null &&
                    update.Severity.ToLower() == "critical"),
                Denied = group.Count(update => update.IsPending &&
                    update.ApprovalState == PatchApprovalState.Denied),
                Approved = group.Count(update => update.IsPending &&
                    (update.ApprovalState == PatchApprovalState.Approved ||
                     update.ApprovalState == PatchApprovalState.AutoApproved)),
                Installed = group.Count(update => update.IsInstalled)
            })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(
            item => item.AgentId,
            item => new PatchCounts(item.Pending, item.Critical, item.Denied, item.Approved, item.Installed));
    }

    private sealed record PatchCounts(int Pending, int Critical, int Denied, int Approved, int Installed);

    // ── Aprovações de patches ───────────────────────────────────────────────

    private async Task<ReportQueryResult> QueryPatchApprovalsAsync(
        Guid? clientId,
        JsonElement filters,
        CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var scopeType = ParsePatchScopeType(GetString(filters, "scopeType"));
        var action = ParsePatchAction(GetString(filters, "action"));
        var updateKey = GetString(filters, "updateKey");
        var kb = GetString(filters, "kb");
        var changedBy = GetString(filters, "changedBy");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");

        var query = _db.PatchApprovals.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
        {
            query = query.Where(rule =>
                rule.ScopeType == PatchApprovalScopeType.Global || rule.ClientId == clientId.Value);
        }

        if (scopeType.HasValue)
            query = query.Where(rule => rule.ScopeType == scopeType.Value);
        if (action.HasValue)
            query = query.Where(rule => rule.Action == action.Value);
        if (!string.IsNullOrWhiteSpace(updateKey))
        {
            var needle = updateKey.Trim().ToLowerInvariant();
            query = query.Where(rule => rule.UpdateKey.ToLower().Contains(needle));
        }
        if (!string.IsNullOrWhiteSpace(kb))
        {
            var needle = kb.Trim().ToLowerInvariant();
            query = query.Where(rule => rule.KbId != null && rule.KbId.ToLower().Contains(needle));
        }
        if (!string.IsNullOrWhiteSpace(changedBy))
        {
            var needle = changedBy.Trim().ToLowerInvariant();
            query = query.Where(rule =>
                (rule.UpdatedBy != null && rule.UpdatedBy.ToLower().Contains(needle)) ||
                (rule.CreatedBy != null && rule.CreatedBy.ToLower().Contains(needle)));
        }
        if (from.HasValue)
            query = query.Where(rule => rule.UpdatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(rule => rule.UpdatedAt <= to.Value);

        var rowsRaw = await query
            .OrderByDescending(rule => rule.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = rowsRaw
            .Select(rule => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["scopeType"] = rule.ScopeType.ToString(),
                ["scopeId"] = rule.ScopeId,
                ["clientId"] = rule.ClientId,
                ["siteId"] = rule.SiteId,
                ["agentId"] = rule.AgentId,
                ["updateKey"] = rule.UpdateKey,
                ["kbId"] = rule.KbId,
                ["action"] = rule.Action.ToString(),
                ["reason"] = rule.Reason,
                ["createdBy"] = rule.CreatedBy,
                ["updatedBy"] = rule.UpdatedBy,
                ["createdAt"] = rule.CreatedAt,
                ["updatedAt"] = rule.UpdatedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns =
            [
                "scopeType", "scopeId", "clientId", "siteId", "agentId",
                "updateKey", "kbId", "action", "reason",
                "createdBy", "updatedBy", "createdAt", "updatedAt"
            ],
            Rows = rows
        };
    }

    // ── Auditoria de aprovações ─────────────────────────────────────────────

    private async Task<ReportQueryResult> QueryPatchApprovalAuditAsync(
        Guid? clientId,
        JsonElement filters,
        CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var scopeType = ParsePatchScopeType(GetString(filters, "scopeType"));
        var action = ParsePatchAction(GetString(filters, "action"));
        var changeType = GetString(filters, "changeType");
        var updateKey = GetString(filters, "updateKey");
        var kb = GetString(filters, "kb");
        var changedBy = GetString(filters, "changedBy");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");

        var query = _db.PatchApprovalAudits.AsNoTracking().AsQueryable();

        if (clientId.HasValue)
        {
            query = query.Where(audit =>
                audit.ScopeType == PatchApprovalScopeType.Global || audit.ClientId == clientId.Value);
        }

        if (scopeType.HasValue)
            query = query.Where(audit => audit.ScopeType == scopeType.Value);
        if (action.HasValue)
            query = query.Where(audit => audit.NewAction == action.Value);
        var changeTypeFilter = ParsePatchAuditChangeType(changeType);
        if (changeTypeFilter.HasValue)
            query = query.Where(audit => audit.ChangeType == changeTypeFilter.Value);
        if (!string.IsNullOrWhiteSpace(updateKey))
        {
            var needle = updateKey.Trim().ToLowerInvariant();
            query = query.Where(audit => audit.UpdateKey.ToLower().Contains(needle));
        }
        if (!string.IsNullOrWhiteSpace(kb))
        {
            var needle = kb.Trim().ToLowerInvariant();
            query = query.Where(audit => audit.KbId != null && audit.KbId.ToLower().Contains(needle));
        }
        if (!string.IsNullOrWhiteSpace(changedBy))
        {
            var needle = changedBy.Trim().ToLowerInvariant();
            query = query.Where(audit => audit.ChangedBy != null &&
                audit.ChangedBy.ToLower().Contains(needle));
        }
        if (from.HasValue)
            query = query.Where(audit => audit.ChangedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(audit => audit.ChangedAt <= to.Value);

        var rowsRaw = await query
            .OrderByDescending(audit => audit.ChangedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = rowsRaw
            .Select(audit => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["changeType"] = audit.ChangeType.ToString(),
                ["scopeType"] = audit.ScopeType.ToString(),
                ["scopeId"] = audit.ScopeId,
                ["clientId"] = audit.ClientId,
                ["siteId"] = audit.SiteId,
                ["agentId"] = audit.AgentId,
                ["updateKey"] = audit.UpdateKey,
                ["kbId"] = audit.KbId,
                ["oldAction"] = audit.OldAction?.ToString(),
                ["newAction"] = audit.NewAction?.ToString(),
                ["reason"] = audit.Reason,
                ["changedBy"] = audit.ChangedBy,
                ["ipAddress"] = audit.IpAddress,
                ["changedAt"] = audit.ChangedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns =
            [
                "changeType", "scopeType", "scopeId", "clientId", "siteId", "agentId",
                "updateKey", "kbId", "oldAction", "newAction", "reason",
                "changedBy", "ipAddress", "changedAt"
            ],
            Rows = rows
        };
    }

    // ── Inventário/estado dos agentes ───────────────────────────────────────

    private async Task<ReportQueryResult> QueryAgentsAsync(
        Guid? clientId,
        JsonElement filters,
        CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var status = GetString(filters, "status");
        var osName = GetString(filters, "osName");
        var pendingReboot = GetBool(filters, "pendingReboot");
        var zeroTouchPending = GetBool(filters, "zeroTouchPending");
        var maintenance = GetBool(filters, "maintenance");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var orderBy = GetEnum(filters, "orderBy", AgentsOrderBy.Hostname);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query =
            from agent in _db.Agents.AsNoTracking()
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in _db.Clients.AsNoTracking() on site.ClientId equals client.Id
            where agent.DeletedAt == null
                  && (!clientId.HasValue || site.ClientId == clientId.Value)
            select new
            {
                ClientId = client.Id,
                ClientName = client.Name,
                SiteId = site.Id,
                SiteName = site.Name,
                AgentId = agent.Id,
                Hostname = agent.Hostname,
                DisplayName = agent.DisplayName,
                Status = agent.Status,
                MaintenanceEnabled = agent.MaintenanceEnabled,
                MaintenanceReason = agent.MaintenanceReason,
                OperatingSystem = agent.OperatingSystem,
                OsVersion = agent.OsVersion,
                AgentVersion = agent.AgentVersion,
                CommitHash = agent.CommitHash,
                LastIpAddress = agent.LastIpAddress,
                LoggedUser = agent.LoggedUser,
                LastSeenAt = agent.LastSeenAt,
                ZeroTouchPending = agent.ZeroTouchPending,
                PendingReboot = agent.PendingReboot,
                PendingRebootReasons = agent.PendingRebootReasons,
                PendingRebootCheckedAt = agent.PendingRebootCheckedAt,
                CreatedAt = agent.CreatedAt
            };

        if (siteId.HasValue)
            query = query.Where(row => row.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(row => row.AgentId == agentId.Value);
        if (!string.IsNullOrWhiteSpace(osName))
        {
            var needle = osName.Trim().ToLowerInvariant();
            query = query.Where(row => row.OperatingSystem != null &&
                row.OperatingSystem.ToLower().Contains(needle));
        }
        if (pendingReboot.HasValue)
            query = query.Where(row => row.PendingReboot == pendingReboot.Value);
        if (zeroTouchPending.HasValue)
            query = query.Where(row => row.ZeroTouchPending == zeroTouchPending.Value);
        if (maintenance.HasValue)
            query = query.Where(row => row.MaintenanceEnabled == maintenance.Value);
        if (from.HasValue)
            query = query.Where(row => row.LastSeenAt >= from.Value);
        if (to.HasValue)
            query = query.Where(row => row.LastSeenAt <= to.Value);

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = normalized switch
            {
                "online" => query.Where(row => !row.MaintenanceEnabled && row.Status == AgentStatus.Online),
                "offline" => query.Where(row => !row.MaintenanceEnabled && row.Status != AgentStatus.Online),
                "maintenance" => query.Where(row => row.MaintenanceEnabled),
                "error" => query.Where(row => row.Status == AgentStatus.Error),
                _ => query
            };
        }

        var ordered = orderBy switch
        {
            AgentsOrderBy.SiteName => descending
                ? query.OrderByDescending(row => row.SiteName).ThenByDescending(row => row.Hostname)
                : query.OrderBy(row => row.SiteName).ThenBy(row => row.Hostname),
            AgentsOrderBy.Status => descending
                ? query.OrderByDescending(row => row.Status).ThenByDescending(row => row.Hostname)
                : query.OrderBy(row => row.Status).ThenBy(row => row.Hostname),
            AgentsOrderBy.LastSeenAt => descending
                ? query.OrderByDescending(row => row.LastSeenAt).ThenByDescending(row => row.Hostname)
                : query.OrderBy(row => row.LastSeenAt).ThenBy(row => row.Hostname),
            AgentsOrderBy.OsName => descending
                ? query.OrderByDescending(row => row.OperatingSystem).ThenByDescending(row => row.Hostname)
                : query.OrderBy(row => row.OperatingSystem).ThenBy(row => row.Hostname),
            _ => descending
                ? query.OrderByDescending(row => row.Hostname)
                : query.OrderBy(row => row.Hostname)
        };

        var rowsRaw = await ordered.Take(limit).ToListAsync(cancellationToken);
        var labelsByAgent = await GetAutomaticLabelsByAgentAsync(
            rowsRaw.Select(row => row.AgentId), cancellationToken);

        var rows = rowsRaw
            .Select(row => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["clientId"] = row.ClientId,
                ["clientName"] = row.ClientName,
                ["siteId"] = row.SiteId,
                ["siteName"] = row.SiteName,
                ["agentId"] = row.AgentId,
                ["hostname"] = row.Hostname,
                ["displayName"] = row.DisplayName,
                ["automaticLabels"] = labelsByAgent.TryGetValue(row.AgentId, out var labels) ? labels : string.Empty,
                ["status"] = row.MaintenanceEnabled
                    ? "Maintenance"
                    : row.Status == AgentStatus.Online ? "Online" : row.Status.ToString(),
                ["maintenance"] = row.MaintenanceEnabled,
                ["maintenanceReason"] = row.MaintenanceReason,
                ["osName"] = row.OperatingSystem,
                ["osVersion"] = row.OsVersion,
                ["agentVersion"] = row.AgentVersion,
                ["commitHash"] = row.CommitHash,
                ["lastIpAddress"] = row.LastIpAddress,
                ["loggedUser"] = row.LoggedUser,
                ["lastSeenAt"] = row.LastSeenAt,
                ["zeroTouchPending"] = row.ZeroTouchPending,
                ["pendingReboot"] = row.PendingReboot,
                ["pendingRebootReasons"] = FormatRebootReasons(row.PendingRebootReasons),
                ["pendingRebootCheckedAt"] = row.PendingRebootCheckedAt,
                ["createdAt"] = row.CreatedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns =
            [
                "clientName", "siteName", "agentId", "hostname", "displayName",
                "automaticLabels", "status", "maintenance", "maintenanceReason",
                "osName", "osVersion", "agentVersion", "commitHash",
                "lastIpAddress", "loggedUser", "lastSeenAt",
                "zeroTouchPending", "pendingReboot", "pendingRebootReasons",
                "pendingRebootCheckedAt", "createdAt"
            ],
            Rows = rows
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static PatchUpdateStatus? ParsePatchUpdateStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (Enum.TryParse<PatchUpdateStatus>(normalized, ignoreCase: true, out var parsed))
            return parsed;
        return int.TryParse(normalized, out var numeric) && Enum.IsDefined(typeof(PatchUpdateStatus), numeric)
            ? (PatchUpdateStatus)numeric
            : null;
    }

    private static PatchApprovalState? ParsePatchApprovalState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (Enum.TryParse<PatchApprovalState>(normalized, ignoreCase: true, out var parsed))
            return parsed;
        return int.TryParse(normalized, out var numeric) && Enum.IsDefined(typeof(PatchApprovalState), numeric)
            ? (PatchApprovalState)numeric
            : null;
    }

    private static PatchApprovalScopeType? ParsePatchScopeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (Enum.TryParse<PatchApprovalScopeType>(normalized, ignoreCase: true, out var parsed))
            return parsed;
        return int.TryParse(normalized, out var numeric) && Enum.IsDefined(typeof(PatchApprovalScopeType), numeric)
            ? (PatchApprovalScopeType)numeric
            : null;
    }

    private static PatchApprovalActionType? ParsePatchAction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (Enum.TryParse<PatchApprovalActionType>(normalized, ignoreCase: true, out var parsed))
            return parsed;
        return int.TryParse(normalized, out var numeric) && Enum.IsDefined(typeof(PatchApprovalActionType), numeric)
            ? (PatchApprovalActionType)numeric
            : null;
    }

    private static PatchApprovalAuditChangeType? ParsePatchAuditChangeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (Enum.TryParse<PatchApprovalAuditChangeType>(normalized, ignoreCase: true, out var parsed))
            return parsed;
        return int.TryParse(normalized, out var numeric) &&
               Enum.IsDefined(typeof(PatchApprovalAuditChangeType), numeric)
            ? (PatchApprovalAuditChangeType)numeric
            : null;
    }

    private static string FormatRebootReasons(string? rawJson)
    {
        var reasons = PatchJson.ParseStringArray(rawJson);
        if (reasons.Count == 0)
            return string.Empty;

        return string.Join(", ", reasons.Select(reason => reason switch
        {
            "windows_update" => "Windows Update",
            "cbs" => "Component Based Servicing",
            "file_rename" => "Arquivos em uso",
            "computer_rename" => "Renomeação do computador",
            "servicing" => "Manutenção de componentes",
            _ => reason
        }));
    }
}
