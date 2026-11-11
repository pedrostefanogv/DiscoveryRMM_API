using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discovery.Core.Cqrs.Mfa;
using Discovery.Core.Entities.Security;
using Discovery.Core.Enums.Security;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Cqrs.Mfa;
using Discovery.Infrastructure.Services;
using Fido2NetLib.Cbor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Correções de MFA/FIDO2 da investigação do Android
/// (RELATORIO_MFA_FIDO2_ANDROID_2026-11-01.md):
/// - timeout do challenge e TTL do Redis alinhados (antes 60 s na lib / 120 s no Redis);
/// - transports persistidos e devolvidos em allowCredentials;
/// - callbacks de segurança consultando o banco em vez de `true` fixo.
/// </summary>
[TestFixture]
public class Fido2AuthServiceTests
{
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ─── Item 1: timeout / TTL do challenge ─────────────────────────────────

    [Test]
    public void ChallengeTtl_CoversTheTimeoutAnnouncedToTheClient()
    {
        // Timeout de 120 s (o valor pedido no relatório) precisa de TTL > 120 s; 180 é o piso.
        var ttl = Fido2AuthService.ComputeChallengeTtlSeconds(configuredTtlSeconds: 120, timeoutMs: 120_000);
        Assert.That(ttl, Is.GreaterThanOrEqualTo(180), "o TTL jamais pode ficar abaixo do mínimo de 180 s");
        Assert.That(ttl, Is.GreaterThanOrEqualTo(180), "120 s de timeout + folga");
    }

    [Test]
    public void ChallengeTtl_RespectsConfiguredValue_WhenItAlreadyCoversTheTimeout()
    {
        var ttl = Fido2AuthService.ComputeChallengeTtlSeconds(configuredTtlSeconds: 300, timeoutMs: 120_000);
        Assert.That(ttl, Is.EqualTo(300), "valor configurado que já cobre o timeout deve ser respeitado");
    }

    [Test]
    public void ChallengeTtl_IsCapped_ToAvoidAnOverlongReplayWindow()
    {
        var ttl = Fido2AuthService.ComputeChallengeTtlSeconds(configuredTtlSeconds: 86_400, timeoutMs: 600_000);
        Assert.That(ttl, Is.EqualTo(900), "o challenge é anti-replay; o TTL tem teto");
    }

    [Test]
    public void BeginAssertion_AnnouncesConfiguredTimeout_AndPersistsChallengeWithTtl()
    {
        var (service, redis, _) = new Harness(timeoutMs: 120_000, challengeTtlSeconds: 300);
        var key = NewFido2Key(UserA, [1, 2, 3]);

        var optionsJson = service.BeginAssertionAsync(UserA, [key]).GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(optionsJson);
        Assert.That(doc.RootElement.GetProperty("timeout").GetInt64(), Is.EqualTo(120_000),
            "o default de 60 s da lib era curto demais para o fluxo híbrido do Android");
        Assert.That(redis.LastExpirySeconds, Is.EqualTo(300), "o challenge tem de viver mais que o timeout anunciado");
    }

    // ─── Item 3: transports em allowCredentials ─────────────────────────────

    [Test]
    public void BeginAssertion_ReturnsPersistedTransports_InAllowCredentials()
    {
        var (service, _, _) = new Harness();
        var key = NewFido2Key(UserA, [1, 2, 3]);
        key.Transports = "internal,hybrid";

        var optionsJson = service.BeginAssertionAsync(UserA, [key]).GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(optionsJson);
        var allow = doc.RootElement.GetProperty("allowCredentials").EnumerateArray().Single();
        var transports = allow.GetProperty("transports").EnumerateArray().Select(t => t.GetString()).ToList();

        Assert.That(transports, Is.EquivalentTo(new[] { "internal", "hybrid" }),
            "sem transports o Android não recebe a dica de credencial internal/hybrid");
    }

