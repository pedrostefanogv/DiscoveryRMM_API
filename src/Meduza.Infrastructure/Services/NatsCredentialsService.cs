using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Security;
using Meduza.Core.Interfaces.Auth;
using Microsoft.Extensions.Logging;
using NATS.Jwt;
using NATS.NKeys;

namespace Meduza.Infrastructure.Services;

public class NatsCredentialsService : INatsCredentialsService
{
    private const string PublishHeartbeat = "heartbeat";
    private const string PublishResult = "result";
    private const string PublishHardware = "hardware";
    private const string SubscribeCommand = "command";
    private const string SubscribeSyncPing = "sync.ping";

    private readonly IAgentRepository _agentRepository;
    private readonly ISiteRepository _siteRepository;
    private readonly IConfigurationService _configurationService;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<NatsCredentialsService> _logger;

    public NatsCredentialsService(
        IAgentRepository agentRepository,
        ISiteRepository siteRepository,
        IConfigurationService configurationService,
        ISecretProtector secretProtector,
        ILogger<NatsCredentialsService> logger)
    {
        _agentRepository = agentRepository;
        _siteRepository = siteRepository;
        _configurationService = configurationService;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public async Task<NatsCredentialsResponse> IssueForAgentAsync(Guid agentId, CancellationToken ct = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found.");
        var site = await _siteRepository.GetByIdAsync(agent.SiteId)
            ?? throw new InvalidOperationException($"Site '{agent.SiteId}' not found for agent.");

        var config = await _configurationService.GetServerConfigAsync();
        EnsureEnabled(config);

        var ttlMinutes = Math.Max(1, config.NatsAgentJwtTtlMinutes);
        var subjects = BuildAgentSubjects(site.ClientId, site.Id, agent.Id, config.NatsUseScopedSubjects, config.NatsIncludeLegacySubjects);

        return IssueCredentials(
            accountSeed: config.NatsAccountSeed,
            ttlMinutes: ttlMinutes,
            publishSubjects: subjects.Publish.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            subscribeSubjects: subjects.Subscribe.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            traceLabel: $"agent:{agent.Id}");
    }

    public async Task<(string Jwt, DateTime ExpiresAtUtc)> IssueUserJwtForAgentAsync(string userPublicKey, Guid agentId, CancellationToken ct = default)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found.");
        var site = await _siteRepository.GetByIdAsync(agent.SiteId)
            ?? throw new InvalidOperationException($"Site '{agent.SiteId}' not found for agent.");

        var config = await _configurationService.GetServerConfigAsync();
        EnsureEnabled(config);

        var subjects = BuildAgentSubjects(site.ClientId, site.Id, agent.Id, config.NatsUseScopedSubjects, config.NatsIncludeLegacySubjects);
        return IssueUserJwtForPublicKey(
            userPublicKey,
            config.NatsAccountSeed,
            config.NatsAgentJwtTtlMinutes,
            subjects.Publish,
            subjects.Subscribe,
            $"agent:{agent.Id}");
    }

    public async Task<(string Jwt, DateTime ExpiresAtUtc)> IssueUserJwtForUserAsync(
        string userPublicKey,
        Guid userId,
        UserScopeAccess scopeAccess,
        CancellationToken ct = default)
    {
        _ = ct;
        var config = await _configurationService.GetServerConfigAsync();
        EnsureEnabled(config);

        var subscribeSubjects = await BuildDashboardSubjectsAsync(scopeAccess, null, null, config.NatsUseScopedSubjects, ct);
        return IssueUserJwtForPublicKey(
            userPublicKey,
            config.NatsAccountSeed,
            config.NatsUserJwtTtlMinutes,
            Array.Empty<string>(),
            subscribeSubjects,
            $"user:{userId}");
    }

