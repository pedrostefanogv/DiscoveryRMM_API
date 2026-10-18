using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Identity;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Resolve a política de consentimento de acesso remoto com herança:
/// Servidor → Cliente → Site → Grupos de usuários → Usuário.
///
/// Regras:
/// - campos bloqueados por LockedFieldsJson (servidor/cliente/site) ignoram
///   overrides de grupo/usuário;
/// - entre grupos, vence o escopo mais específico (site > cliente > global) e,
///   no empate, o valor mais restritivo;
/// - o override do usuário vence os grupos, campo a campo.
/// </summary>
public sealed class RemoteConsentPolicyResolver(
    IConfigurationResolver configurationResolver,
    IUserGroupRepository userGroupRepository,
    IUserRepository userRepository,
    DiscoveryDbContext db) : IRemoteConsentPolicyResolver
{
    private const string SourceServer = "Server";
    private const string SourceClient = "Client";
    private const string SourceSite = "Site";
    private const string SourceUserGroup = "UserGroup";
    private const string SourceUser = "User";
    private const string SourceBlock = "Block";

    private static readonly string[] Fields =
    [
        nameof(RemoteConsentPolicy.Mode),
        nameof(RemoteConsentPolicy.TimeoutSeconds),
        nameof(RemoteConsentPolicy.TimeoutAction),
        nameof(RemoteConsentPolicy.ShowBanner),
        nameof(RemoteConsentPolicy.Message),
        nameof(RemoteConsentPolicy.BannerText)
    ];

    public async Task<RemoteConsentPolicy> ResolveAsync(
        Guid userId,
        Guid clientId,
        Guid siteId,
        CancellationToken ct = default)
    {
        var (policy, blocked) = await BuildBaseAsync(clientId, siteId);

        if (userId != Guid.Empty)
        {
            // Grupos primeiro (o usuário sobrescreve depois). Grupos inativos não
            // concedem política — mesmo contrato das permissões do IAM.
            var memberships = (await userGroupRepository.GetGroupIdsForUserAsync(userId)).ToHashSet();
            var activeGroups = (await userGroupRepository.GetAllAsync())
                .Where(group => group.IsActive && memberships.Contains(group.Id))
                .ToList();
            var groupIds = activeGroups.Select(group => group.Id).ToArray();
            if (groupIds.Length > 0)
            {
                var groupOverrides = await LoadOverridesAsync(
                    RemoteConsentSubjectType.UserGroup, groupIds, clientId, siteId, ct);
                var groupNames = activeGroups.ToDictionary(group => group.Id, group => group.Name);
                ApplyGroupOverrides(policy, blocked, groupOverrides, groupNames);
            }

            var userOverrides = await LoadOverridesAsync(
                RemoteConsentSubjectType.User, [userId], clientId, siteId, ct);
            ApplyScopedOverrides(policy, blocked, userOverrides, SourceUser, await LoadUserNameAsync(userId));
        }

        return policy;
    }

    private async Task<(RemoteConsentPolicy Policy, HashSet<string> Blocked)> BuildBaseAsync(
        Guid clientId, Guid siteId)
    {
        var policy = new RemoteConsentPolicy();

        if (siteId != Guid.Empty)
        {
            var resolved = await configurationResolver.ResolveForSiteAsync(siteId);
            policy.Mode = resolved.RemoteConsentMode;
            policy.TimeoutSeconds = resolved.RemoteConsentTimeoutSeconds;
            policy.TimeoutAction = resolved.RemoteConsentTimeoutAction;
            policy.ShowBanner = resolved.RemoteConsentShowBanner;
            policy.Message = resolved.RemoteConsentMessage;
            policy.BannerText = resolved.RemoteConsentBannerText;
            FillSources(policy, resolved, SourceSite);
            return (policy, new HashSet<string>(resolved.BlockedFields, StringComparer.OrdinalIgnoreCase));
        }

        if (clientId != Guid.Empty)
        {
            var resolved = await configurationResolver.ResolveForClientAsync(clientId);
            policy.Mode = resolved.RemoteConsentMode;
            policy.TimeoutSeconds = resolved.RemoteConsentTimeoutSeconds;
            policy.TimeoutAction = resolved.RemoteConsentTimeoutAction;
            policy.ShowBanner = resolved.RemoteConsentShowBanner;
            policy.Message = resolved.RemoteConsentMessage;
            policy.BannerText = resolved.RemoteConsentBannerText;
            FillSources(policy, resolved, SourceClient);
            return (policy, new HashSet<string>(resolved.BlockedFields, StringComparer.OrdinalIgnoreCase));
        }

        var server = await configurationResolver.GetServerAsync();
        policy.Mode = server.RemoteConsentMode;
        policy.TimeoutSeconds = server.RemoteConsentTimeoutSeconds;
        policy.TimeoutAction = server.RemoteConsentTimeoutAction;
        policy.ShowBanner = server.RemoteConsentShowBanner;
        policy.Message = server.RemoteConsentMessage;
        policy.BannerText = server.RemoteConsentBannerText;

        var blocked = ParseBlocked(server.LockedFieldsJson);
        foreach (var field in Fields)
            policy.Sources[field] = new RemoteConsentSource(blocked.Contains(field) ? SourceBlock : SourceServer);

        return (policy, blocked);
    }

    private static void FillSources(
        RemoteConsentPolicy policy,
        ResolvedConfiguration resolved,
        string fallbackSource)
    {
        var blocked = new HashSet<string>(resolved.BlockedFields, StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            var source = fallbackSource;
            if (blocked.Contains(field))
                source = SourceBlock;
            else if (resolved.Inheritance.TryGetValue(field, out var numeric))
            {
                source = numeric switch
                {
                    (int)ConfigurationPriorityType.Block => SourceBlock,
                    (int)ConfigurationPriorityType.Client => SourceClient,
                    (int)ConfigurationPriorityType.Site => SourceSite,
                    _ => SourceServer
                };
            }

            policy.Sources[field] = new RemoteConsentSource(source);
        }
    }

    private async Task<List<RemoteConsentPolicyOverride>> LoadOverridesAsync(
        RemoteConsentSubjectType subjectType,
        Guid[] subjectIds,
        Guid clientId,
        Guid siteId,
        CancellationToken ct)
    {
        var query = db.RemoteConsentPolicyOverrides
            .AsNoTracking()
            .Where(item => item.SubjectType == subjectType && subjectIds.Contains(item.SubjectId));

        // Escopo: global (Guid.Empty), cliente ou site. Quando siteId é informado,
        // aceita também overrides de cliente (o site herda o cliente); o filtro de
        // especificidade é feito depois.
        if (clientId != Guid.Empty)
            query = query.Where(item => item.ClientId == Guid.Empty || item.ClientId == clientId);
        else
            query = query.Where(item => item.ClientId == Guid.Empty);

        if (siteId != Guid.Empty)
            query = query.Where(item => item.SiteId == Guid.Empty || item.SiteId == siteId);
        else
            query = query.Where(item => item.SiteId == Guid.Empty);

        return await query.ToListAsync(ct);
    }

    /// <summary>
    /// Aplica overrides de usuário: para cada campo, vence a linha de escopo mais
    /// específico que o define.
    /// </summary>
    private static void ApplyScopedOverrides(
        RemoteConsentPolicy policy,
        HashSet<string> blocked,
        List<RemoteConsentPolicyOverride> overrides,
        string source,
        string? subjectName)
    {
        foreach (var field in Fields)
        {
            if (blocked.Contains(field)) continue;

            var candidate = overrides
                .Where(item => HasValue(item, field))
                .OrderByDescending(ScopeRank)
                .ThenBy(item => item.UpdatedAt)
                .FirstOrDefault();
            if (candidate is null) continue;

            ApplyValue(policy, field, candidate);
            policy.Sources[field] = new RemoteConsentSource(source, candidate.SubjectId, subjectName);
        }
    }

    /// <summary>
    /// Aplica overrides de grupos: para cada campo vence o escopo mais específico;
    /// no empate entre grupos, escolhe o valor mais restritivo.
    /// </summary>
    private static void ApplyGroupOverrides(
        RemoteConsentPolicy policy,
        HashSet<string> blocked,
        List<RemoteConsentPolicyOverride> overrides,
        IReadOnlyDictionary<Guid, string> groupNames)
    {
        foreach (var field in Fields)
        {
            if (blocked.Contains(field)) continue;

            var candidates = overrides.Where(item => HasValue(item, field)).ToList();
            if (candidates.Count == 0) continue;

            var maxScope = candidates.Max(ScopeRank);
            var top = candidates.Where(item => ScopeRank(item) == maxScope).ToList();
            var winner = top.Count == 1 ? top[0] : PickMostRestrictive(top, field);
            if (winner is null) continue;

            ApplyValue(policy, field, winner);
            groupNames.TryGetValue(winner.SubjectId, out var name);
            policy.Sources[field] = new RemoteConsentSource(SourceUserGroup, winner.SubjectId, name);
        }
    }

    private static RemoteConsentPolicyOverride? PickMostRestrictive(
        List<RemoteConsentPolicyOverride> candidates,
        string field) =>
        field switch
        {
            nameof(RemoteConsentPolicy.Mode) => candidates
                .OrderByDescending(item => (int)(item.Mode ?? RemoteConsentMode.Off))
                .First(),
            nameof(RemoteConsentPolicy.TimeoutAction) => candidates
                .OrderBy(item => (int)(item.TimeoutAction ?? RemoteConsentTimeoutAction.Approve))
                .ThenBy(item => item.SubjectId)
                .First(),
            nameof(RemoteConsentPolicy.TimeoutSeconds) => candidates
                .OrderBy(item => item.TimeoutSeconds ?? int.MaxValue)
                .ThenBy(item => item.SubjectId)
                .First(),
            nameof(RemoteConsentPolicy.ShowBanner) => candidates
                .OrderByDescending(item => item.ShowBanner ?? false)
                .ThenBy(item => item.SubjectId)
                .First(),
            _ => candidates.OrderBy(item => item.SubjectId).First()
        };

    private static bool HasValue(RemoteConsentPolicyOverride item, string field) =>
        field switch
        {
            nameof(RemoteConsentPolicy.Mode) => item.Mode.HasValue,
            nameof(RemoteConsentPolicy.TimeoutSeconds) => item.TimeoutSeconds.HasValue,
            nameof(RemoteConsentPolicy.TimeoutAction) => item.TimeoutAction.HasValue,
            nameof(RemoteConsentPolicy.ShowBanner) => item.ShowBanner.HasValue,
            nameof(RemoteConsentPolicy.Message) => !string.IsNullOrWhiteSpace(item.Message),
            nameof(RemoteConsentPolicy.BannerText) => !string.IsNullOrWhiteSpace(item.BannerText),
            _ => false
        };

    private static void ApplyValue(
        RemoteConsentPolicy policy,
        string field,
        RemoteConsentPolicyOverride item)
    {
        switch (field)
        {
            case nameof(RemoteConsentPolicy.Mode):
                policy.Mode = item.Mode!.Value;
                break;
            case nameof(RemoteConsentPolicy.TimeoutSeconds):
                policy.TimeoutSeconds = item.TimeoutSeconds!.Value;
                break;
            case nameof(RemoteConsentPolicy.TimeoutAction):
                policy.TimeoutAction = item.TimeoutAction!.Value;
                break;
            case nameof(RemoteConsentPolicy.ShowBanner):
                policy.ShowBanner = item.ShowBanner!.Value;
                break;
            case nameof(RemoteConsentPolicy.Message):
                policy.Message = item.Message!;
                break;
            case nameof(RemoteConsentPolicy.BannerText):
                policy.BannerText = item.BannerText!;
                break;
        }
    }

    private static int ScopeRank(RemoteConsentPolicyOverride item) =>
        item.SiteId != Guid.Empty ? 3 : item.ClientId != Guid.Empty ? 2 : 1;

    private async Task<string?> LoadUserNameAsync(Guid userId)
    {
        var user = await userRepository.GetByIdAsync(userId);
        if (user is null) return null;
        return string.IsNullOrWhiteSpace(user.FullName) ? user.Login : user.FullName;
    }

    private static HashSet<string> ParseBlocked(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
