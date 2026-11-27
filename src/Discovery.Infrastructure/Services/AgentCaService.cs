using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Discovery.Core.Interfaces.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public sealed class AgentCaService : IAgentCaService, IDisposable
{
    private const string AgentUrnPrefix = "urn:discovery:agent:";
    private const string ClientUrnPrefix = "urn:discovery:client:";
    private static readonly Oid ClientAuthOid = new("1.3.6.1.5.5.7.3.2");
    private static readonly Oid ServerAuthOid = new("1.3.6.1.5.5.7.3.1");

    private readonly string _caCertPath;
    private readonly string _caKeyPath;
    private readonly int _agentCertValidityDays;
    private readonly ILogger<AgentCaService> _logger;
    private readonly object _lock = new();

    private X509Certificate2? _caCert;
    private ECDsa? _caEcdsa;
    private RSA? _caRsa;
    private string? _caCertPem;

    public AgentCaService(IConfiguration configuration, ILogger<AgentCaService> logger)
    {
        _logger = logger;
        var defaultDir = OperatingSystem.IsLinux()
            ? "/etc/discovery-api/certs"
            : Path.Combine(AppContext.BaseDirectory, "certs");

        _caCertPath = configuration["AgentMtls:CaCertPath"] ?? Path.Combine(defaultDir, "agent-ca.crt");
        _caKeyPath = configuration["AgentMtls:CaKeyPath"] ?? Path.Combine(defaultDir, "agent-ca.key");
        var days = configuration.GetValue<int?>("AgentMtls:AgentCertValidityDays") ?? 30;
        _agentCertValidityDays = Math.Clamp(days, 1, 365);

        EnsureCaLoaded();
    }

    public string GetCaCertificatePem()
    {
        lock (_lock)
        {
            EnsureCaLoaded();
            return _caCertPem!;
        }
    }

    public X509Certificate2 GetCaCertificate()
    {
        lock (_lock)
        {
            EnsureCaLoaded();
            return _caCert!;
        }
    }

    public AgentMtlsEnrollmentResult SignAgentCsr(Guid agentId, Guid clientId, string csrPem)
    {
        if (agentId == Guid.Empty)
            throw new ArgumentException("AgentId inválido.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(csrPem))
            throw new ArgumentException("CSR PEM vazio.", nameof(csrPem));

        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequestPem(
                csrPem,
                HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"CSR inválido: {ex.Message}", nameof(csrPem), ex);
        }

        var subject = new X500DistinguishedName(
            $"CN=agent:{agentId:D}, OU=client:{clientId:D}, O=DiscoveryRMM");

        var certReq = new CertificateRequest(subject, csr.PublicKey, HashAlgorithmName.SHA256);
        certReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        certReq.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { ClientAuthOid, ServerAuthOid }, false));
        certReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(csr.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri($"{AgentUrnPrefix}{agentId:D}"));
        if (clientId != Guid.Empty)
            san.AddUri(new Uri($"{ClientUrnPrefix}{clientId:D}"));
        certReq.CertificateExtensions.Add(san.Build());

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F; // positivo segundo RFC 5280

        var now = DateTimeOffset.UtcNow;
        var notBefore = now.AddMinutes(-5);
        var notAfter = now.AddDays(_agentCertValidityDays);

        X509Certificate2 issued;
        lock (_lock)
        {
            EnsureCaLoaded();
            if (_caCert is null)
                throw new InvalidOperationException("CA não inicializada.");

            using var caWithKey = _caEcdsa is not null
                ? _caCert.CopyWithPrivateKey(_caEcdsa)
                : _caRsa is not null
                    ? _caCert.CopyWithPrivateKey(_caRsa)
                    : throw new InvalidOperationException("Chave privada da CA indisponível.");

            issued = certReq.Create(caWithKey, notBefore, notAfter, serial);
        }

        using (issued)
        {
            var certPem = issued.ExportCertificatePem();
            var serialHex = Convert.ToHexString(serial);
            var thumbprint = issued.GetCertHashString(HashAlgorithmName.SHA256);
            return new AgentMtlsEnrollmentResult(
                certPem,
                GetCaCertificatePem(),
                serialHex,
                notAfter.UtcDateTime,
                thumbprint);
        }
    }

    public AgentMtlsValidationResult ValidateClientCertificate(X509Certificate2? cert, Guid expectedAgentId)
    {
        if (cert is null)
            return new AgentMtlsValidationResult(false, "Certificado cliente mTLS ausente.", null, null);

        var now = DateTime.UtcNow;
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime())
            return new AgentMtlsValidationResult(false, "Certificado cliente mTLS expirado ou ainda não válido.", null, null);

        X509Certificate2 caCert;
        lock (_lock)
        {
            EnsureCaLoaded();
            caCert = _caCert!;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = now;

        if (!chain.Build(cert))
        {
            var status = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
            return new AgentMtlsValidationResult(false, $"Cadeia mTLS inválida: {status}", null, null);
        }

        var (agentId, clientId) = ExtractIdentities(cert);
        if (agentId is null || agentId == Guid.Empty)
            return new AgentMtlsValidationResult(false, "Certificado mTLS sem AgentId válido.", null, clientId);

        if (expectedAgentId != Guid.Empty && agentId != expectedAgentId)
            return new AgentMtlsValidationResult(false, "AgentId do certificado diverge do token.", agentId, clientId);

        return new AgentMtlsValidationResult(true, null, agentId, clientId);
    }

    private static (Guid? AgentId, Guid? ClientId) ExtractIdentities(X509Certificate2 cert)
    {
        Guid? agentId = null;
        Guid? clientId = null;

        var sanExt = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (sanExt is not null)
        {
            foreach (var rawLine in sanExt.Format(true).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                var idxAgent = line.IndexOf(AgentUrnPrefix, StringComparison.OrdinalIgnoreCase);
                if (idxAgent >= 0 && Guid.TryParse(line[(idxAgent + AgentUrnPrefix.Length)..].Trim(), out var parsedAgent))
                    agentId ??= parsedAgent;

                var idxClient = line.IndexOf(ClientUrnPrefix, StringComparison.OrdinalIgnoreCase);
                if (idxClient >= 0 && Guid.TryParse(line[(idxClient + ClientUrnPrefix.Length)..].Trim(), out var parsedClient))
                    clientId ??= parsedClient;
            }
        }

        if (agentId is null || clientId is null)
        {
            foreach (var part in cert.Subject.Split(','))
            {
                var trimmed = part.Trim();
                if (agentId is null && trimmed.StartsWith("CN=agent:", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(trimmed["CN=agent:".Length..].Trim(), out var cnAgent))
                {
                    agentId = cnAgent;
                }
                if (clientId is null && trimmed.StartsWith("OU=client:", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(trimmed["OU=client:".Length..].Trim(), out var ouClient))
                {
                    clientId = ouClient;
                }
            }
        }

        return (agentId, clientId);
    }

    private void EnsureCaLoaded()
    {
        if (_caCert is not null && (_caEcdsa is not null || _caRsa is not null))
            return;

        if (File.Exists(_caCertPath) && File.Exists(_caKeyPath))
        {
            var certPem = File.ReadAllText(_caCertPath);
            var keyPem = File.ReadAllText(_caKeyPath);
            var loaded = X509Certificate2.CreateFromPem(certPem);
            var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportFromPem(keyPem);
                _caEcdsa = ecdsa;
            }
            catch
            {
                ecdsa.Dispose();
                var rsa = RSA.Create();
                rsa.ImportFromPem(keyPem);
                _caRsa = rsa;
            }
            _caCert = loaded;
            _caCertPem = certPem;
            return;
        }

        var dir = Path.GetDirectoryName(_caCertPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            "CN=DiscoveryRMM Internal Agent CA, O=DiscoveryRMM",
            key,
            HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        var caCert = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        var newCertPem = caCert.ExportCertificatePem();
        var newKeyPem = key.ExportPkcs8PrivateKeyPem();

        File.WriteAllText(_caCertPath, newCertPem);
        File.WriteAllText(_caKeyPath, newKeyPem);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(_caKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AgentCa] Não foi possível restringir permissão de {KeyPath}", _caKeyPath);
            }
        }

        _caCert = X509Certificate2.CreateFromPem(newCertPem);
        var persistedKey = ECDsa.Create();
        persistedKey.ImportFromPem(newKeyPem);
        _caEcdsa = persistedKey;
        _caCertPem = newCertPem;
        caCert.Dispose();

        _logger.LogInformation("[AgentCa] CA interna gerada e persistida em {CertPath}", _caCertPath);
    }

    public void Dispose()
    {
        _caCert?.Dispose();
        _caEcdsa?.Dispose();
        _caRsa?.Dispose();
    }
}
