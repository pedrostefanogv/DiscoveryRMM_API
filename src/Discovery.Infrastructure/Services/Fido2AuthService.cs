using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Discovery.Core.DTOs.Mfa;
using Discovery.Core.Entities.Security;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço FIDO2 / WebAuthn usando a biblioteca Fido2NetLib 4.x.
/// Armazena challenges temporários no Redis (TTL 3 minutos).
///
/// Correções aplicadas após a investigação de falha de passkey no Android
/// (RELATORIO_MFA_FIDO2_ANDROID_2026-11-01.md, itens 1, 3, 4 e 5):
/// <list type="bullet">
///   <item>timeout do challenge configurável (era o default de 60 s da lib) e TTL do
///   challenge alinhado a ele;</item>
///   <item>transports da credencial persistidos e devolvidos em allowCredentials;</item>
///   <item>callbacks de segurança consultando o banco em vez de responder <c>true</c> fixo;</item>
///   <item>telemetria de falha do cliente WebAuthn (nome do DOMException).</item>
/// </list>
/// </summary>
public class Fido2AuthService : IFido2Service
{
    /// <summary>
    /// Nome dado à credencial quando o registro é confirmado pelo cliente WebAuthn.
    /// O nome definitivo é enviado em seguida por <c>CompleteFido2RegistrationCommand</c>.
    /// </summary>
    public const string PendingCredentialName = "Credencial em configuração";

    /// <summary>Contexto logado quando a falha vem do cliente (navegador), não do servidor.</summary>
    public const string ClientErrorContext = "client-webauthn";

    /// <summary>
    /// TTL (segundos) do challenge no Redis. Precisa ser maior que o timeout enviado ao
    /// cliente: o fluxo híbrido (QR + BLE) do Android pode levar mais de um minuto, e antes
    /// o challenge expirava no Redis enquanto o celular ainda estava concluindo — o usuário
    /// via o erro genérico de "reinicie o processo no outro dispositivo".
    /// </summary>
    private const int DefaultChallengeTtlSeconds = 300;

    /// <summary>Timeout padrão do challenge (ms) enviado ao cliente WebAuthn.</summary>
    private const int DefaultTimeoutMilliseconds = 120_000;

    /// <summary>
    /// Teto do TTL do challenge. O challenge é a peça anti-replay desta cerimônia; mantê-lo
    /// vivo por muito tempo amplia a janela de ataque, então o valor é limitado mesmo que a
    /// configuração traga algo maior.
    /// </summary>
    private const int MaxChallengeTtlSeconds = 900;

    private const string RegChallengePrefix = "fido2:reg:";
    private const string AssertChallengePrefix = "fido2:assert:";

    private readonly Fido2 _fido2;
    private readonly IRedisService _redis;
    private readonly IUserMfaKeyRepository _mfaKeyRepo;
    private readonly ILogger<Fido2AuthService> _logger;
    private readonly int _challengeTtlSeconds;

