using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Discovery.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

public class AgentCaServiceTests
{
    [Test]
    public void GeneratesCaOnDisk_SignsCsr_AndValidatesClientCertificate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "discovery-ca-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var certPath = Path.Combine(tempDir, "agent-ca.crt");
            var keyPath = Path.Combine(tempDir, "agent-ca.key");
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentMtls:CaCertPath"] = certPath,
                    ["AgentMtls:CaKeyPath"] = keyPath,
                    ["AgentMtls:AgentCertValidityDays"] = "30"
                })
                .Build();

            using var caService = new AgentCaService(config, NullLogger<AgentCaService>.Instance);
            Assert.That(File.Exists(certPath), Is.True);
            Assert.That(File.Exists(keyPath), Is.True);

            var agentId = Guid.NewGuid();
            var clientId = Guid.NewGuid();
            using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = new CertificateRequest($"CN=agent:{agentId:D}", agentKey, HashAlgorithmName.SHA256);
            var csrPem = csr.CreateSigningRequestPem();

            var enrolled = caService.SignAgentCsr(agentId, clientId, csrPem);
            Assert.That(string.IsNullOrWhiteSpace(enrolled.CertificatePem), Is.False);
            Assert.That(string.IsNullOrWhiteSpace(enrolled.CaCertificatePem), Is.False);

            using var issued = X509Certificate2.CreateFromPem(enrolled.CertificatePem);
            var ok = caService.ValidateClientCertificate(issued, agentId);
            Assert.That(ok.IsValid, Is.True, ok.Error);
            Assert.That(ok.AgentId, Is.EqualTo(agentId));
            Assert.That(ok.ClientId, Is.EqualTo(clientId));

            // AgentId divergente deve ser rejeitado
            var wrongAgent = caService.ValidateClientCertificate(issued, Guid.NewGuid());
            Assert.That(wrongAgent.IsValid, Is.False);

            // Certificado ausente deve ser rejeitado
            var missing = caService.ValidateClientCertificate(null, agentId);
            Assert.That(missing.IsValid, Is.False);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
