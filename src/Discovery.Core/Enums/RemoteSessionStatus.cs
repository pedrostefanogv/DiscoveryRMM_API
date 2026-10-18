namespace Discovery.Core.Enums;

/// <summary>Status canônicos de uma sessão remota (coluna remote_sessions.status).</summary>
public static class RemoteSessionStatus
{
    /// <summary>Aguardando a decisão do usuário do computador acessado.</summary>
    public const string PendingConsent = "pending_consent";

    public const string Active = "active";
    public const string Closed = "closed";
    public const string Expired = "expired";

    /// <summary>Consentimento negado (manual, timeout ou sem usuário).</summary>
    public const string Denied = "denied";

    /// <summary>Status em que a sessão ainda ocupa vaga/conflito.</summary>
    public static bool IsOpen(string? status) =>
        string.Equals(status, Active, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, PendingConsent, StringComparison.OrdinalIgnoreCase);
}