    [Test]
    public void BeginAssertion_OmitsTransports_ForKeyWithoutThem()
    {
        var (service, _, _) = new Harness();
        var key = NewFido2Key(UserA, [9, 9, 9]);

        var optionsJson = service.BeginAssertionAsync(UserA, [key]).GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(optionsJson);
        var allow = doc.RootElement.GetProperty("allowCredentials").EnumerateArray().Single();
        var hasTransports = allow.TryGetProperty("transports", out var transports)
                            && transports.ValueKind == JsonValueKind.Array
                            && transports.GetArrayLength() > 0;

        Assert.That(hasTransports, Is.False, "credencial legada sem transports continua válida, só sem a dica");
    }

    [Test]
    public void ParseTransports_ReadsTheStoredVocabulary_AndIgnoresUnknownValues()
    {
        var parsed = Fido2AuthService.ParseTransports("internal,HYBRID, usb ,inventado");

        Assert.That(parsed.Select(t => t.ToString().ToLowerInvariant()),
            Is.EquivalentTo(new[] { "internal", "hybrid", "usb" }));
    }

    [Test]
    public void BeginAssertion_AcceptsLegacyStandardBase64CredentialId()
    {
        // Credenciais antigas foram gravadas em base64 padrão; a decodificação tem de aceitar
        // as duas formas, senão o login delas passaria a falhar.
        var (service, _, _) = new Harness();
        var key = NewFido2Key(UserA, [0xFB, 0xFF, 0xFE]);
        key.CredentialIdBase64 = Convert.ToBase64String([0xFB, 0xFF, 0xFE]);

        var optionsJson = service.BeginAssertionAsync(UserA, [key]).GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(optionsJson);
        var allow = doc.RootElement.GetProperty("allowCredentials").EnumerateArray().Single();
        Assert.That(allow.GetProperty("id").GetString(), Is.EqualTo("-__-"),
            "o id volta em base64url, o formato que o navegador entende");
    }

    // ─── Regressão 6188cd8: comparação do credential id na assertion ───────

    /// <summary>
    /// Regressão da commit 6188cd8 (2026-11-09): o registro passou a gravar o credential id em
    /// base64url (sem padding — ver PersistPendingKeyAsync), mas o CompleteAssertionAsync continuou
    /// comparando strings cruas contra Convert.ToBase64String(RawId) (base64 padrão, com '=').
    /// Em produção TODA asserção FIDO2 passou a falhar com "Credencial não reconhecida.".
    /// </summary>
    [Test]
    public void CompleteAssertion_FindsKeyStoredAsBase64Url_AsTheRegistrationPathPersists()
    {
        var (service, _, repo) = new Harness();
        var credentialId = new byte[] { 0xE0, 0xD1, 0xB5, 0x76, 0xCF, 0x17, 0x1D, 0x71, 0x01, 0x48, 0xA1, 0xE7, 0x52, 0x6D, 0x91, 0xA8, 0xFE, 0x61, 0x8C, 0xE2, 0xE4, 0x1A, 0x74, 0x87, 0x97, 0x7D, 0x03, 0xAA, 0x5E, 0x98, 0xAE, 0x23 };

        var key = NewFido2Key(UserA, credentialId);
        key.CredentialIdBase64 = Fido2AuthService.ToBase64Url(credentialId); // como o registro grava
        key.PublicKeyBase64 = AssertionFixture.PublicKeyBase64;
        // IsUserHandleOwnerOfCredentialId consulta o repositório: sem semear, a lib recusaria
        // a assertion por "user is not owner" antes mesmo da verificação de assinatura.
        repo.Seed(key);

        Assert.That(key.CredentialIdBase64, Is.EqualTo("4NG1ds8XHXEBSKHnUm2RqP5hjOLkGnSHl30Dql6YriM"),
            "pré-condição: exatamente o id da produção, em base64url de 43 chars");
        Assert.That(key.CredentialIdBase64, Does.Not.Contain("="), "base64url não leva padding");
        Assert.That(Convert.ToBase64String(credentialId),
            Is.EqualTo("4NG1ds8XHXEBSKHnUm2RqP5hjOLkGnSHl30Dql6YriM="),
            "pré-condição: a conversão antiga (linha 246) produzia a forma COM '=' — 44 chars");

        var assertionJson = AssertionFixture.Build(service, UserA, key, credentialId);

        // Antes da correção: InvalidOperationException("Credencial não reconhecida.") — nunca casava.
        var result = service.CompleteAssertionAsync(UserA, assertionJson, [key]).GetAwaiter().GetResult();

        Assert.That(result.KeyId, Is.EqualTo(key.Id), "a chave gravada em base64url precisa ser encontrada");
        Assert.That(result.CredentialIdBase64, Is.EqualTo(Convert.ToBase64String(credentialId)),
            "o resultado segue devolvendo a forma de sempre (compatibilidade do contrato)");
    }

