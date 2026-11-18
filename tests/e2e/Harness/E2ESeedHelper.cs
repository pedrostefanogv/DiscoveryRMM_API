using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Entities;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.E2E.Tests.Harness;

public sealed record E2EUserCredentials(
    User User,
    Guid SessionId,
    string Jwt,
    string ApiKey,
    string ApiSecret);

public sealed record E2ETenantWorld(
    Client ClientA,
    Client ClientB,
    Site SiteA1,
    Site SiteA2,
    Site SiteB1,
    Agent AgentA1,
    Agent AgentB1,
    string AgentTokenA1,
    string AgentTokenB1,
    E2EUserCredentials GlobalAdmin,
    E2EUserCredentials TechA,
    E2EUserCredentials SiteTechA1,
    E2EUserCredentials TechB,
    Ticket TicketA1,
    Ticket TicketB1,
    RemoteSession RemoteSessionA1,
    RemoteSession RemoteSessionB1);

public static class E2ESeedHelper
{
    public static async Task<List<Permission>> EnsureAllPermissionsAsync(DiscoveryDbContext db)
    {
        var existing = await db.Permissions.ToListAsync();
        if (existing.Count > 0)
            return existing;

        var list = new List<Permission>();
        foreach (var resource in Enum.GetValues<ResourceType>())
        {
            foreach (var action in Enum.GetValues<ActionType>())
            {
                list.Add(new Permission
                {
                    Id = Guid.NewGuid(),
                    ResourceType = resource,
                    ActionType = action,
                    Description = $"{resource}:{action}"
                });
            }
        }

        db.Permissions.AddRange(list);
        await db.SaveChangesAsync();
        return list;
    }

