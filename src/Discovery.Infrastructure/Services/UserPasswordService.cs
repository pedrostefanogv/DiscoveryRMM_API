using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Discovery.Core.Interfaces.Auth;
using Microsoft.Extensions.Configuration;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço de hash de senhas usando Argon2id (RFC 9106).
/// Configuração: 3 iterações, 64MB de memória, paralelismo 2.
/// </summary>
public class UserPasswordService : IPasswordService
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 3;
    private const int MemorySize = 65536; // 64 MB
    private const int DegreeOfParallelism = 2;

    private readonly int _minLength;
    private readonly bool _requireUppercase;
    private readonly bool _requireDigit;
    private readonly bool _requireSpecialChar;

    public UserPasswordService(IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:PasswordPolicy");
        _minLength = section.GetValue<int>("MinLength", 12);
        _requireUppercase = section.GetValue<bool>("RequireUppercase", true);
        _requireDigit = section.GetValue<bool>("RequireDigit", true);
        _requireSpecialChar = section.GetValue<bool>("RequireSpecialChar", true);
    }

    public string GenerateSalt()
    {
        var salt = new byte[SaltBytes];
        RandomNumberGenerator.Fill(salt);
        return Convert.ToBase64String(salt);
    }

    public string HashPassword(string password, string saltBase64)
    {
        var salt = Convert.FromBase64String(saltBase64);
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        using var argon2 = new Argon2id(passwordBytes)
        {
            Salt = salt,
            DegreeOfParallelism = DegreeOfParallelism,
            MemorySize = MemorySize,
            Iterations = Iterations
        };

        var hash = argon2.GetBytes(HashBytes);
        return Convert.ToBase64String(hash);
    }

    public bool VerifyPassword(string password, string saltBase64, string hashBase64)
    {
        var expectedHash = HashPassword(password, saltBase64);
        // Comparação em tempo constante para evitar timing attacks
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(expectedHash),
            Convert.FromBase64String(hashBase64));
    }

    public (bool Valid, string? Reason) ValidatePolicy(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < _minLength)
            return (false, $"A senha deve ter no mínimo {_minLength} caracteres.");

        if (_requireUppercase && !password.Any(char.IsUpper))
            return (false, "A senha deve conter pelo menos uma letra maiúscula.");

        if (_requireDigit && !password.Any(char.IsDigit))
            return (false, "A senha deve conter pelo menos um número.");

        if (_requireSpecialChar && !password.Any(c => !char.IsLetterOrDigit(c)))
            return (false, "A senha deve conter pelo menos um caractere especial.");

        return (true, null);
    }
}