    [Test]
    public void CompleteAssertion_StillFindsLegacyKeyStoredAsStandardBase64()
    {
        // Credenciais antigas ficaram em base64 padrão COM padding; a comparação tem de continuar
        // aceitando as duas formas — a correção não pode trocar um bug por outro.
        var (service, _, repo) = new Harness();
        var key = NewFido2Key(UserA, [1, 2, 3]);
        key.CredentialIdBase64 = Convert.ToBase64String([1, 2, 3]);
        key.PublicKeyBase64 = AssertionFixture.PublicKeyBase64;
        // 3 bytes -> 4 chars, sem padding (só comprimentos não múltiplos de 3 geram '=').
        Assert.That(key.CredentialIdBase64, Is.EqualTo("AQID"));
        repo.Seed(key);

        var assertionJson = AssertionFixture.Build(service, UserA, key, [1, 2, 3]);

        var result = service.CompleteAssertionAsync(UserA, assertionJson, [key]).GetAwaiter().GetResult();

        Assert.That(result.KeyId, Is.EqualTo(key.Id), "chave legada em base64 padrão continua aceita");
    }

    [Test]
    public void CompleteAssertion_RejectsCredentialOfAnotherKey_WithTheSameErrorAsBefore()
    {
        // A tolerância é de CODIFICAÇÃO, não de identidade: id diferente continua recusado, com o
        // mesmo tipo e a mesma mensagem (o log de produção e o suporte dependem disso).
        var (service, _, _) = new Harness();
        var registered = NewFido2Key(UserA, [1, 2, 3]);
        registered.PublicKeyBase64 = AssertionFixture.PublicKeyBase64;
        var other = NewFido2Key(UserA, [9, 9, 9]);
        other.PublicKeyBase64 = AssertionFixture.PublicKeyBase64;

        var assertionJson = AssertionFixture.Build(service, UserA, registered, [1, 2, 3]);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAssertionAsync(UserA, assertionJson, [other]))
            .GetAwaiter().GetResult();

