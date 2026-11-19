using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.IntegrationTests.Fixtures;

public sealed record IntegrationUserCredentials(
    User User,
    Guid SessionId,
    string Jwt);

public static class IntegrationSeedHelper
{
    public static async Task<List<Permission>> EnsureAllPermissionsAsync(DiscoveryDbContext db)
    {
        var existing = await db.Permissions.ToListAsync();
        var existingKeys = existing.Select(p => (p.ResourceType, p.ActionType)).ToHashSet();

        var added = new List<Permission>();
        foreach (var resource in Enum.GetValues<ResourceType>())
        {
            foreach (var action in Enum.GetValues<ActionType>())
            {
                if (!existingKeys.Contains((resource, action)))
                {
                    added.Add(new Permission
                    {
                        Id = Guid.NewGuid(),
                        ResourceType = resource,
                        ActionType = action,
                        Description = $"{resource}:{action}"
                    });
                }
            }
        }

        if (added.Count > 0)
        {
            db.Permissions.AddRange(added);
            await db.SaveChangesAsync();
            existing.AddRange(added);
        }

        return existing;
    }

    public static async Task<Role> CreateRoleAsync(
        DiscoveryDbContext db,
        string name,
        RoleType type,
        IEnumerable<Permission> grantedPermissions)
    {
        var now = DateTime.UtcNow;
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = type,
            IsSystem = type == RoleType.System,
            MfaRequirement = RoleMfaRequirement.None,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Roles.Add(role);

        foreach (var perm in grantedPermissions)
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = role.Id,
                PermissionId = perm.Id
            });
        }

        await db.SaveChangesAsync();
        return role;
    }

    public static async Task<IntegrationUserCredentials> CreateUserWithJwtAsync(
        IServiceProvider services,
        string login,
        Role role,
        ScopeLevel scopeLevel,
        Guid? scopeId)
    {
        var db = services.GetRequiredService<DiscoveryDbContext>();
        var jwtService = services.GetRequiredService<IJwtService>();
        var passwordService = services.GetRequiredService<IPasswordService>();

        var now = DateTime.UtcNow;
        var salt = passwordService.GenerateSalt();
        var hash = passwordService.HashPassword("IntegrationPass!1234", salt);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Login = login,
            Email = $"{login}@integration.local",
            FullName = $"IT {login}",
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
            Name = $"it-group-{login}-{Guid.NewGuid():N}",
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

        await db.SaveChangesAsync();
        return new IntegrationUserCredentials(user, sessionId, jwt);
    }

    public static HttpRequestMessage WithBearer(this HttpRequestMessage req, string jwt)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return req;
    }
}
