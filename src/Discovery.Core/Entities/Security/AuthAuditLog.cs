namespace Discovery.Core.Entities.Security;

/// <summary>
/// Registro de auditoria de eventos de autenticação.
/// Cada login, logout, falha de login, MFA assertion e lockout é registrado.
/// </summary>
public class AuthAuditLog
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredAt { get; set; }
}