        Assert.That(ex.Message, Is.EqualTo("Credencial não reconhecida."));
    }

    // Pares REAIS: mesmos bytes, codificações diferentes. ("AQID"/"AQID=" NÃO é par válido:
    // um valor de 3 bytes não leva padding; "AQID=" é base64 malformado.)
    [TestCase("4NG1ds8XHXEBSKHnUm2RqP5hjOLkGnSHl30Dql6YriM", "4NG1ds8XHXEBSKHnUm2RqP5hjOLkGnSHl30Dql6YriM=")] // só o padding difere
    [TestCase("4NG1dg", "4NG1dg==")] // 4 bytes: base64url sem padding vs base64 padrão com '=='
    [TestCase("-___", "+///")] // mesmo valor, alfabetos diferentes
    [TestCase("AQID", "AQID")] // 3 bytes: as duas formas coincidem (sem padding)
    public void CredentialIdMatches_AcceptsBothEncodingsOfTheSameBytes(string base64Url, string base64Standard)
    {
        Assert.That(Fido2AuthService.CredentialIdMatches(base64Url, base64Standard), Is.True);
        Assert.That(Fido2AuthService.CredentialIdMatches(base64Standard, base64Url), Is.True);
        Assert.That(Fido2AuthService.CredentialIdMatches(base64Url, base64Url), Is.True);
    }

    [Test]
    public void CredentialIdMatches_IsFalse_ForDifferentOrInvalidValues()
    {
        Assert.That(Fido2AuthService.CredentialIdMatches("AQID", "BAUG"), Is.False, "ids diferentes não casam");
        Assert.That(Fido2AuthService.CredentialIdMatches(null, "AQID"), Is.False);
        Assert.That(Fido2AuthService.CredentialIdMatches("AQID", null), Is.False);
        Assert.That(Fido2AuthService.CredentialIdMatches("AQID", ""), Is.False);
        Assert.That(Fido2AuthService.CredentialIdMatches("AQID", "não é base64!"), Is.False,
            "valor indecifrável não pode virar match");
    }

    // ─── Itens 4 e 5: telemetria e handler do registro ──────────────────────

    [Test]
    public void LogClientError_DoesNotThrow_ForHostileInput()
    {
        var (service, _, _) = new Harness();

        Assert.DoesNotThrow(() => service.LogClientError(null, null, null, null, null));
        Assert.DoesNotThrow(() => service.LogClientError(UserA, "NotAllowedError", new string('x', 5000), "mfa-assert-begin", "UA"));
        Assert.DoesNotThrow(() => service.LogClientError(Guid.Empty, "SecurityError", "origem incompatível", "mfa-assert-complete", null));
    }

    [Test]
    public async Task ReportClientError_AlwaysSucceeds_SoTelemetryNeverBreaksTheMfaScreen()
    {
        var (service, _, _) = new Harness();
        var handler = new ReportFido2ClientErrorCommandHandler(service, NullLogger<ReportFido2ClientErrorCommandHandler>.Instance);

        var result = await handler.Handle(
            new ReportFido2ClientErrorCommand(UserA, "NotAllowedError", "usuário cancelou", "mfa-assert-begin", "Android"),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
    }

    [Test]
    public async Task CompleteRegistration_RenamesTheKeyPersistedByTheService()
    {
        // O serviço grava a credencial no complete (nome temporário) e o handler confirma o
        // nome escolhido pelo usuário — sem gravar uma segunda linha.
        var userId = Guid.NewGuid();
        var repo = new InMemoryMfaKeyRepository();
        var pending = NewFido2Key(userId, [7, 7, 7]);
        pending.Name = Fido2AuthService.PendingCredentialName;
        repo.Seed(pending);

        var result = new Discovery.Core.DTOs.Mfa.Fido2CredentialResult
        {
            Success = true,
            CredentialIdBase64 = pending.CredentialIdBase64!,
            PublicKeyBase64 = "pk",
            SignCount = 0,
            UserHandleBase64 = Convert.ToBase64String(userId.ToByteArray())
        };

        var handler = new CompleteFido2RegistrationCommandHandler(
            new StubFido2Service(result), repo, new NullUserRepository(),
            NullLogger<CompleteFido2RegistrationCommandHandler>.Instance);

        var outcome = await handler.Handle(
            new CompleteFido2RegistrationCommand(userId, "{}", "YubiKey do Pedro"), CancellationToken.None);

        Assert.That(outcome.IsSuccess, Is.True);
        Assert.That(outcome.Value.KeyId, Is.EqualTo(pending.Id), "o handler reaproveita a linha já gravada");
        Assert.That(pending.Name, Is.EqualTo("YubiKey do Pedro"));
        Assert.That(repo.CreateCalls, Is.EqualTo(0), "não pode duplicar a credencial");
    }

    [Test]
    public async Task CompleteRegistration_CreatesTheKey_WhenTheServiceCouldNotPersistIt()
    {
        var userId = Guid.NewGuid();
        var repo = new InMemoryMfaKeyRepository();

        var result = new Discovery.Core.DTOs.Mfa.Fido2CredentialResult
        {
            Success = true,
            CredentialIdBase64 = Convert.ToBase64String([4, 5, 6]),
            PublicKeyBase64 = "pk",
            SignCount = 0,
            UserHandleBase64 = Convert.ToBase64String(userId.ToByteArray()),
            TransportsBase64Url = ["aW50ZXJuYWw"] // "internal"
        };

        var handler = new CompleteFido2RegistrationCommandHandler(
            new StubFido2Service(result), repo, new NullUserRepository(),
            NullLogger<CompleteFido2RegistrationCommandHandler>.Instance);

        var outcome = await handler.Handle(
            new CompleteFido2RegistrationCommand(userId, "{}", "Passkey do celular"), CancellationToken.None);

        Assert.That(outcome.IsSuccess, Is.True);
        Assert.That(repo.CreateCalls, Is.EqualTo(1), "sem a gravação preventiva o registro não pode se perder");
        Assert.That(repo.LastCreated!.Transports, Is.EqualTo("internal"),
            "os transports devolvidos pelo serviço viram o vocabulário WebAuthn gravado");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Monta um AuthenticatorAssertionRawResponse REAL para uma chave já registrada: usa o
    /// GetAssertionOptions/AssertionOptions.ToJson da própria biblioteca (challenge, rpId e origin
    /// como em produção) e assina com uma chave ECDSA P-256 descartável cuja pública é a que vai
    /// na chave gravada. Assim o teste atravessa o caminho de produção — inclusive a busca da
    /// credencial — em vez de simular o resultado. (O foco do teste é a BUSCA da credencial; a
    /// verificação de assinatura da lib roda antes, então a assinatura precisa ser válida.)
    /// </summary>
    private static class AssertionFixture
    {
        private static readonly ECDsa SigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        /// <summary>
        /// Pública no formato que o banco guarda: uma <b>COSE_Key</b> (CBOR), não SPKI.
        /// A Fido2NetLib reconstrói um CredentialPublicKey a partir desses bytes; passar SPKI
        /// faz o parser explodir com InvalidCastException (CborInteger -> CborMap).
        /// ES256 = {1:2 (kty EC2), 3:-7 (alg ES256), -1:1 (crv P-256), -2:x, -3:y}.
        /// </summary>
        public static string PublicKeyBase64 { get; } = BuildCoseEs256PublicKey();

        private static string BuildCoseEs256PublicKey()
        {
            var p = SigningKey.ExportParameters(false);
            var cose = new CborMap
            {
                { 1, 2 },      // kty: EC2
                { 3, -7 },     // alg: ES256
                { -1, 1 },     // crv: P-256
                { -2, p.Q.X! },
                { -3, p.Q.Y! }
            };
            return Convert.ToBase64String(cose.Encode());
        }

        public static string Build(Fido2AuthService service, Guid userId, UserMfaKey key, byte[] credentialId)
        {
            var credentialIdB64Url = Fido2AuthService.ToBase64Url(credentialId);

            // O BeginAssertionAsync grava o challenge no Redis falso; é dele que sai o challenge.
            var optionsJson = service.BeginAssertionAsync(userId, [key]).GetAwaiter().GetResult();
            Assert.That(optionsJson, Does.Contain(credentialIdB64Url),
                "allowCredentials devolve o id em base64url — o formato que o navegador ecoa em rawId");

            using var doc = JsonDocument.Parse(optionsJson);
            var challenge = doc.RootElement.GetProperty("challenge").GetString()!;

            var clientDataJson = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "webauthn.get",
                challenge,
                origin = "https://localhost:5001", // Authentication:Fido2:Origins:0 do Harness
                crossOrigin = false
            });

            var authData = BuildAuthenticatorData();
            var signature = SigningKey.SignData(
                [.. authData, .. SHA256.HashData(clientDataJson)],
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);

            return JsonSerializer.Serialize(new
            {
                id = credentialIdB64Url,
                rawId = credentialIdB64Url,
                type = "public-key",
                response = new
                {
                    clientDataJSON = Fido2AuthService.ToBase64Url(clientDataJson),
                    authenticatorData = Fido2AuthService.ToBase64Url(authData),
                    signature = Fido2AuthService.ToBase64Url(signature),
                    userHandle = Fido2AuthService.ToBase64Url(Convert.FromBase64String(key.UserHandleBase64!))
                }
            });
        }

        /// <summary>rpIdHash(SHA256 de "localhost") + flags UP|UV + contador de assinatura (big-endian).</summary>
        private static byte[] BuildAuthenticatorData()
        {
            var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes("localhost"));
            var authData = new byte[rpIdHash.Length + 1 + 4];
            rpIdHash.CopyTo(authData, 0);
            authData[rpIdHash.Length] = 0x05; // UP | UV
            authData[^4] = 0; authData[^3] = 0; authData[^2] = 0; authData[^1] = 1;
            return authData;
        }
    }

    private static UserMfaKey NewFido2Key(Guid userId, byte[] credentialId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        KeyType = MfaKeyType.Fido2,
        Name = "Chave",
        IsActive = true,
        CredentialIdBase64 = Fido2AuthService.ToBase64Url(credentialId),
        PublicKeyBase64 = "pk",
        UserHandleBase64 = Convert.ToBase64String(userId.ToByteArray()),
        CreatedAt = DateTime.UtcNow
    };

    private sealed record Harness(Fido2AuthService Service, RecordingRedisService Redis, InMemoryMfaKeyRepository Repo)
    {
        public Harness(int timeoutMs = 120_000, int challengeTtlSeconds = 300)
            : this(
                BuildService(timeoutMs, challengeTtlSeconds, out var redis, out var repo),
                redis!,
                repo!)
        {
        }

        private static Fido2AuthService BuildService(
            int timeoutMs, int challengeTtlSeconds, out RecordingRedisService redis, out InMemoryMfaKeyRepository repo)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Fido2:ServerDomain"] = "localhost",
                    ["Authentication:Fido2:ServerName"] = "Discovery RMM",
                    ["Authentication:Fido2:Origins:0"] = "https://localhost:5001",
                    ["Authentication:Fido2:TimeoutMilliseconds"] = timeoutMs.ToString(),
                    ["Authentication:Fido2:ChallengeTtlSeconds"] = challengeTtlSeconds.ToString()
                })
                .Build();

            redis = new RecordingRedisService();
            repo = new InMemoryMfaKeyRepository();
            return new Fido2AuthService(configuration, redis, repo, NullLogger<Fido2AuthService>.Instance);
        }
    }

    private sealed class RecordingRedisService : IRedisService
    {
        private readonly Dictionary<string, string> _values = new();

        public int? LastExpirySeconds { get; private set; }

        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out var v) ? v : null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(1L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(amount);
        public Task SetAsync(string key, string value, int expirySeconds = 3600)
        {
            _values[key] = value;
            LastExpirySeconds = expirySeconds;
            return Task.CompletedTask;
        }
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(0);
        public Task DeleteAsync(string key) { _values.Remove(key); return Task.CompletedTask; }
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds)
        {
            if (_values.ContainsKey(key)) return Task.FromResult(false);
            _values[key] = value;
            return Task.FromResult(true);
        }
    }

    private sealed class InMemoryMfaKeyRepository : IUserMfaKeyRepository
    {
        private readonly List<UserMfaKey> _keys = [];

        public int CreateCalls { get; private set; }
        public UserMfaKey? LastCreated { get; private set; }

        public void Seed(UserMfaKey key) => _keys.Add(key);

        public Task<IEnumerable<UserMfaKey>> GetActiveByUserIdAsync(Guid userId)
            => Task.FromResult<IEnumerable<UserMfaKey>>(_keys.Where(k => k.UserId == userId && k.IsActive).ToList());

        public Task<UserMfaKey?> GetByIdAsync(Guid id) => Task.FromResult(_keys.FirstOrDefault(k => k.Id == id));

        public Task<UserMfaKey?> GetByCredentialIdAsync(string credentialIdBase64)
            => Task.FromResult(_keys.FirstOrDefault(k => k.IsActive && k.CredentialIdBase64 == credentialIdBase64));

        public Task<UserMfaKey> CreateAsync(UserMfaKey key)
        {
            CreateCalls++;
            LastCreated = key;
            _keys.Add(key);
            return Task.FromResult(key);
        }

        public Task<bool> UpdateSignCountAsync(Guid keyId, uint newSignCount) => Task.FromResult(true);
        public Task<bool> UpdateLastUsedAsync(Guid keyId) => Task.FromResult(true);
        public Task<bool> UpdateFido2KeyAsync(
            Guid keyId, string credentialIdBase64, string publicKeyBase64, uint signCount,
            string? aaguidBase64, string? userHandleBase64, string? transports) => Task.FromResult(true);
        public Task<bool> SetLastUsedStepAsync(Guid keyId, long step) => Task.FromResult(true);
        public Task<bool> UpdateBackupCodeHashesAsync(Guid keyId, string[] hashes) => Task.FromResult(true);
        public Task<bool> DeactivateAsync(Guid keyId, Guid userId) => Task.FromResult(true);
        public Task<int> DeactivateAllByUserIdAsync(Guid userId) => Task.FromResult(0);
        public Task<int> CountActiveByUserIdAsync(Guid userId) => Task.FromResult(0);

        public Task<bool> RenameAsync(Guid keyId, Guid userId, string newName)
        {
            var key = _keys.FirstOrDefault(k => k.Id == keyId && k.UserId == userId);
            if (key is null) return Task.FromResult(false);
            key.Name = newName;
            return Task.FromResult(true);
        }
    }

    private sealed class StubFido2Service(Discovery.Core.DTOs.Mfa.Fido2CredentialResult result) : Discovery.Core.Interfaces.Auth.IFido2Service
    {
        public Task<string> BeginRegistrationAsync(Guid userId, string email, string fullName, IEnumerable<string> existingCredentialIds) => throw new NotSupportedException();
        public Task<Discovery.Core.DTOs.Mfa.Fido2CredentialResult> CompleteRegistrationAsync(Guid userId, string attestationResponseJson)
            => Task.FromResult(result);
        public Task<string> BeginAssertionAsync(Guid userId, IEnumerable<UserMfaKey> activeKeys) => throw new NotSupportedException();
        public Task<Discovery.Core.DTOs.Mfa.Fido2AssertionResult> CompleteAssertionAsync(Guid userId, string assertionResponseJson, IEnumerable<UserMfaKey> activeKeys) => throw new NotSupportedException();
        public void LogClientError(Guid? userId, string? errorName, string? errorMessage, string? stage, string? userAgent) { }
    }

    private sealed class NullUserRepository : Discovery.Core.Interfaces.Identity.IUserRepository
    {
        public Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured) => Task.FromResult(true);
        public Task<Discovery.Core.Entities.Identity.User?> GetByIdAsync(Guid id) => Task.FromResult<Discovery.Core.Entities.Identity.User?>(null);
        public Task<IReadOnlyList<Discovery.Core.Entities.Identity.User>> GetByIdsAsync(IEnumerable<Guid> ids) => Task.FromResult<IReadOnlyList<Discovery.Core.Entities.Identity.User>>([]);
        public Task<Discovery.Core.Entities.Identity.User?> GetByLoginAsync(string login) => Task.FromResult<Discovery.Core.Entities.Identity.User?>(null);
        public Task<Discovery.Core.Entities.Identity.User?> GetByEmailAsync(string email) => Task.FromResult<Discovery.Core.Entities.Identity.User?>(null);
        public Task<Discovery.Core.Entities.Identity.User?> GetByLoginOrEmailAsync(string loginOrEmail) => Task.FromResult<Discovery.Core.Entities.Identity.User?>(null);
        public Task<IReadOnlyList<Discovery.Core.Entities.Identity.User>> GetAllPageAsync(string? cursor, int take = 50) => Task.FromResult<IReadOnlyList<Discovery.Core.Entities.Identity.User>>([]);
        public Task<Discovery.Core.Entities.Identity.User> CreateAsync(Discovery.Core.Entities.Identity.User u) => Task.FromResult(u);
        public Task<Discovery.Core.Entities.Identity.User> UpdateAsync(Discovery.Core.Entities.Identity.User u) => Task.FromResult(u);
        public Task<bool> SetMfaFailureStateAsync(Guid userId, int failedAttempts, DateTime? lockoutUntil) => Task.FromResult(true);
        public Task<bool> ResetMfaFailureStateAsync(Guid userId) => Task.FromResult(true);
        public Task<bool> SetLastLoginAsync(Guid userId, DateTime at) => Task.FromResult(true);
        public Task<bool> ExistsByLoginAsync(string login) => Task.FromResult(false);
        public Task<bool> ExistsByEmailAsync(string email) => Task.FromResult(false);
        public Task<int> CountAsync() => Task.FromResult(0);
    }
}