    public async Task<NatsCredentialsResponse> IssueForUserAsync(
        Guid userId,
        UserScopeAccess scopeAccess,
        Guid? clientId,
        Guid? siteId,
        CancellationToken ct = default)
    {
        _ = ct;

        var config = await _configurationService.GetServerConfigAsync();
        EnsureEnabled(config);

        var resolvedClientId = clientId;
        var resolvedSiteId = siteId;

        if (!scopeAccess.HasGlobalAccess)
        {
            if (resolvedSiteId.HasValue)
            {
                if (!scopeAccess.AllowedSiteIds.Contains(resolvedSiteId.Value))
                    throw new InvalidOperationException("User does not have access to the requested site.");
            }
            else if (resolvedClientId.HasValue)
            {
                if (!scopeAccess.AllowedClientIds.Contains(resolvedClientId.Value))
                    throw new InvalidOperationException("User does not have access to the requested client.");
            }
            else
            {
                resolvedClientId = scopeAccess.AllowedClientIds.FirstOrDefault();
                resolvedSiteId = scopeAccess.AllowedSiteIds.FirstOrDefault();
            }
        }

        var publishSubjects = Array.Empty<string>();
        var subscribeSubjects = await BuildDashboardSubjectsAsync(scopeAccess, resolvedClientId, resolvedSiteId, config.NatsUseScopedSubjects, ct);
        var ttlMinutes = Math.Max(1, config.NatsUserJwtTtlMinutes);

        return IssueCredentials(
            accountSeed: config.NatsAccountSeed,
            ttlMinutes: ttlMinutes,
            publishSubjects: publishSubjects,
            subscribeSubjects: subscribeSubjects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            traceLabel: $"user:{userId}");
    }

    private NatsCredentialsResponse IssueCredentials(
        string accountSeed,
        int ttlMinutes,
        IReadOnlyList<string> publishSubjects,
        IReadOnlyList<string> subscribeSubjects,
        string traceLabel)
    {
        var accountSeedPlain = _secretProtector.UnprotectOrSelf(accountSeed);
        if (string.IsNullOrWhiteSpace(accountSeedPlain))
            throw new InvalidOperationException("NATS account seed is not configured.");

        var accountKeyPair = KeyPair.FromSeed(accountSeedPlain);
        var userKeyPair = KeyPair.CreatePair(PrefixByte.User);

        var now = DateTime.UtcNow;
        var expiresAtUtc = now.AddMinutes(ttlMinutes);

        var claims = NatsJwt.NewUserClaims(userKeyPair.GetPublicKey());
        claims.Name = traceLabel;
        claims.Expires = new DateTimeOffset(expiresAtUtc);

        if (publishSubjects.Count > 0)
            claims.User.Pub.Allow = publishSubjects.ToList();
        if (subscribeSubjects.Count > 0)
            claims.User.Sub.Allow = subscribeSubjects.ToList();

        var jwt = NatsJwt.EncodeUserClaims(claims, accountKeyPair);

        _logger.LogInformation("NATS credentials issued for {Trace}. Exp={ExpUtc}", traceLabel, expiresAtUtc);

        return new NatsCredentialsResponse(
            Jwt: jwt,
            NkeySeed: userKeyPair.GetSeed(),
            PublicKey: userKeyPair.GetPublicKey(),
            ExpiresAtUtc: expiresAtUtc,
            PublishSubjects: publishSubjects.ToArray(),
            SubscribeSubjects: subscribeSubjects.ToArray());
    }

