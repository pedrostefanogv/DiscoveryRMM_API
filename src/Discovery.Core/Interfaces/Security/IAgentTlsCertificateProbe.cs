namespace Discovery.Core.Interfaces.Security;

/// <summary>
/// Observa o certificado TLS publicado nos endpoints acessados pelos agents
/// e devolve o SHA-256 (hex, minúsculas) do certificado folha.
/// Usado para popular <c>apiTlsCertHash</c>/<c>natsTlsCertHash</c> na
/// configuração efetiva do agent — a ponta do pin de TLS (C2).
/// </summary>
public interface IAgentTlsCertificateProbe
{
    Task<string?> GetExpectedTlsCertHashAsync(CancellationToken cancellationToken = default);

    /// <summary>Hash do certificado do endpoint NATS/WSS (sonda própria; null se não configurada).</summary>
    Task<string?> GetExpectedNatsTlsCertHashAsync(CancellationToken cancellationToken = default);

    Task<string?> GetExpectedTlsCertHashAsync(string probeUrl, string cacheKey, CancellationToken cancellationToken = default);
    void InvalidateCache(string cacheKey);
}
