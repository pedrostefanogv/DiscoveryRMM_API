namespace Discovery.Core.Interfaces.Auth;

public interface IPasswordService
{
    /// <summary>Gera salt aleatório (16 bytes) e retorna Base64.</summary>
    string GenerateSalt();

    /// <summary>Gera hash Argon2id de password com o salt fornecido. Retorna Base64.</summary>
    string HashPassword(string password, string saltBase64);

    /// <summary>Verifica se password corresponde ao hash+salt armazenados.</summary>
    bool VerifyPassword(string password, string saltBase64, string hashBase64);

    /// <summary>Valida se a senha atende à política de complexidade configurada.</summary>
    (bool Valid, string? Reason) ValidatePolicy(string password);
}
