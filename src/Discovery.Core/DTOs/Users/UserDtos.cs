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

public class UserDto
{
    public Guid Id { get; set; }
    public string Login { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool MfaRequired { get; set; }
    public bool MfaConfigured { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public IEnumerable<UserGroupSummaryDto> Groups { get; set; } = [];
}

public class UserSummaryDto
{
    public Guid Id { get; set; }
    public string Login { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool MfaConfigured { get; set; }
}

public class UserGroupSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
