namespace Discovery.Core.DTOs.ApiTokens;

public class CreateApiTokenRequestDto
{
    public string Name { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Retornado SOMENTE na criação do token.
/// AccessKey não é mais acessível após esta resposta.
/// </summary>
public class CreateApiTokenResponseDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenIdPublic { get; set; } = string.Empty;

    /// <summary>
    /// Chave de acesso completa. Formato: mzk_{base64url}
    /// EXIBIDA SOMENTE UMA VEZ. Não é possível recuperá-la depois.
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class ApiTokenSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenIdPublic { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
