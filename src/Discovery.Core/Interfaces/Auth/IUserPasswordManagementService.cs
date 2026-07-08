namespace Discovery.Core.Interfaces.Auth;

/// <summary>
/// Serviço de aplicação para gerenciamento de senhas de usuário.
/// Encapsula a orquestração entre IPasswordService, IUserRepository e regras de negócio.
/// </summary>
public interface IUserPasswordManagementService
{
    /// <summary>Reseta a senha de um usuário (fluxo administrativo).</summary>
    Task ResetPasswordAsync(Guid userId, string newPassword, string? requestedBy, CancellationToken ct = default);

    /// <summary>Troca a própria senha (requer senha atual).</summary>
    Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default);
}
