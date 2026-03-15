using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Meduza.Core.DTOs.Mfa;
using Meduza.Core.Entities.Security;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Auth;
using Microsoft.Extensions.Configuration;

namespace Meduza.Infrastructure.Services;

/// <summary>
/// Serviço FIDO2 / WebAuthn usando a biblioteca Fido2NetLib 4.x.
/// Armazena challenges temporários no Redis (TTL 2 minutos).
/// </summary>
public class Fido2AuthService : IFido2Service
{
    private readonly Fido2 _fido2;
    private readonly IRedisService _redis;

    private const int ChallengeTtlSeconds = 120;
    private const string RegChallengePrefix = "fido2:reg:";
    private const string AssertChallengePrefix = "fido2:assert:";

    public Fido2AuthService(IConfiguration configuration, IRedisService redis)
    {
        _redis = redis;

        var section = configuration.GetSection("Authentication:Fido2");
        var serverDomain = section.GetValue<string>("ServerDomain", "localhost")!;
        var serverName = section.GetValue<string>("ServerName", "Meduza RMM")!;
        var originsConfig = section.GetSection("Origins").Get<string[]>()
                            ?? ["https://localhost:5001", "http://localhost:5001"];

        _fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = serverDomain,
            ServerName = serverName,
            Origins = new HashSet<string>(originsConfig),
            TimestampDriftTolerance = 300000
        });
    }

    public async Task<string> BeginRegistrationAsync(
        Guid userId,
        string email,
        string fullName,
        IEnumerable<string> existingCredentialIds)
    {
        var fido2User = new Fido2User
        {
            Id = userId.ToByteArray(),
            Name = email,
            DisplayName = fullName
        };

        var excludeCredentials = existingCredentialIds
            .Select(id => new PublicKeyCredentialDescriptor(Convert.FromBase64String(id)))
            .ToList();

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = fido2User,
            ExcludeCredentials = excludeCredentials,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                UserVerification = UserVerificationRequirement.Required,
                ResidentKey = ResidentKeyRequirement.Preferred
            },
            AttestationPreference = AttestationConveyancePreference.None
        });

        var optionsJson = options.ToJson();
        await _redis.SetAsync(RegChallengePrefix + userId, optionsJson, ChallengeTtlSeconds);
        return optionsJson;
    }

    public async Task<Fido2CredentialResult> CompleteRegistrationAsync(
        Guid userId,
        string attestationResponseJson)
    {
        var storedOptionsJson = await _redis.GetAsync(RegChallengePrefix + userId);
        if (storedOptionsJson is null)
            throw new InvalidOperationException("Challenge expirado ou não iniciado. Inicie o registro novamente.");

        var options = CredentialCreateOptions.FromJson(storedOptionsJson);
        var attestationResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
            attestationResponseJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Resposta de attestation inválida.");

        var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = attestationResponse,
            OriginalOptions = options,
            IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true)
        });

        await _redis.DeleteAsync(RegChallengePrefix + userId);

        return new Fido2CredentialResult
        {
            Success = true,
            CredentialIdBase64 = Convert.ToBase64String(result.Id),
            PublicKeyBase64 = Convert.ToBase64String(result.PublicKey),
            SignCount = result.SignCount,
            AaguidBase64 = Convert.ToBase64String(result.AaGuid.ToByteArray()),
            UserHandleBase64 = Convert.ToBase64String(userId.ToByteArray())
        };
    }

    public async Task<string> BeginAssertionAsync(Guid userId, IEnumerable<UserMfaKey> activeKeys)
    {
        var allowedCredentials = activeKeys
            .Where(k => k.CredentialIdBase64 != null)
            .Select(k => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, Convert.FromBase64String(k.CredentialIdBase64!)))
            .ToList();

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCredentials,
            UserVerification = UserVerificationRequirement.Required
        });

        var optionsJson = options.ToJson();
        await _redis.SetAsync(AssertChallengePrefix + userId, optionsJson, ChallengeTtlSeconds);
        return optionsJson;
    }

    public async Task<Fido2AssertionResult> CompleteAssertionAsync(
        Guid userId,
        string assertionResponseJson,
        IEnumerable<UserMfaKey> activeKeys)
    {
        var storedOptionsJson = await _redis.GetAsync(AssertChallengePrefix + userId);
        if (storedOptionsJson is null)
            throw new InvalidOperationException("Challenge expirado ou não iniciado. Inicie a autenticação novamente.");

        var options = AssertionOptions.FromJson(storedOptionsJson);

        var assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
            assertionResponseJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Resposta de assertion inválida.");

        // Encontra a chave correspondente pelo CredentialId
        var credIdBase64 = Convert.ToBase64String(assertionResponse.RawId);
        var matchingKey = activeKeys
            .FirstOrDefault(k => k.CredentialIdBase64 == credIdBase64)
            ?? throw new InvalidOperationException("Credencial não reconhecida.");

        var publicKey = Convert.FromBase64String(matchingKey.PublicKeyBase64!);

        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = assertionResponse,
            OriginalOptions = options,
            StoredPublicKey = publicKey,
            StoredSignatureCounter = matchingKey.SignCount,
            IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true)
        });

        await _redis.DeleteAsync(AssertChallengePrefix + userId);

        return new Fido2AssertionResult
        {
            Success = true,
            KeyId = matchingKey.Id,
            CredentialIdBase64 = credIdBase64,
            NewSignCount = result.SignCount
        };
    }
}
