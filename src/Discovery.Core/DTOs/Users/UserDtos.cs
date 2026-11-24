using Discovery.Core.Enums.Identity;
using Discovery.Core.Enums.Security;

namespace Discovery.Core.DTOs.Users;

public class CreateUserDto
{
    public string Login { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool MfaRequired { get; set; } = true;
}

public class UpdateUserDto
{
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public bool? IsActive { get; set; }
    public bool? MfaRequired { get; set; }
}

public class UpdateMyProfileDto
{
    public string? Email { get; set; }
    public string? FullName { get; set; }
}

public class MySecurityProfileDto
{
    public bool MfaRequired { get; set; }
    public bool MfaConfigured { get; set; }
    public RoleMfaRequirement RoleMfaRequirement { get; set; } = RoleMfaRequirement.None;
    public IReadOnlyList<MyMfaKeySummaryDto> Keys { get; set; } = [];
}

public class MyMfaKeySummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public MfaKeyType KeyType { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class ChangePasswordDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Estado de segurança administrativo do usuário: lockout de senha e de MFA e
/// obrigações de troca. Exposto apenas em GET /api/v1/users/{id}/security-state,
/// restrito a Users.Edit (antes os contadores vazavam no UserDto geral).
/// </summary>
public class UserSecurityStateDto
{
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
    public bool MfaRequired { get; set; }
    public bool MfaConfigured { get; set; }
    public bool MustChangePassword { get; set; }
    public bool MustChangeProfile { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutUntil { get; set; }
    public int MfaFailedAttempts { get; set; }
    public DateTime? MfaLockoutUntil { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// Resumo seguro de uma sessão do usuário (nunca expõe AccessTokenHash nem RefreshTokenHash).
/// </summary>
public class UserSessionSummaryDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool IsActive { get; set; }
    public bool IsExpired { get; set; }
    public bool IsRevoked { get; set; }
    public bool MfaVerified { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>
/// Item consolidado de atividade/auditoria de um usuário no sistema.
/// </summary>
public class UserActivityItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public bool Success { get; set; } = true;
    public string? FailureReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? ActorLogin { get; set; }
    public string Role { get; set; } = "self";
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? LinkPath { get; set; }
    public string? Details { get; set; }
    public DateTime OccurredAt { get; set; }
}

/// <summary>
/// Resposta consolidada de atividades de um usuário por categoria.
/// </summary>
public class UserActivityResponseDto
{
    public IReadOnlyList<UserActivityItemDto> Items { get; set; } = [];
    public Dictionary<string, int> CategoryCounts { get; set; } = new();
    public int TotalReturned { get; set; }
}

// UserDto/UserSummaryDto/UserGroupSummaryDto foram removidos: eram duplicatas sem
// nenhuma referência (o contrato real vive em Discovery.Core.Cqrs.Users.Commands).
