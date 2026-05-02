using Discovery.Core.Entities.Identity;
using Discovery.Core.Entities.Security;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Data;

/// <summary>
/// Identity & Security entity configurations: Users, Groups, Roles, Permissions, Sessions, MFA, API tokens.
/// </summary>
public partial class DiscoveryDbContext
{
    static partial void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(u => u.Login).HasColumnName("login").HasMaxLength(100);
            e.Property(u => u.Email).HasColumnName("email").HasMaxLength(256);
            e.Property(u => u.FullName).HasColumnName("full_name").HasMaxLength(256);
            e.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(256);
            e.Property(u => u.PasswordSalt).HasColumnName("password_salt").HasMaxLength(64);
            e.Property(u => u.IsActive).HasColumnName("is_active");
            e.Property(u => u.MfaRequired).HasColumnName("mfa_required");
            e.Property(u => u.MfaConfigured).HasColumnName("mfa_configured");
            e.Property(u => u.MustChangePassword).HasColumnName("must_change_password");
            e.Property(u => u.MustChangeProfile).HasColumnName("must_change_profile");
            e.Property(u => u.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(u => u.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(u => u.LastLoginAt).HasColumnName("last_login_at").HasColumnType("timestamptz");
            e.Property(u => u.FailedLoginAttempts).HasColumnName("failed_login_attempts");
            e.Property(u => u.LockoutUntil).HasColumnName("lockout_until").HasColumnType("timestamptz");
            e.Property(u => u.MeshCentralUserId).HasColumnName("meshcentral_user_id").HasMaxLength(256);
            e.Property(u => u.MeshCentralUsername).HasColumnName("meshcentral_username").HasMaxLength(100);
            e.Property(u => u.MeshCentralLastSyncedAt).HasColumnName("meshcentral_last_synced_at").HasColumnType("timestamptz");
            e.Property(u => u.MeshCentralSyncStatus).HasColumnName("meshcentral_sync_status").HasMaxLength(32);
            e.Property(u => u.MeshCentralSyncError).HasColumnName("meshcentral_sync_error").HasMaxLength(1024);
            e.HasIndex(u => u.Login).IsUnique().HasDatabaseName("ix_users_login");
            e.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ix_users_email");
            e.HasIndex(u => u.MeshCentralUserId).HasDatabaseName("ix_users_meshcentral_user_id");
        });

        modelBuilder.Entity<UserGroup>(e =>
        {
            e.ToTable("user_groups");
            e.HasKey(g => g.Id);
            e.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(g => g.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(g => g.Description).HasColumnName("description").HasMaxLength(1000);
            e.Property(g => g.IsActive).HasColumnName("is_active");
            e.Property(g => g.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(g => g.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserGroupMembership>(e =>
        {
            e.ToTable("user_group_memberships");
            e.HasKey(m => new { m.UserId, m.GroupId });
            e.Property(m => m.UserId).HasColumnName("user_id");
            e.Property(m => m.GroupId).HasColumnName("group_id");
            e.Property(m => m.JoinedAt).HasColumnName("joined_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(r => r.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(r => r.Description).HasColumnName("description").HasMaxLength(1000);
            e.Property(r => r.Type).HasColumnName("type").HasConversion<string>();
            e.Property(r => r.IsSystem).HasColumnName("is_system");
            e.Property(r => r.MfaRequirement).HasColumnName("mfa_requirement").HasConversion<string>();
            e.Property(r => r.MeshRightsMask).HasColumnName("mesh_rights_mask");
            e.Property(r => r.MeshRightsProfile).HasColumnName("mesh_rights_profile").HasMaxLength(64);
            e.Property(r => r.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(r => r.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(p => p.ResourceType).HasColumnName("resource_type").HasConversion<string>();
            e.Property(p => p.ActionType).HasColumnName("action_type").HasConversion<string>();
            e.Property(p => p.Description).HasColumnName("description").HasMaxLength(500);
            e.HasIndex(p => new { p.ResourceType, p.ActionType }).IsUnique().HasDatabaseName("ix_permissions_resource_action");
        });

        modelBuilder.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(rp => new { rp.RoleId, rp.PermissionId });
            e.Property(rp => rp.RoleId).HasColumnName("role_id");
            e.Property(rp => rp.PermissionId).HasColumnName("permission_id");
        });

        modelBuilder.Entity<UserGroupRole>(e =>
        {
            e.ToTable("user_group_roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(r => r.GroupId).HasColumnName("group_id");
            e.Property(r => r.RoleId).HasColumnName("role_id");
            e.Property(r => r.ScopeLevel).HasColumnName("scope_level").HasConversion<string>();
            e.Property(r => r.ScopeId).HasColumnName("scope_id");
            e.Property(r => r.AssignedAt).HasColumnName("assigned_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserMfaKey>(e =>
        {
            e.ToTable("user_mfa_keys");
            e.HasKey(k => k.Id);
            e.Property(k => k.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(k => k.UserId).HasColumnName("user_id");
            e.Property(k => k.KeyType).HasColumnName("key_type").HasConversion<string>();
            e.Property(k => k.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(k => k.IsActive).HasColumnName("is_active");
            e.Property(k => k.CredentialIdBase64).HasColumnName("credential_id_base64").HasMaxLength(512);
            e.Property(k => k.PublicKeyBase64).HasColumnName("public_key_base64").HasMaxLength(2048);
            e.Property(k => k.SignCount).HasColumnName("sign_count");
            e.Property(k => k.AaguidBase64).HasColumnName("aaguid_base64").HasMaxLength(64);
            e.Property(k => k.UserHandleBase64).HasColumnName("user_handle_base64").HasMaxLength(128);
            e.Property(k => k.OtpSecretEncrypted).HasColumnName("otp_secret_encrypted").HasMaxLength(512);
            e.Property(k => k.BackupCodeHashes).HasColumnName("backup_code_hashes").HasColumnType("text[]");
            e.Property(k => k.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(k => k.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(s => s.UserId).HasColumnName("user_id");
            e.Property(s => s.AccessTokenHash).HasColumnName("access_token_hash").HasMaxLength(128);
            e.Property(s => s.RefreshTokenHash).HasColumnName("refresh_token_hash").HasMaxLength(128);
            e.Property(s => s.MfaVerified).HasColumnName("mfa_verified");
            e.Property(s => s.IpAddress).HasColumnName("ip_address").HasMaxLength(64);
            e.Property(s => s.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
            e.Property(s => s.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(s => s.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            e.Property(s => s.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamptz");
            e.HasIndex(s => s.RefreshTokenHash).HasDatabaseName("ix_user_sessions_refresh_token_hash");
            e.HasIndex(s => new { s.UserId, s.RevokedAt }).HasDatabaseName("ix_user_sessions_user_active");
        });

        modelBuilder.Entity<ApiToken>(e =>
        {
            e.ToTable("api_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(t => t.UserId).HasColumnName("user_id");
            e.Property(t => t.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(t => t.TokenIdPublic).HasColumnName("token_id_public").HasMaxLength(50);
            e.Property(t => t.AccessKeyHash).HasColumnName("access_key_hash").HasMaxLength(128);
            e.Property(t => t.IsActive).HasColumnName("is_active");
            e.Property(t => t.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(t => t.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
            e.Property(t => t.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            e.HasIndex(t => t.TokenIdPublic).IsUnique().HasDatabaseName("ix_api_tokens_token_id_public");
            e.HasIndex(t => t.UserId).HasDatabaseName("ix_api_tokens_user_id");
        });
    }
}