    public static async Task<(Role AdminRole, Role TechRole)> EnsureStandardRolesAsync(
        DiscoveryDbContext db,
        IReadOnlyCollection<Permission> permissions)
    {
        var now = DateTime.UtcNow;
        var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "E2E-GlobalAdmin");
        if (adminRole is null)
        {
            adminRole = new Role
            {
                Id = Guid.NewGuid(),
                Name = "E2E-GlobalAdmin",
                Type = RoleType.System,
                IsSystem = true,
                MfaRequirement = RoleMfaRequirement.None,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Roles.Add(adminRole);
            foreach (var p in permissions)
            {
                db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = p.Id });
            }
        }

        var techRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "E2E-ClientTechnician");
        if (techRole is null)
        {
            techRole = new Role
            {
                Id = Guid.NewGuid(),
                Name = "E2E-ClientTechnician",
                Type = RoleType.Custom,
                IsSystem = false,
                MfaRequirement = RoleMfaRequirement.None,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Roles.Add(techRole);

            var allowedPairs = new HashSet<(ResourceType, ActionType)>
            {
                (ResourceType.Clients, ActionType.View),
                (ResourceType.Clients, ActionType.Edit),
                (ResourceType.Sites, ActionType.View),
                (ResourceType.Sites, ActionType.Create),
                (ResourceType.Sites, ActionType.Edit),
                (ResourceType.Sites, ActionType.Delete),
                (ResourceType.Agents, ActionType.View),
                (ResourceType.Agents, ActionType.Create),
                (ResourceType.Agents, ActionType.Edit),
                (ResourceType.Agents, ActionType.Delete),
                (ResourceType.Agents, ActionType.Execute),
                (ResourceType.Tickets, ActionType.View),
                (ResourceType.Tickets, ActionType.Create),
                (ResourceType.Tickets, ActionType.Edit),
                (ResourceType.Tickets, ActionType.Delete),
                (ResourceType.Tickets, ActionType.Execute),
                (ResourceType.Automation, ActionType.View),
                (ResourceType.Automation, ActionType.Execute),
                (ResourceType.Reports, ActionType.View),
                (ResourceType.Reports, ActionType.Execute),
                (ResourceType.Logs, ActionType.View),
                (ResourceType.Dashboard, ActionType.View),
                (ResourceType.RemoteDebug, ActionType.Execute),
                (ResourceType.RemoteSession, ActionType.View),
                (ResourceType.RemoteSession, ActionType.Execute),
            };

            foreach (var p in permissions.Where(p => allowedPairs.Contains((p.ResourceType, p.ActionType))))
            {
                db.RolePermissions.Add(new RolePermission { RoleId = techRole.Id, PermissionId = p.Id });
            }
        }

        await db.SaveChangesAsync();
        return (adminRole, techRole);
    }

    public static async Task<E2EUserCredentials> CreateUserWithCredentialsAsync(
        IServiceProvider services,
        string login,
        Role role,
        ScopeLevel scopeLevel,
        Guid? scopeId,
        string? password = null)
    {
        var db = services.GetRequiredService<DiscoveryDbContext>();
        var jwtService = services.GetRequiredService<IJwtService>();
        var passwordService = services.GetRequiredService<IPasswordService>();

        var now = DateTime.UtcNow;
        var salt = password is not null ? passwordService.GenerateSalt() : Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var hash = password is not null ? passwordService.HashPassword(password, salt) : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var user = new User
        {
            Id = Guid.NewGuid(),
            Login = login,
            Email = $"{login}@e2e.local",
            FullName = $"E2E {login}",
            PasswordSalt = salt,
            PasswordHash = hash,
            IsActive = true,
            MfaRequired = false,
            MfaConfigured = false,
            MustChangePassword = false,
            MustChangeProfile = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.Add(user);

        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = $"group-{login}-{Guid.NewGuid():N}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.UserGroups.Add(group);

        db.UserGroupMemberships.Add(new UserGroupMembership
        {
            UserId = user.Id,
            GroupId = group.Id,
            JoinedAt = now
        });

        db.UserGroupRoles.Add(new UserGroupRole
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            RoleId = role.Id,
            ScopeLevel = scopeLevel,
            ScopeId = scopeId,
            AssignedAt = now
        });

        var sessionId = Guid.NewGuid();
        var jwt = jwtService.GenerateAccessToken(user.Id, sessionId, [new Claim("unique_name", user.Login)]);
        var accessHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(jwt)));
        var refreshHash = Convert.ToBase64String(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));

        db.UserSessions.Add(new UserSession
        {
            Id = sessionId,
            UserId = user.Id,
            AccessTokenHash = accessHash,
            RefreshTokenHash = refreshHash,
            MfaVerified = false,
            CreatedAt = now,
            ExpiresAt = now.AddDays(1)
        });

        var tokenIdPublic = $"mzt_{Guid.NewGuid():N}";
        var accessKey = $"mzk_{Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}";
        var accessKeyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(accessKey)));

        db.ApiTokens.Add(new ApiToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = $"api-{login}",
            TokenIdPublic = tokenIdPublic,
            AccessKeyHash = accessKeyHash,
            IsActive = true,
            CreatedAt = now,
            ExpiresAt = now.AddDays(1)
        });

        await db.SaveChangesAsync();
        return new E2EUserCredentials(user, sessionId, jwt, tokenIdPublic, accessKey);
    }

    public static async Task<(Agent Agent, string RawToken)> CreateAgentWithTokenAsync(
        DiscoveryDbContext db,
        Guid siteId,
        string hostname,
        AgentStatus status = AgentStatus.Online,
        DateTime? deletedAt = null)
    {
        var now = DateTime.UtcNow;
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            Hostname = hostname,
            Status = status,
            // NOTA (ajuste de spec): o agent do harness representa o BUILD ATUAL
            // do agente. A versão "1.0.0-e2e" era mais antiga que o mínimo que
            // suporta o prompt de consentimento (RemoteAccess:Consent:MinimumAgentVersion),
            // e o gate agent_update_required recusava TODO start de sessão desktop
            // (kind screen/all) com 409 antes de qualquer cenário ser exercitado.
            AgentVersion = "9.9.9-e2e",
            LastIpAddress = "10.0.0.10",
            MacAddress = "AA:BB:CC:DD:EE:01",
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deletedAt
        };
        db.Agents.Add(agent);

        var rawToken = $"mdz_{Base64UrlEncode(RandomNumberGenerator.GetBytes(24))}";
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

        db.AgentTokens.Add(new AgentToken
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            TokenHash = tokenHash,
            TokenPrefix = rawToken[..8],
            Description = "e2e-agent-token",
            CreatedAt = now
        });

        await db.SaveChangesAsync();
        return (agent, rawToken);
    }

    public static async Task<(DeployToken Entity, string RawToken)> CreateDeployTokenAsync(
        DiscoveryDbContext db,
        Guid? clientId,
        Guid? siteId,
        int? maxUses = 1)
    {
        var now = DateTime.UtcNow;
        var rawToken = $"mdt_{Base64UrlEncode(RandomNumberGenerator.GetBytes(24))}";
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

        var entity = new DeployToken
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            SiteId = siteId,
            TokenHash = tokenHash,
            TokenPrefix = rawToken[..8],
            Description = "e2e-deploy-token",
            CreatedAt = now,
            ExpiresAt = now.AddDays(1),
            UsedCount = 0,
            MaxUses = maxUses
        };
        db.DeployTokens.Add(entity);
        await db.SaveChangesAsync();
        return (entity, rawToken);
    }

    public static async Task<E2ETenantWorld> SeedMultiTenantWorldAsync(ApiE2ETestHost host)
    {
        E2ETenantWorld? world = null;

        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<DiscoveryDbContext>();
            var now = DateTime.UtcNow;

            var permissions = await EnsureAllPermissionsAsync(db);
            var (adminRole, techRole) = await EnsureStandardRolesAsync(db, permissions);

            var clientA = new Client { Id = Guid.NewGuid(), Name = "Tenant Alpha", IsActive = true, CreatedAt = now, UpdatedAt = now };
            var clientB = new Client { Id = Guid.NewGuid(), Name = "Tenant Beta", IsActive = true, CreatedAt = now, UpdatedAt = now };
            db.Clients.AddRange(clientA, clientB);

            var siteA1 = new Site { Id = Guid.NewGuid(), ClientId = clientA.Id, Name = "Alpha Site 1", IsActive = true, CreatedAt = now, UpdatedAt = now };
            var siteA2 = new Site { Id = Guid.NewGuid(), ClientId = clientA.Id, Name = "Alpha Site 2", IsActive = true, CreatedAt = now, UpdatedAt = now };
            var siteB1 = new Site { Id = Guid.NewGuid(), ClientId = clientB.Id, Name = "Beta Site 1", IsActive = true, CreatedAt = now, UpdatedAt = now };
            db.Sites.AddRange(siteA1, siteA2, siteB1);
            await db.SaveChangesAsync();

            var (agentA1, tokenA1) = await CreateAgentWithTokenAsync(db, siteA1.Id, "WIN-ALPHA-1");
            var (agentB1, tokenB1) = await CreateAgentWithTokenAsync(db, siteB1.Id, "WIN-BETA-1");

            var globalAdmin = await CreateUserWithCredentialsAsync(sp, "admin-e2e", adminRole, ScopeLevel.Global, null, "AdminPass!1234");
            var techA = await CreateUserWithCredentialsAsync(sp, "tech-alpha", techRole, ScopeLevel.Client, clientA.Id, "TechPass!1234");
            var siteTechA1 = await CreateUserWithCredentialsAsync(sp, "sitetech-a1", techRole, ScopeLevel.Site, siteA1.Id, "TechPass!1234");
            var techB = await CreateUserWithCredentialsAsync(sp, "tech-beta", techRole, ScopeLevel.Client, clientB.Id, "TechPass!1234");

            var openState = await db.WorkflowStates.FirstAsync(s => s.IsInitial && s.ClientId == null);

            var ticketA1 = new Ticket
            {
                Id = Guid.NewGuid(),
                ClientId = clientA.Id,
                SiteId = siteA1.Id,
                AgentId = agentA1.Id,
                Title = "Alpha Ticket 1",
                Description = "Tenant A issue",
                WorkflowStateId = openState.Id,
                Priority = TicketPriority.Medium,
                CreatedAt = now,
                UpdatedAt = now
            };
            var ticketB1 = new Ticket
            {
                Id = Guid.NewGuid(),
                ClientId = clientB.Id,
                SiteId = siteB1.Id,
                AgentId = agentB1.Id,
                Title = "Beta Ticket 1",
                Description = "Tenant B issue",
                WorkflowStateId = openState.Id,
                Priority = TicketPriority.High,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Tickets.AddRange(ticketA1, ticketB1);

            var rsA1Id = Guid.NewGuid();
            var rsA1 = new RemoteSession
            {
                Id = rsA1Id,
                AgentId = agentA1.Id,
                UserId = techA.User.Id,
                TenantId = clientA.Id,
                SiteId = siteA1.Id,
                Kind = RemoteSessionKind.Terminal,
                Transport = RemoteTransport.Nats,
                QualityProfile = QualityProfile.High,
                Codec = RemoteCodec.Jpeg,
                Status = "active",
                NatsSubject = $"tenant.{clientA.Id:N}.site.{siteA1.Id:N}.agent.{agentA1.Id:N}.remote.session.{rsA1Id:N}",
                StartedAt = now,
                ExpiresAt = now.AddHours(1)
            };

            var rsB1Id = Guid.NewGuid();
            var rsB1 = new RemoteSession
            {
                Id = rsB1Id,
                AgentId = agentB1.Id,
                UserId = globalAdmin.User.Id,
                TenantId = clientB.Id,
                SiteId = siteB1.Id,
                Kind = RemoteSessionKind.Terminal,
                Transport = RemoteTransport.Nats,
                QualityProfile = QualityProfile.High,
                Codec = RemoteCodec.Jpeg,
                Status = "active",
                NatsSubject = $"tenant.{clientB.Id:N}.site.{siteB1.Id:N}.agent.{agentB1.Id:N}.remote.session.{rsB1Id:N}",
                StartedAt = now,
                ExpiresAt = now.AddHours(1)
            };
            db.RemoteSessions.AddRange(rsA1, rsB1);

            await db.SaveChangesAsync();

            world = new E2ETenantWorld(
                clientA, clientB,
                siteA1, siteA2, siteB1,
                agentA1, agentB1, tokenA1, tokenB1,
                globalAdmin, techA, siteTechA1, techB,
                ticketA1, ticketB1,
                rsA1, rsB1);
        });

        return world!;
    }

    public static HttpRequestMessage WithJwt(this HttpRequestMessage req, string jwt)
    {
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwt);
        return req;
    }

    public static HttpRequestMessage WithApiKey(this HttpRequestMessage req, string apiKey, string apiSecret)
    {
        req.Headers.Add("X-Api-Key", apiKey);
        req.Headers.Add("X-Api-Secret", apiSecret);
        return req;
    }

    public static HttpRequestMessage WithAgentAuth(this HttpRequestMessage req, string rawToken, Guid? agentId = null)
    {
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawToken);
        if (agentId.HasValue)
            req.Headers.Add("X-Agent-ID", agentId.Value.ToString());
        return req;
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
