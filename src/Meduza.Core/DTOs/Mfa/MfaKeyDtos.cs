using Meduza.Core.Enums.Security;

namespace Meduza.Core.DTOs.Mfa;

public class MfaKeyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public MfaKeyType KeyType { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class RegisterMfaKeyNameDto
{
    /// <summary>Nome amigável para a nova chave (ex: "YubiKey 5C", "iPhone 15 Passkey").</summary>
    public string KeyName { get; set; } = string.Empty;
}

public class AdminUserMfaKeyDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public MfaKeyType KeyType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}
