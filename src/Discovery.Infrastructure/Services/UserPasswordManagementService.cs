using Discovery.Core.Entities.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Identity;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Implementação de IUserPasswordManagementService.
/// Orquestra IPasswordService + IUserRepository para operações de senha.
/// </summary>
public sealed class UserPasswordManagementService : IUserPasswordManagementService
{
    private readonly IUserRepository _userRepo;
    private readonly IPasswordService _passwordService;
    private readonly ILogger<UserPasswordManagementService> _logger;

    public UserPasswordManagementService(
        IUserRepository userRepo,
        IPasswordService passwordService,
        ILogger<UserPasswordManagementService> logger)
    {
        _userRepo = userRepo;
        _passwordService = passwordService;
        _logger = logger;
    }

    public async Task ResetPasswordAsync(
        Guid userId, string newPassword, string? requestedBy, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null)
            throw new KeyNotFoundException($"User {userId} not found");

        var validation = _passwordService.ValidatePolicy(newPassword);
        if (!validation.Valid)
            throw new ArgumentException(validation.Reason ?? "Password does not meet policy requirements", nameof(newPassword));

        var salt = _passwordService.GenerateSalt();
        user.PasswordSalt = salt;
        user.PasswordHash = _passwordService.HashPassword(newPassword, salt);
        user.MustChangePassword = false;
        user.FailedLoginAttempts = 0;
        user.LockoutUntil = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _userRepo.UpdateAsync(user);
        _logger.LogInformation("Password reset for user {UserId} by {RequestedBy}", userId, requestedBy);
    }

    public async Task ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByIdAsync(userId);
        if (user is null)
            throw new KeyNotFoundException($"User {userId} not found");

        if (!_passwordService.VerifyPassword(currentPassword, user.PasswordSalt, user.PasswordHash))
            throw new UnauthorizedAccessException("Current password is incorrect");

        var validation = _passwordService.ValidatePolicy(newPassword);
        if (!validation.Valid)
            throw new ArgumentException(validation.Reason ?? "Password does not meet policy requirements", nameof(newPassword));

        var salt = _passwordService.GenerateSalt();
        user.PasswordSalt = salt;
        user.PasswordHash = _passwordService.HashPassword(newPassword, salt);
        user.MustChangePassword = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _userRepo.UpdateAsync(user);
        _logger.LogInformation("Password changed for user {UserId}", userId);
    }
}