    private (string Jwt, DateTime ExpiresAtUtc) IssueUserJwtForPublicKey(
        string userPublicKey,
        string accountSeed,
        int ttlMinutes,
        IReadOnlyList<string> publishSubjects,
        IReadOnlyList<string> subscribeSubjects,
        string traceLabel)
    {
        var accountSeedPlain = _secretProtector.UnprotectOrSelf(accountSeed);
        if (string.IsNullOrWhiteSpace(accountSeedPlain))
            throw new InvalidOperationException("NATS account seed is not configured.");

        var accountKeyPair = KeyPair.FromSeed(accountSeedPlain);

        var now = DateTime.UtcNow;
        var expiresAtUtc = now.AddMinutes(Math.Max(1, ttlMinutes));

        var claims = NatsJwt.NewUserClaims(userPublicKey);
        claims.Name = traceLabel;
        claims.Expires = new DateTimeOffset(expiresAtUtc);

        if (publishSubjects.Count > 0)
            claims.User.Pub.Allow = publishSubjects.ToList();
        if (subscribeSubjects.Count > 0)
            claims.User.Sub.Allow = subscribeSubjects.ToList();

        var jwt = NatsJwt.EncodeUserClaims(claims, accountKeyPair);
        _logger.LogInformation("NATS callout JWT issued for {Trace}. Exp={ExpUtc}", traceLabel, expiresAtUtc);

        return (jwt, expiresAtUtc);
    }

    private static (IReadOnlyList<string> Publish, IReadOnlyList<string> Subscribe) BuildAgentSubjects(
        Guid clientId,
        Guid siteId,
        Guid agentId,
        bool useScopedSubjects,
        bool includeLegacy)
    {
        var publishSubjects = new List<string>();
        var subscribeSubjects = new List<string>();

        if (useScopedSubjects)
        {
            publishSubjects.Add(NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, PublishHeartbeat));
            publishSubjects.Add(NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, PublishResult));
            publishSubjects.Add(NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, PublishHardware));
            subscribeSubjects.Add(NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, SubscribeCommand));
            subscribeSubjects.Add(NatsSubjectBuilder.AgentSubject(clientId, siteId, agentId, SubscribeSyncPing));
        }

        if (includeLegacy)
        {
            publishSubjects.Add(NatsSubjectBuilder.AgentLegacySubject(agentId, PublishHeartbeat));
            publishSubjects.Add(NatsSubjectBuilder.AgentLegacySubject(agentId, PublishResult));
            publishSubjects.Add(NatsSubjectBuilder.AgentLegacySubject(agentId, PublishHardware));
            subscribeSubjects.Add(NatsSubjectBuilder.AgentLegacySubject(agentId, SubscribeCommand));
            subscribeSubjects.Add(NatsSubjectBuilder.AgentLegacySubject(agentId, SubscribeSyncPing));
        }

        return (publishSubjects, subscribeSubjects);
    }

    private async Task<IReadOnlyList<string>> BuildDashboardSubjectsAsync(
        UserScopeAccess scopeAccess,
        Guid? clientId,
        Guid? siteId,
        bool useScopedSubjects,
        CancellationToken ct)
    {
        if (!useScopedSubjects)
            return new[] { NatsSubjectBuilder.DashboardSubject(null, null) };

        var subjects = new List<string>();

        if (scopeAccess.HasGlobalAccess)
        {
            subjects.Add("tenant.*.site.*.dashboard.events");
            subjects.Add("tenant.*.dashboard.events");
            subjects.Add(NatsSubjectBuilder.DashboardSubject(null, null));
            return subjects;
        }

        if (siteId.HasValue && clientId.HasValue)
        {
            subjects.Add(NatsSubjectBuilder.DashboardSubject(clientId, siteId));
            return subjects;
        }

        if (clientId.HasValue)
        {
            subjects.Add(NatsSubjectBuilder.DashboardSubject(clientId, null));
            return subjects;
        }

        foreach (var allowedClientId in scopeAccess.AllowedClientIds)
        {
            subjects.Add(NatsSubjectBuilder.DashboardSubject(allowedClientId, null));
        }

        foreach (var allowedSiteId in scopeAccess.AllowedSiteIds)
        {
            var site = await _siteRepository.GetByIdAsync(allowedSiteId);
            if (site is null)
                continue;
            subjects.Add(NatsSubjectBuilder.DashboardSubject(site.ClientId, site.Id));
        }

        return subjects;
    }

    private static void EnsureEnabled(ServerConfiguration config)
    {
        if (!config.NatsAuthEnabled)
            throw new InvalidOperationException("NATS auth is disabled in server configuration.");
    }
}
