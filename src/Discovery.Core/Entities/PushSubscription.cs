namespace Discovery.Core.Entities;

/// <summary>
/// Inscricao de Web Push (RFC 8030 / RFC 8291) de um navegador ou dispositivo
/// do usuario. Um mesmo usuario pode ter N inscricoes — uma por navegador/perfil.
/// </summary>
public class PushSubscription
{
    public Guid Id { get; set; }

    /// <summary>Usuario dono da inscricao. Nunca vem do corpo da requisicao.</summary>
    public Guid UserId { get; set; }

    /// <summary>Endpoint de push do provedor (FCM/Mozilla/Apple/WNS). Chave natural unica.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Chave publica do cliente (P-256, base64url) usada no ECDH.</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>Segredo de autenticacao do cliente (base64url).</summary>
    public string Auth { get; set; } = string.Empty;

    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Ultima vez que o navegador confirmou a inscricao (registro/refresh na
    /// carga do console). Usado pela retencao para descartar dispositivos orfaos.
    /// </summary>
    public DateTime LastSeenAt { get; set; }
}
