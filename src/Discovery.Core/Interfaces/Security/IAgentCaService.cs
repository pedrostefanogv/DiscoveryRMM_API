using System.Security.Cryptography.X509Certificates;

namespace Discovery.Core.Interfaces.Security;

public sealed record AgentMtlsEnrollmentResult(
    string CertificatePem,
    string CaCertificatePem,
    string SerialNumber,
    DateTime ExpiresAtUtc,
    string ThumbprintSha256);

public sealed record AgentMtlsValidationResult(
    bool IsValid,
    string? Error,
    Guid? AgentId,
    Guid? ClientId);

/// <summary>
/// CA interna da DiscoveryRMM para emissão e validação de certificados mTLS de agentes.
/// </summary>
public interface IAgentCaService
{
    string GetCaCertificatePem();
    X509Certificate2 GetCaCertificate();
    AgentMtlsEnrollmentResult SignAgentCsr(Guid agentId, Guid clientId, string csrPem);
    AgentMtlsValidationResult ValidateClientCertificate(X509Certificate2? cert, Guid expectedAgentId);
}
