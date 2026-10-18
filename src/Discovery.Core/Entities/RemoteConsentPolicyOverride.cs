using Discovery.Core.Enums;

namespace Discovery.Core.Entities;

/// <summary>
/// Override de política de consentimento de acesso remoto por usuário ou grupo
/// de usuários. Campos nulos herdam do nível superior (Site → Cliente → Servidor).
///
/// <para>
/// O escopo opcional (ClientId/SiteId) permite que o mesmo usuário tenha
/// políticas diferentes por cliente/site. Guid.Empty significa "todos".
/// </para>
/// </summary>
public class RemoteConsentPolicyOverride
{
    public Guid Id { get; set; }

    public RemoteConsentSubjectType SubjectType { get; set; }

    /// <summary>UserId ou UserGroupId conforme <see cref="SubjectType"/>.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>Restringe o override a um cliente (Guid.Empty = todos).</summary>
    public Guid ClientId { get; set; }

    /// <summary>Restringe o override a um site (Guid.Empty = todos).</summary>
    public Guid SiteId { get; set; }

    public RemoteConsentMode? Mode { get; set; }
    public int? TimeoutSeconds { get; set; }
    public RemoteConsentTimeoutAction? TimeoutAction { get; set; }
    public bool? ShowBanner { get; set; }
    public string? Message { get; set; }
    public string? BannerText { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}