    public Fido2AuthService(
        IConfiguration configuration,
        IRedisService redis,
        IUserMfaKeyRepository mfaKeyRepo,
        ILogger<Fido2AuthService> logger)
    {
        _redis = redis;
        _mfaKeyRepo = mfaKeyRepo;
        _logger = logger;

        var section = configuration.GetSection("Authentication:Fido2");
        var serverDomain = section.GetValue<string>("ServerDomain", "localhost")!;
        var serverName = section.GetValue<string>("ServerName", "Discovery RMM")!;
        var originsConfig = section.GetSection("Origins").Get<string[]>()
                            ?? ["https://localhost:5001", "http://localhost:5001"];

        var timeoutMs = section.GetValue<int>("TimeoutMilliseconds", DefaultTimeoutMilliseconds);
        if (timeoutMs <= 0)
        {
            _logger.LogWarning(
                "Authentication:Fido2:TimeoutMilliseconds inválido ({TimeoutMs}); usando o padrão de {Default} ms.",
                timeoutMs, DefaultTimeoutMilliseconds);
            timeoutMs = DefaultTimeoutMilliseconds;
        }

        // O TTL do challenge tem de cobrir o timeout anunciado ao cliente, com folga para a
        // requisição de complete chegar ao servidor.
        var configuredTtl = section.GetValue<int>("ChallengeTtlSeconds", DefaultChallengeTtlSeconds);
        _challengeTtlSeconds = ComputeChallengeTtlSeconds(configuredTtl, timeoutMs);

        _fido2 = new Fido2(new Fido2Configuration
        {
            // RPID e RPName substituem ServerDomain/ServerName (obsoletos no Fido2 4.1+;
            // serão removidos na próxima major)
            RPID = serverDomain,
            RPName = serverName,
            Origins = new HashSet<string>(originsConfig),
            TimestampDriftTolerance = 300000,
            // Sem isto a lib anuncia 60 s ao autenticador — curto demais para o fluxo
            // híbrido (QR/caBLE) do Android, que é justamente o que falhava.
            Timeout = (uint)timeoutMs
        });

        _logger.LogInformation(
            "FIDO2 configurado: rpId={RpId}, timeout={TimeoutMs} ms, challengeTtl={TtlSeconds} s, origins={OriginCount}.",
            serverDomain, timeoutMs, _challengeTtlSeconds, originsConfig.Length);
    }

