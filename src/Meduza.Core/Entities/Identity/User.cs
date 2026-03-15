namespace Meduza.Core.Entities.Identity;

public class User
{
    public Guid Id { get; set; }

    /// <summary>Nome de login único (username). Pode ser usado no lugar do email para logar.</summary>
    public string Login { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>Se verdadeiro, o usuário precisa ter MFA configurado e verificado em cada sessão.</summary>
    public bool MfaRequired { get; set; } = true;

    /// <summary>Indica se o usuário já registrou pelo menos uma chave MFA.</summary>
    public bool MfaConfigured { get; set; } = false;

    /// <summary>Força troca de senha no primeiro acesso.</summary>
    public bool MustChangePassword { get; set; } = false;

    /// <summary>Força atualização de perfil (login/email/nome) no primeiro acesso.</summary>
    public bool MustChangeProfile { get; set; } = false;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