    /// <summary>
    /// TTL efetivo do challenge: pelo menos o timeout do cliente + 60 s de folga,
    /// limitado por <see cref="MaxChallengeTtlSeconds"/> e nunca abaixo de 180 s
    /// (o mínimo recomendado no relatório do Android).
    /// </summary>
    internal static int ComputeChallengeTtlSeconds(int configuredTtlSeconds, int timeoutMs)
    {
        var minimumForTimeout = (int)Math.Ceiling(timeoutMs / 1000d) + 60;
        var ttl = Math.Max(configuredTtlSeconds, minimumForTimeout);
        ttl = Math.Max(ttl, 180);
        return Math.Min(ttl, MaxChallengeTtlSeconds);
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
        await _redis.SetAsync(RegChallengePrefix + userId, optionsJson, _challengeTtlSeconds);
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
            // Antes: (_, _) => Task.FromResult(true) — aceitava qualquer credential id,
            // inclusive um já registrado por outro usuário. Agora consulta o banco e
            // registra o motivo da recusa (ver IsCredentialIdUniqueToUser).
            IsCredentialIdUniqueToUserCallback = (params_, ct) =>
                IsCredentialIdUniqueToUser(userId, params_, ct)
        });

        var transports = NormalizeTransports(attestationResponse.Response?.Transports);

        // Ver o comentário de PendingCredentialName: a chave é criada agora (com o mesmo nome
        // temporário já usado pelo handler) para que a checagem de unicidade de credential id
        // no LOGIN encontre esta credencial. Sem isso o login FIDO2 passaria a ser recusado
        // como "credencial não reconhecida" para toda chave nova.
        await PersistPendingKeyAsync(userId, result, transports);

        await _redis.DeleteAsync(RegChallengePrefix + userId);

        return new Fido2CredentialResult
        {
            Success = true,
            CredentialIdBase64 = Convert.ToBase64String(result.Id),
            PublicKeyBase64 = Convert.ToBase64String(result.PublicKey),
            SignCount = result.SignCount,
            AaguidBase64 = Convert.ToBase64String(result.AaGuid.ToByteArray()),
            UserHandleBase64 = Convert.ToBase64String(userId.ToByteArray()),
            // Base64url: é o formato que o navegador devolve em getTransports()/getClientExtensionResults().
            TransportsBase64Url = transports
                .Select(t => ToBase64Url(System.Text.Encoding.UTF8.GetBytes(t)))
                .ToList()
        };
    }

    public async Task<string> BeginAssertionAsync(Guid userId, IEnumerable<UserMfaKey> activeKeys)
    {
        var allowedCredentials = activeKeys
            .Where(k => k.CredentialIdBase64 != null)
            .Select(BuildCredentialDescriptor)
            .ToList();

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCredentials,
            UserVerification = UserVerificationRequirement.Required
        });

        var optionsJson = options.ToJson();
        await _redis.SetAsync(AssertChallengePrefix + userId, optionsJson, _challengeTtlSeconds);
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

        // Encontra a chave correspondente pelo CredentialId.
        // A comparação é por BYTES (CredentialIdMatches), nunca por string: o banco guarda
        // base64url sem padding (ver PersistPendingKeyAsync) enquanto aqui a forma natural é
        // Convert.ToBase64String (base64 padrão, COM '='). Comparar as strings cruas fazia
        // TODA asserção falhar com "Credencial não reconhecida." (regressão 6188cd8).
        var credIdBase64 = Convert.ToBase64String(assertionResponse.RawId);
        var matchingKey = activeKeys
            .FirstOrDefault(k => CredentialIdMatches(k.CredentialIdBase64, credIdBase64))
            ?? throw new InvalidOperationException("Credencial não reconhecida.");

        var publicKey = Convert.FromBase64String(matchingKey.PublicKeyBase64!);

        var result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = assertionResponse,
            OriginalOptions = options,
            StoredPublicKey = publicKey,
            StoredSignatureCounter = matchingKey.SignCount,
            // Antes: (_, _) => Task.FromResult(true). Agora confirma que o userHandle
            // apresentado pertence a quem registrou a credencial.
            IsUserHandleOwnerOfCredentialIdCallback = (params_, ct) =>
                IsUserHandleOwnerOfCredentialId(userId, params_, ct)
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

    /// <summary>
    /// Registra, sem propagar erro, a falha do cliente WebAuthn (DOMException etc.).
    /// O relatório do Android mostrou que a maioria das falhas acontece no navegador ANTES
    /// de chegar ao servidor: sem este canal, o caso "desaparece" dos logs e vira suporte
    /// às cegas. Nada aqui conta como tentativa de MFA nem alimenta o lockout da conta.
    /// </summary>
    public void LogClientError(Guid? userId, string? errorName, string? errorMessage, string? stage, string? userAgent)
    {
        var safeUserId = userId?.ToString() ?? "(anônimo)";
        var safeName = Truncate(errorName, 64) ?? "(sem nome)";
        var safeStage = Truncate(stage, 64) ?? "(etapa não informada)";
        var safeBrowserMessage = Truncate(errorMessage, 512);
        var safeUserAgent = Truncate(userAgent, 256);

        var known = DescribeKnownClientError(safeName);

        _logger.LogWarning(
            "Falha WebAuthn no cliente (context={Context}): user={UserId} etapa={Stage} DOMException={ErrorName} " +
            "significado={Meaning} mensagemDoNavegador={BrowserMessage} userAgent={UserAgent}.",
            ClientErrorContext, safeUserId, safeStage, safeName,
            known ?? "desconhecido/não catalogado", safeBrowserMessage ?? "(vazia)", safeUserAgent ?? "(vazio)");

        if (safeName == "NotAllowedError")
        {
            _logger.LogWarning(
                "WebAuthn NotAllowedError (context={Context}): o cliente cancelou, deixou expirar ou teve o " +
                "autenticador bloqueado. No fluxo híbrido (QR/BLE) no Android isto costuma indicar celular fora " +
                "de alcance da rede/Bluetooth, VPN no desktop ou demora acima do timeout do challenge. user={UserId}.",
                ClientErrorContext, safeUserId);
        }
    }

    private static string? DescribeKnownClientError(string errorName) => errorName switch
    {
        "NotAllowedError" => "cancelado, expirado ou bloqueado pelo navegador/autenticador (sem toque na chave, timeout ou fluxo híbrido interrompido)",
        "SecurityError" => "rpId/origem incompatíveis ou página fora de contexto seguro",
        "AbortError" => "operação interrompida antes de concluir",
        "InvalidStateError" => "credencial já registrada ou removida do autenticador",
        "NotSupportedError" => "parâmetros WebAuthn não suportados pelo autenticador/navegador",
        "ConstraintError" => "autenticador não atendeu aos requisitos pedidos",
        "UnknownError" => "falha não especificada do autenticador",
        _ => null
    };

    // ─── Callbacks de segurança ──────────────────────────────────────────────

    /// <summary>
    /// Um credential id só pode pertencer a UM usuário. A identidade é o par
    /// (credential_id_base64, user_id) e as duas colunas são guardadas em base64url (o
    /// formato que o navegador usa); registros legados ficam em base64 padrão — a consulta
    /// considera as duas formas, senão credenciais antigas passariam a ser recusadas.
    ///
    /// Risco conhecido e aceito: durante uma troca de dispositivo (fluxo híbrido
    /// caBLE/QR) a credencial pode chegar ao servidor antes do <c>register/complete</c> do
    /// desktop. Nesse caso a chave ainda não está no banco e as duas requisições concorrentes
    /// se recusam mutuamente — troque por um backfill (que aposenta as credenciais de login
    /// antigas) para eliminar essa corrida.
    /// </summary>
    private async Task<bool> IsCredentialIdUniqueToUser(
        Guid userId,
        IsCredentialIdUniqueToUserParams credentialIdUserParams,
        CancellationToken cancellationToken)
    {
        var credentialId = credentialIdUserParams?.CredentialId;
        if (credentialId is null || credentialId.Length == 0)
        {
            _logger.LogWarning(
                "Registro FIDO2 recusado — attestation sem credential id (context={Context}, user={UserId}).",
                ClientErrorContext, userId);
            return false;
        }

        var b64Url = ToBase64Url(credentialId);
        var b64Std = Convert.ToBase64String(credentialId);
        var browserUserHandle = credentialIdUserParams!.User?.Id;
        var browserUserId = browserUserHandle is { Length: > 0 }
            ? new Guid(browserUserHandle)
            : (Guid?)null;

        _logger.LogDebug(
            "Verificando unicidade do credential id (user={UserId}, credentialId={CredentialId}, userHandleDoNavegador={BrowserUserId}).",
            userId, b64Url, browserUserId?.ToString() ?? "(ausente)");

        // O userHandle anunciado pelo navegador tem de ser o próprio usuário do token: se
        // divergir, o cliente está tentando registrar a credencial na conta errada (ou o
        // browser repetiu/expos um userHandle antigo) — recusa explícita, com o motivo no log.
        if (browserUserId is { } announcedUserId && announcedUserId != userId)
        {
            _logger.LogWarning(
                "Registro FIDO2 RECUSADO — o userHandle apresentado pelo navegador ({BrowserUserId}) não é o usuário autenticado ({UserId}). " +
                "Credencial {CredentialId} não será gravada.",
                announcedUserId, userId, b64Url);
            return false;
        }

        var owner = await FindOwnerByCredentialIdAsync(b64Url, b64Std, cancellationToken);
        if (owner is null)
        {
            _logger.LogInformation(
                "Credential id {CredentialId} não existe no banco — unicidade confirmada para o usuário {UserId}.",
                b64Url, userId);
            return true;
        }

        if (owner.UserId == userId)
        {
            _logger.LogInformation(
                "Credential id {CredentialId} já pertence ao próprio usuário {UserId} (key {KeyId}, ativa={IsActive}) — " +
                "reregistro idempotente permitido.",
                b64Url, userId, owner.Id, owner.IsActive);
            return true;
        }

        _logger.LogError(
            "Registro FIDO2 RECUSADO — credential id {CredentialId} já está registrado para OUTRO usuário " +
            "(dono={OwnerUserId}, key={KeyId}, criadaEm={CreatedAt:u}). Usuário que tentou registrar={UserId}. " +
            "Indício de clonagem/migração indevida de autenticador; a credencial NÃO foi gravada.",
            b64Url, owner.UserId, owner.Id, owner.CreatedAt, userId);
        return false;
    }

    /// <summary>
    /// O userHandle apresentado na assertion tem de pertencer a quem registrou a credencial.
    /// Credencial gravada por um fluxo antigo (ou por outro usuário) é recusada com o motivo
    /// explícito no log — antes a checagem era <c>true</c> fixo.
    /// </summary>
    private async Task<bool> IsUserHandleOwnerOfCredentialId(
        Guid userId,
        IsUserHandleOwnerOfCredentialIdParams credentialIdUserHandleParams,
        CancellationToken cancellationToken)
    {
        var credentialId = credentialIdUserHandleParams?.CredentialId;
        var userHandle = credentialIdUserHandleParams?.UserHandle;

        if (credentialId is null || credentialId.Length == 0)
        {
            _logger.LogWarning(
                "Assertion FIDO2 recusada — sem credential id (context={Context}, user={UserId}).",
                ClientErrorContext, userId);
            return false;
        }

        var b64Url = ToBase64Url(credentialId);
        var b64Std = Convert.ToBase64String(credentialId);

        var owner = await FindOwnerByCredentialIdAsync(b64Url, b64Std, cancellationToken);
        if (owner is null)
        {
            _logger.LogError(
                "Assertion FIDO2 RECUSADA — credential id {CredentialId} não encontrado no banco para o usuário {UserId} " +
                "(userHandlePresente={HasHandle}). Pode ser credencial de outro usuário, revogada ou challenge de outra conta.",
                b64Url, userId, userHandle is { Length: > 0 });
            return false;
        }

        if (owner.UserId != userId)
        {
            _logger.LogError(
                "Assertion FIDO2 RECUSADA — a credencial {CredentialId} pertence ao usuário {OwnerUserId}, " +
                "mas a autenticação foi pedida para {UserId} (key {KeyId}). Tentativa de usar a credencial de outra conta.",
                b64Url, owner.UserId, userId, owner.Id);
            return false;
        }

        // Handle ausente não é recusa: credenciais de plataforma podem não devolver userHandle.
        if (userHandle is null || userHandle.Length == 0)
        {
            _logger.LogDebug(
                "Assertion FIDO2 sem userHandle para {CredentialId}; vínculo confirmado pelo banco (user={UserId}).",
                b64Url, userId);
            return true;
        }

        // Handle do banco ausente = credencial gravada por fluxo antigo. O dono já foi
        // confirmado pela coluna user_id, então concedemos e registramos o caso para que a
        // lacuna apareça no log em vez de virar um bloqueio silencioso de login.
        if (string.IsNullOrWhiteSpace(owner.UserHandleBase64))
        {
            _logger.LogWarning(
                "Assertion FIDO2 aceita com ressalva — a credencial {CredentialId} (key {KeyId}, user {UserId}) não tem " +
                "userHandle gravado no banco (registro legado). Dono confirmado por user_id; avalie regravar a credencial.",
                b64Url, owner.Id, userId);
            return true;
        }

        // O banco pode ter o handle em base64 padrão (formato antigo) ou base64url (o que o
        // navegador envia hoje) — comparamos pelos BYTES para não recusar credencial válida.
        var expected = DecodeBase64AnyFormat(owner.UserHandleBase64);
        var matches = expected is not null && expected.AsSpan().SequenceEqual(userHandle);

        if (!matches)
        {
            _logger.LogError(
                "Assertion FIDO2 RECUSADA — o userHandle apresentado não é o dono da credencial {CredentialId} " +
                "(key {KeyId}, user {UserId}). userHandle recebido={Presented} / userHandle gravado={Expected}. " +
                "Se o log se repetir para o mesmo usuário, a credencial está inconsistente e deve ser registrada novamente.",
                b64Url, owner.Id, userId, ToBase64Url(userHandle), owner.UserHandleBase64);
            return false;
        }

        _logger.LogDebug(
            "Assertion FIDO2: userHandle confere com o dono da credencial {CredentialId} (user={UserId}).",
            b64Url, userId);
        return true;
    }

    /// <summary>
    /// Busca o dono da credencial pelas duas codificações em uso: a consulta vai ao banco
    /// (e não à lista em memória do chamador) porque <c>activeKeys</c> é montada antes do
    /// registro e não contém a credencial recém-criada.
    /// </summary>
    private async Task<UserMfaKey?> FindOwnerByCredentialIdAsync(
        string credentialIdBase64Url,
        string credentialIdBase64Standard,
        CancellationToken cancellationToken)
    {
        // O repositório recebe o credential id — nunca o userHandle/segredo.
        foreach (var candidate in new[] { credentialIdBase64Url, credentialIdBase64Standard })
        {
            cancellationToken.ThrowIfCancellationRequested();

            var found = await _mfaKeyRepo.GetByCredentialIdAsync(candidate);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Grava a credencial recém-validada com nome temporário (ver <see cref="PendingCredentialName"/>).
    /// Se o credential id já existir para o mesmo usuário, atualiza os campos FIDO2 em vez de
    /// duplicar a linha. Falha de escrita não derruba o registro: apenas é logada.
    /// </summary>
    private async Task PersistPendingKeyAsync(Guid userId, RegisteredPublicKeyCredential result, IReadOnlyList<string> transports)
    {
        try
        {
            var credentialIdBase64Url = ToBase64Url(result.Id);
            var credentialIdBase64Standard = Convert.ToBase64String(result.Id);

            var existing = await FindOwnerByCredentialIdAsync(
                credentialIdBase64Url, credentialIdBase64Standard, CancellationToken.None);

            var transportsStorage = transports.Count == 0 ? null : string.Join(",", transports);
            var userHandleBase64 = Convert.ToBase64String(userId.ToByteArray());

            if (existing is not null)
            {
                if (existing.UserId != userId)
                {
                    // A callback de unicidade já recusaria este caso; aqui é só cinto de segurança.
                    _logger.LogError(
                        "Credencial {CredentialId} validada mas pertence ao usuário {OwnerUserId}; usuário {UserId} não será sobrescrito.",
                        credentialIdBase64Url, existing.UserId, userId);
                    return;
                }

                var updated = await _mfaKeyRepo.UpdateFido2KeyAsync(
                    existing.Id,
                    credentialIdBase64Url,
                    Convert.ToBase64String(result.PublicKey),
                    result.SignCount,
                    Convert.ToBase64String(result.AaGuid.ToByteArray()),
                    userHandleBase64,
                    transportsStorage);

                _logger.LogInformation(
                    "Credencial FIDO2 {CredentialId} já existia para o usuário {UserId} (key {KeyId}); dados atualizados={Updated}.",
                    credentialIdBase64Url, userId, existing.Id, updated);
                return;
            }

            var created = await _mfaKeyRepo.CreateAsync(new UserMfaKey
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                KeyType = Core.Enums.Security.MfaKeyType.Fido2,
                Name = PendingCredentialName,
                IsActive = true,
                // Guardado em base64url — é o formato que o navegador usa em RawId e o que a
                // assertion devolve; o handler troca o nome e mantém o id.
                CredentialIdBase64 = credentialIdBase64Url,
                PublicKeyBase64 = Convert.ToBase64String(result.PublicKey),
                SignCount = result.SignCount,
                AaguidBase64 = Convert.ToBase64String(result.AaGuid.ToByteArray()),
                UserHandleBase64 = userHandleBase64,
                Transports = transportsStorage,
                CreatedAt = DateTime.UtcNow
            });

            _logger.LogInformation(
                "Credencial FIDO2 {CredentialId} criada com nome temporário para o usuário {UserId} (key {KeyId}, transports={Transports}).",
                credentialIdBase64Url, userId, created.Id, transportsStorage ?? "(nenhum)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Falha ao gravar preventivamente a credencial FIDO2 do usuário {UserId}; o registro segue e será concluído pelo handler.",
                userId);
        }
    }

    // ─── Helpers de credencial ───────────────────────────────────────────────

    /// <summary>
    /// Monta o descritor de allowCredentials. Antes só iam <c>type</c> e <c>id</c>: sem
    /// <c>transports</c> o Android não recebe a dica de que a credencial é
    /// <c>internal</c>/<c>hybrid</c>, o que piora a descoberta no fluxo híbrido.
    /// </summary>
    private PublicKeyCredentialDescriptor BuildCredentialDescriptor(UserMfaKey key)
    {
        var idBytes = DecodeBase64AnyFormat(key.CredentialIdBase64!);

        if (idBytes is null)
        {
            _logger.LogWarning(
                "Credencial {KeyId} do usuário {UserId} tem credential id inválido ({Raw}); será enviada sem id e o autenticador vai ignorá-la.",
                key.Id, key.UserId, key.CredentialIdBase64);
            return new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, [], []);
        }

        var transports = ParseTransports(key.Transports);
        if (transports.Length == 0)
        {
            _logger.LogDebug(
                "Credencial {KeyId} do usuário {UserId} sem transports persistidos (registro anterior à correção).",
                key.Id, key.UserId);
        }

        return new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, idBytes, transports);
    }

    /// <summary>Lê os transports gravados (separados por vírgula, nomes WebAuthn em minúsculas).</summary>
    internal static AuthenticatorTransport[] ParseTransports(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return [];

        var parsed = new List<AuthenticatorTransport>();
        foreach (var raw in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<AuthenticatorTransport>(raw, ignoreCase: true, out var transport) && !parsed.Contains(transport))
                parsed.Add(transport);
            // Valor desconhecido é ignorado de propósito: o navegador simplesmente não recebe
            // a dica daquele transporte, e a credencial continua utilizável.
        }

        return parsed.ToArray();
    }

    /// <summary>Normaliza para o vocabulário WebAuthn em minúsculas ("internal", "hybrid", "usb"...).</summary>
    internal static IReadOnlyList<string> NormalizeTransports(AuthenticatorTransport[]? transports)
        => transports is null || transports.Length == 0
            ? []
            : transports.Select(t => t.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Decodifica base64url OU base64 padrão. As credenciais antigas foram gravadas em base64
    /// padrão; as novas, em base64url (formato do navegador). Sem aceitar as duas formas, a
    /// comparação de allowCredentials e a busca no banco falhariam para metade das chaves.
    /// </summary>
    internal static byte[]? DecodeBase64AnyFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var trimmed = value.Trim();
            if (trimmed.Contains('-') || trimmed.Contains('_'))
                trimmed = trimmed.Replace('-', '+').Replace('_', '/');

            // FromBase64String exige o padding; o cliente base64url o remove.
            var remainder = trimmed.Length % 4;
            if (remainder == 2) trimmed += "==";
            else if (remainder == 3) trimmed += "=";
            else if (remainder == 1) return null;

            return Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Compara dois credential ids pelos BYTES decodificados, aceitando base64url (novo) ou base64
    /// padrão (legado) dos dois lados. É a única forma correta de comparar: os mesmos bytes têm
    /// representações textuais diferentes ('=' de padding, '+'/'/' vs '-'/'_'), então igualdade de
    /// string recusa credenciais válidas. Ver a regressão 6188cd8: o login FIDO2 quebrava inteiro.
    /// </summary>
    internal static bool CredentialIdMatches(string? storedValue, string? returnedValue)
    {
        var stored = DecodeBase64AnyFormat(storedValue);
        var returned = DecodeBase64AnyFormat(returnedValue);
        return stored is not null && returned is not null && stored.AsSpan().SequenceEqual(returned);
    }

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : (value.Length <= maxLength ? value : value[..maxLength]);
}
