using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Jwt;
using NATS.Jwt.Models;
using NATS.NKeys;

namespace Discovery.Api.Services;

public class NatsAuthCalloutBackgroundService : BackgroundService
{
    private const string ServerXKeyHeader = "Nats-Server-Xkey";
    private static readonly TimeSpan JwtClockSkew = TimeSpan.FromSeconds(30);

    private readonly NatsConnection _natsConnection;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INatsAuthCalloutReloadSignal _reloadSignal;
    private readonly ILogger<NatsAuthCalloutBackgroundService> _logger;

    // Limita a concorrencia do processamento de auth (flood de requests nao
    // pode derrubar o banco): alto o bastante para nunca enfileirar viewers
    // legitimos (a fila era o bug), baixo o bastante para proteger o banco.
    private static readonly SemaphoreSlim AuthProcessingGate = new(16, 16);

    public NatsAuthCalloutBackgroundService(
        NatsConnection natsConnection,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        INatsAuthCalloutReloadSignal reloadSignal,
        ILogger<NatsAuthCalloutBackgroundService> logger)
    {
        _natsConnection = natsConnection;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _reloadSignal = reloadSignal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var appsettingsEnabled = _configuration.GetValue<bool?>("Nats:AuthCallout:Enabled") ?? false;
        if (!appsettingsEnabled)
        {
            _logger.LogInformation("NATS auth callout service is disabled (Nats:AuthCallout:Enabled = false).");
            return;
        }

        // Loop de reload: reinicia a assinatura quando configurações mudam (sem reiniciar a API).
        while (!stoppingToken.IsCancellationRequested)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _reloadSignal.Token);
            var loopToken = linkedCts.Token;

            try
            {
                await RunSubscriptionAsync(loopToken);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("NATS auth callout service reloading due to configuration change.");
                await Task.Delay(500, stoppingToken); // pequena pausa antes de reconectar
            }
        }
    }

    private async Task RunSubscriptionAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        var serverConfig = await configurationService.GetServerConfigAsync();

        if (!serverConfig.NatsEnabled)
        {
            _logger.LogInformation("NATS auth callout service aguardando — NatsEnabled = false.");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }

        if (!serverConfig.NatsAuthEnabled)
        {
            _logger.LogInformation("NATS auth callout service aguardando — NatsAuthEnabled = false.");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(_configuration["Nats:AccountSeed"]))
        {
            _logger.LogWarning("NATS auth callout service aguardando — Nats:AccountSeed não configurado.");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }

        var subject = _configuration.GetValue<string>("Nats:AuthCallout:Subject") ?? "$SYS.REQ.USER.AUTH";
        _logger.LogInformation("NATS auth callout listening on {Subject}.", subject);

        // Sempre subscreve como byte[] para suportar xkey (payload encriptado) e modo texto.
        // CONCORRENTE (nao sequencial): cada autenticacao e processada em task
        // propria. O processamento sequencial criava FILA quando multiplas
        // conexoes autenticam juntas (liveness + terminal do mesmo viewer, cada
        // uma com queries ao banco) — a segunda passava do authorization.timeout
        // (30s) do servidor e recebia "Authentication Timeout" generico com o
        // terminal em tela branca (incidente 2026-10-27).
        await foreach (var msg in _natsConnection.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
        {
            _ = ProcessAuthRequestAsync(msg, ct);
        }
    }


    private async Task ProcessAuthRequestAsync(NATS.Client.Core.NatsMsg<byte[]> msg, CancellationToken ct)
    {
            await AuthProcessingGate.WaitAsync(ct);
            try
            {
                await using var msgScope = _scopeFactory.CreateAsyncScope();
                var agentAuthService = msgScope.ServiceProvider.GetRequiredService<IAgentAuthService>();
                var jwtService = msgScope.ServiceProvider.GetRequiredService<IJwtService>();
                var permissionService = msgScope.ServiceProvider.GetRequiredService<IPermissionService>();
                var credentialsService = msgScope.ServiceProvider.GetRequiredService<INatsCredentialsService>();
                var msgConfigService = msgScope.ServiceProvider.GetRequiredService<IConfigurationService>();

                var rawData = msg.Data ?? [];

                // Resolve xkey seed (opcional). Quando configurado, o payload esta encriptado.
                var xKeySeedPlain = _configuration["Nats:XKeySeed"];
                xKeySeedPlain = string.IsNullOrWhiteSpace(xKeySeedPlain) ? null : xKeySeedPlain;

                string? serverXKey = null;
                if (msg.Headers != null && msg.Headers.TryGetValue(ServerXKeyHeader, out var xkeyValueRaw))
                {
                    serverXKey = xkeyValueRaw.ToString();
                }

                if (!TryDecryptRequestPayload(rawData, xKeySeedPlain, serverXKey, out var requestJwt, out var decryptError))
                {
                    _logger.LogWarning(
                        "NATS auth callout request rejected during payload decryption: {Reason}",
                        decryptError);
                    await ReplyAuthErrorAsync(_natsConnection, msg.ReplyTo, decryptError ?? "Auth request rejected.", ct);
                    return;
                }

                var responseJwt = await HandleAuthRequestAsync(
                    requestJwt,
                    agentAuthService, jwtService, permissionService, credentialsService, msgConfigService,
                    ct);

                if (string.IsNullOrWhiteSpace(msg.ReplyTo))
                    return;

                if (!string.IsNullOrWhiteSpace(xKeySeedPlain) && !string.IsNullOrWhiteSpace(serverXKey))
                {
                    // Encripta resposta para o server usando a chave efemera dele
                    using var xKeyPair = KeyPair.FromSeed(xKeySeedPlain);
                    var encryptedResponse = xKeyPair.Seal(Encoding.UTF8.GetBytes(responseJwt), serverXKey);
                    await _natsConnection.PublishAsync(msg.ReplyTo, encryptedResponse, cancellationToken: ct);
                }
                else
                {
                    await _natsConnection.PublishAsync(msg.ReplyTo, responseJwt, cancellationToken: ct);
                }
            }
            catch (OperationCanceledException)
            {
                return; // task concorrente cancelada: encerra apenas esta autenticacao
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process NATS auth callout request. ReplyTo={ReplyTo}", msg.ReplyTo);
                // SEM resposta aqui o NATS espera o auth_timeout e o cliente
                // recebe o generico "Authentication Timeout" (incidente 2026-10-27).
                // Responder SEMPRE com uma negacao explicita para o erro ficar
                // visivel no cliente.
                if (!string.IsNullOrWhiteSpace(msg.ReplyTo))
                {
                    try
                    {
                        await using var errScope = _scopeFactory.CreateAsyncScope();
                        var errConfig = errScope.ServiceProvider.GetRequiredService<IConfigurationService>();
                        var errorJwt = await BuildErrorResponseAsync(
                            "Auth callout internal error.", null, null, errConfig, ct);

                        var xKeySeedPlain = _configuration["Nats:XKeySeed"];
                        string? serverXKey = null;
                        if (msg.Headers != null && msg.Headers.TryGetValue(ServerXKeyHeader, out var xkeyHdr))
                            serverXKey = xkeyHdr.ToString();

                        if (!string.IsNullOrWhiteSpace(xKeySeedPlain) && !string.IsNullOrWhiteSpace(serverXKey))
                        {
                            try
                            {
                                using var xKeyPair = KeyPair.FromSeed(xKeySeedPlain);
                                var encryptedError = xKeyPair.Seal(Encoding.UTF8.GetBytes(errorJwt), serverXKey);
                                await _natsConnection.PublishAsync(msg.ReplyTo, encryptedError, cancellationToken: ct);
                                return;
                            }
                            catch (Exception sealEx)
                            {
                                _logger.LogDebug(sealEx, "Failed to xkey-seal error response; falling back to plaintext.");
                            }
                        }

                        await _natsConnection.PublishAsync(msg.ReplyTo, errorJwt, cancellationToken: ct);
                    }
                    catch (Exception pubEx)
                    {
                        _logger.LogError(pubEx, "Failed to publish auth callout error response.");
                    }
                }
            }
            finally
            {
                AuthProcessingGate.Release();
            }
    }

    /// <summary>
    /// Publica uma negacao de auth explicita para o servidor NATS nos
    /// caminhos que antes apenas faziam continue/return sem responder.
    /// </summary>
    private static async Task ReplyAuthErrorAsync(
        NatsConnection conn, string? replyTo, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(replyTo))
            return;
        try
        {
            await conn.PublishAsync(replyTo, Encoding.UTF8.GetBytes(reason), cancellationToken: ct);
        }
        catch
        {
            // o chamador loga o contexto
        }
    }

    private async Task<string> HandleAuthRequestAsync(
        string requestJwt,
        IAgentAuthService agentAuthService,
        IJwtService jwtService,
        IPermissionService permissionService,
        INatsCredentialsService credentialsService,
        IConfigurationService configurationService,
        CancellationToken ct)
    {
        var expectedServerPublicKey = _configuration["Nats:ServerPublicKey"];
        if (string.IsNullOrWhiteSpace(expectedServerPublicKey))
        {
            // Defesa em profundidade (R1): sem Nats:ServerPublicKey configurado,
            // a confiança do auth request fica reduzida à checagem
            // issuer == nats.Server.Id (o server.id vem do broker genuíno, então
            // o fluxo real continua seguro) — mas a trust list explícita é a
            // proteção recomendada contra cenários de rede adversária.
            _logger.LogWarning(
                "Auth callout: Nats:ServerPublicKey vazio — configure a(s) chave(s) públicas do servidor NATS para ancorar a confiança dos auth requests");
        }
        var request = ParseAuthRequest(requestJwt, expectedServerPublicKey);
        if (request is null)
        {
            _logger.LogWarning(
                "Auth callout: failed to parse auth request JWT. JwtLength={JwtLength}",
                requestJwt?.Length ?? 0);
            return await BuildErrorResponseAsync("Invalid auth request.", null, null, configurationService, ct);
        }

        var serverId = request.Nats.Server?.Id;

        _logger.LogInformation(
            "Auth callout request received. ServerId={ServerId}, UserNkey={UserNkey}, " +
            "HasAuthToken={HasAuthToken}, HasToken={HasToken}, HasJwt={HasJwt}",
            serverId,
            request.Nats.UserNkey,
            !string.IsNullOrWhiteSpace(request.Nats.ConnectOptions.AuthToken),
            !string.IsNullOrWhiteSpace(request.Nats.ConnectOptions.Token),
            !string.IsNullOrWhiteSpace(request.Nats.ConnectOptions.Jwt));

        if (string.IsNullOrWhiteSpace(request.Nats.UserNkey))
            return await BuildErrorResponseAsync("Missing user nkey.", null, serverId, configurationService, ct);

        var token = FirstNonEmpty(
            request.Nats.ConnectOptions.AuthToken,
            request.Nats.ConnectOptions.Token,
            request.Nats.ConnectOptions.Jwt);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning(
                "Auth callout request sem token aproveitavel (auth_token={HasAuthToken}, token={HasToken}, jwt={HasJwt}).",
                !string.IsNullOrWhiteSpace(request.Nats.ConnectOptions.AuthToken),
                !string.IsNullOrWhiteSpace(request.Nats.ConnectOptions.Token),
                !string.IsNullOrWhiteSpace(request.Nats.ConnectOptions.Jwt));
            return await BuildErrorResponseAsync("Missing auth token.", request.Nats.UserNkey, serverId, configurationService, ct);
        }

        token = NormalizeAuthToken(token);

        if (token.StartsWith("mdz_", StringComparison.OrdinalIgnoreCase))
        {
            var agentToken = await agentAuthService.ValidateTokenAsync(token);
            if (agentToken is null)
                return await BuildErrorResponseAsync("Invalid agent token.", request.Nats.UserNkey, serverId, configurationService, ct);

            // Fase 1: Detecção de conexão duplicada.
            // Se o mesmo token já está em uso por outra conexão NATS, rejeitamos.
            var sessionAcquired = await agentAuthService.TryAcquireNatsSessionAsync(
                agentToken.Id,
                agentToken.AgentId,
                request.Nats.UserNkey,
                TimeSpan.FromMinutes(5));

            if (!sessionAcquired)
            {
                _logger.LogWarning(
                    "NATS auth callout rejected: token {TokenId} for agent {AgentId} already has an active NATS session.",
                    agentToken.Id,
                    agentToken.AgentId);
                return await BuildErrorResponseAsync(
                    "Token already in use by another connection.",
                    request.Nats.UserNkey,
                    serverId,
                    configurationService,
                    ct);
            }

            // Fase 2: Auditoria — registra última conexão NATS bem-sucedida.
            await agentAuthService.UpdateLastNatsConnectedAsync(agentToken.Id);

            var jwt = await credentialsService.IssueUserJwtForAgentAsync(
                request.Nats.UserNkey,
                agentToken.AgentId,
                ct);

            return await BuildSuccessResponseAsync(request, jwt.Jwt, jwt.ExpiresAtUtc, configurationService, ct);
        }

        // Aceita JWT NATS pré-emitido pela API (agent, user ou sessão remota), que
        // chega no CONNECT como auth_token. O NATS em modo config (auth_callout
        // sem operator mode) DESCARTA o campo "jwt" do CONNECT antes de chamar o
        // callout (nats-server client.go: "when not in operator mode, discard the
        // jwt"), então a credencial escopada emitida por /nats-credentials só
        // sobrevive se for enviada como auth_token — senão o callout recebe
        // connect_opts vazio e responde "Missing auth token.".
        //
        // Valida issuer/validade e REEMITE um JWT com sub = request.Nats.UserNkey:
        // o user_nkey do auth request é uma chave EFÊMERA gerada pelo NATS a cada
        // conexão (nats-server auth_callout.go) e o server só aceita o JWT
        // devolvido se o subject dele for exatamente essa chave. Comparar com o sub
        // do JWT pré-emitido (a chave do cliente) nunca casa — era a segunda causa
        // do Authorization Violation mesmo quando o JWT chegava ao callout.
        if (TryValidatePreIssuedNatsJwt(token, out var preIssuedExpiresAtUtc, out var isSessionToken, out var jwtSubject, out var pubPerms, out var subPerms))
        {
            _logger.LogInformation(
                "Auth callout: pre-issued NATS JWT validated. IsSession={IsSession}, Subject={Subject}, " +
                "UserNkey={UserNkey}, PubCount={PubCount}, SubCount={SubCount}, Exp={ExpUtc}",
                isSessionToken, jwtSubject, request.Nats.UserNkey,
                pubPerms.Length, subPerms.Length, preIssuedExpiresAtUtc);

            // Mantém a conexão autorizada até a expiração da credencial original:
            // o NATS encerra o cliente quando o JWT reemitido expira.
            var remainingMinutes = (int)Math.Ceiling((preIssuedExpiresAtUtc - DateTime.UtcNow).TotalMinutes);
            var ttlMinutes = Math.Clamp(remainingMinutes, 1, 60);

            var reissuedJwt = await credentialsService.IssueSessionJwtForPublicKeyAsync(
                request.Nats.UserNkey,
                pubPerms,
                subPerms,
                ttlMinutes,
                isSessionToken ? $"session:{jwtSubject}" : $"preissued:{jwtSubject}",
                ct);

            _logger.LogInformation(
                "Auth callout: pre-issued JWT reissued for userNkey={UserNkey}. OriginalSubject={Subject}, " +
                "Pub=[{PubPerms}], Sub=[{SubPerms}], NewExp={NewExpUtc}",
                request.Nats.UserNkey, jwtSubject,
                string.Join(",", pubPerms), string.Join(",", subPerms),
                reissuedJwt.ExpiresAtUtc);

            return await BuildSuccessResponseAsync(request, reissuedJwt.Jwt, reissuedJwt.ExpiresAtUtc, configurationService, ct);
        }

        var principal = jwtService.ValidateToken(token);
        if (principal is null)
        {
            _logger.LogWarning(
                "Auth callout failed to validate token as API JWT or pre-issued NATS JWT. UserNkey={UserNkey}, TokenLength={TokenLength}",
                request.Nats.UserNkey,
                token.Length);
            return await BuildErrorResponseAsync("Invalid user token.", request.Nats.UserNkey, serverId, configurationService, ct);
        }

        var userIdValue = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                          ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdValue, out var userId))
            return await BuildErrorResponseAsync("Invalid user token.", request.Nats.UserNkey, serverId, configurationService, ct);

        if (principal.FindFirst("mfa_pending")?.Value == "true" || principal.FindFirst("mfa_setup")?.Value == "true")
            return await BuildErrorResponseAsync("MFA pending token is not allowed.", request.Nats.UserNkey, serverId, configurationService, ct);

        var scopeAccess = await permissionService.GetScopeAccessAsync(userId, Discovery.Core.Enums.Identity.ResourceType.Dashboard, Discovery.Core.Enums.Identity.ActionType.View);
        if (!scopeAccess.HasGlobalAccess && scopeAccess.AllowedClientIds.Count == 0 && scopeAccess.AllowedSiteIds.Count == 0)
            return await BuildErrorResponseAsync("User has no dashboard access.", request.Nats.UserNkey, serverId, configurationService, ct);

        var remoteDebugScopeAccess = await permissionService.GetScopeAccessAsync(
            userId,
            Discovery.Core.Enums.Identity.ResourceType.RemoteDebug,
            Discovery.Core.Enums.Identity.ActionType.Execute);

        var userJwt = await credentialsService.IssueUserJwtForUserAsync(
            request.Nats.UserNkey,
            userId,
            scopeAccess,
            ct,
            remoteDebugScopeAccess);
        return await BuildSuccessResponseAsync(request, userJwt.Jwt, userJwt.ExpiresAtUtc, configurationService, ct);
    }

    /// <summary>
    /// Valida um JWT NATS pré-emitido (agent, user ou sessão) emitido por
    /// NatsCredentialsService: verifica assinatura Ed25519 com a AccountSeed,
    /// header typ=JWT/alg=ed25519-nkey, issuer (account public key), PrefixByte.User
    /// do subject, exp/nbf e extrai as permissões pub/sub do claim "nats".
    ///
    /// Em HandleAuthRequestAsync, expectedUserNkey é null porque o user_nkey do auth
    /// request é uma chave efêmera gerada pelo NATS por conexão (nats-server auth_callout.go),
    /// enquanto o sub do JWT pré-emitido é a chave do cliente.
    ///
    /// isSessionToken identifica apenas o rótulo de auditoria (Name = "session:*").
    /// </summary>
    internal bool TryValidatePreIssuedNatsJwt(
        string token,
        out DateTime expiresAtUtc,
        out bool isSessionToken,
        out string jwtSubject,
        out string[] pubPerms,
        out string[] subPerms)
        => TryValidatePreIssuedNatsJwt(
            token,
            expectedUserNkey: null,
            out expiresAtUtc,
            out isSessionToken,
            out jwtSubject,
            out pubPerms,
            out subPerms);

    internal bool TryValidatePreIssuedNatsJwt(
        string token,
        string? expectedUserNkey,
        out DateTime expiresAtUtc,
        out bool isSessionToken,
        out string jwtSubject,
        out string[] pubPerms,
        out string[] subPerms)
    {
        expiresAtUtc = default;
        isSessionToken = false;
        jwtSubject = string.Empty;
        pubPerms = [];
        subPerms = [];

        if (!TrySplitAndVerifyHeader(token, out var parts, out var headerError))
        {
            _logger.LogWarning("TryValidatePreIssuedNatsJwt: {Reason}", headerError);
            return false;
        }

        // Resolve AccountSeed e verifica assinatura Ed25519 ANTES de confiar em qualquer claim.
        var accountSeed = _configuration["Nats:AccountSeed"];
        if (string.IsNullOrWhiteSpace(accountSeed))
        {
            _logger.LogWarning("TryValidatePreIssuedNatsJwt: Nats:AccountSeed não configurado.");
            return false;
        }

        KeyPair accountKeyPair;
        try
        {
            accountKeyPair = KeyPair.FromSeed(accountSeed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryValidatePreIssuedNatsJwt: invalid Nats:AccountSeed.");
            return false;
        }

        using (accountKeyPair)
        {
            if (!TryDecodeBase64Url(parts[2], out var sigBytes) || sigBytes.Length != 64)
            {
                _logger.LogWarning("TryValidatePreIssuedNatsJwt: invalid Ed25519 signature encoding/length.");
                return false;
            }

            var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            try
            {
                if (!accountKeyPair.Verify(signingInput, sigBytes))
                {
                    _logger.LogWarning("TryValidatePreIssuedNatsJwt: Ed25519 signature verification failed.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryValidatePreIssuedNatsJwt: signature verification threw an exception.");
                return false;
            }

            if (!TryDecodeBase64Url(parts[1], out var payloadBytes))
            {
                _logger.LogWarning("TryValidatePreIssuedNatsJwt: invalid Base64URL payload.");
                return false;
            }

            try
            {
                using var payloadDoc = JsonDocument.Parse(payloadBytes);
                var root = payloadDoc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("TryValidatePreIssuedNatsJwt: payload is not a JSON object.");
                    return false;
                }

                var expectedIssuer = accountKeyPair.GetPublicKey();
                var issuer = TryGetStringClaim(root, "iss");
                if (string.IsNullOrWhiteSpace(issuer)
                    || !string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "TryValidatePreIssuedNatsJwt: issuer mismatch. Expected={Expected}, Actual={Actual}",
                        expectedIssuer, issuer);
                    return false;
                }

                var sub = TryGetStringClaim(root, "sub");
                if (string.IsNullOrWhiteSpace(sub) || !IsValidNkeyPublicKey(PrefixByte.User, sub))
                {
                    _logger.LogWarning("TryValidatePreIssuedNatsJwt: rejected due to missing or invalid User NKey subject.");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(expectedUserNkey)
                    && !string.Equals(sub, expectedUserNkey, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "TryValidatePreIssuedNatsJwt: subject NKey mismatch. Expected={Expected}, Actual={Actual}",
                        expectedUserNkey, sub);
                    return false;
                }

                var now = DateTimeOffset.UtcNow;
                if (!TryGetUnixTimestamp(root, "exp", out var expUnix))
                {
                    _logger.LogWarning("TryValidatePreIssuedNatsJwt: no expiration claim.");
                    return false;
                }

                var exp = DateTimeOffset.FromUnixTimeSeconds(expUnix);
                if (exp <= now.Subtract(JwtClockSkew))
                {
                    _logger.LogWarning("TryValidatePreIssuedNatsJwt: JWT expired. Exp={ExpUtc}", exp);
                    return false;
                }

                if (root.TryGetProperty("nbf", out _))
                {
                    if (!TryGetUnixTimestamp(root, "nbf", out var nbfUnix))
                    {
                        _logger.LogWarning("TryValidatePreIssuedNatsJwt: invalid nbf claim.");
                        return false;
                    }

                    var nbf = DateTimeOffset.FromUnixTimeSeconds(nbfUnix);
                    if (nbf > now.Add(JwtClockSkew))
                    {
                        _logger.LogWarning("TryValidatePreIssuedNatsJwt: not-before in the future. Nbf={NbfUtc}", nbf);
                        return false;
                    }
                }

                if (!TryGetNatsElement(root, out var natsElement))
                {
                    _logger.LogWarning("TryValidatePreIssuedNatsJwt: missing or invalid nats claim.");
                    return false;
                }

                if (natsElement.TryGetProperty("type", out var typeProp))
                {
                    if (typeProp.ValueKind != JsonValueKind.String
                        || !string.Equals(typeProp.GetString(), "user", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("TryValidatePreIssuedNatsJwt: unexpected nats.type ({ValueKind}).", typeProp.ValueKind);
                        return false;
                    }
                }

                if (natsElement.TryGetProperty("version", out var versionProp))
                {
                    if (versionProp.ValueKind != JsonValueKind.Number
                        || !versionProp.TryGetInt32(out var version)
                        || version < 2)
                    {
                        _logger.LogWarning("TryValidatePreIssuedNatsJwt: unsupported nats.version ({ValueKind}).", versionProp.ValueKind);
                        return false;
                    }
                }

                var nameClaim = TryGetStringClaim(root, "name") ?? TryGetStringClaim(root, "sub_name");
                isSessionToken = !string.IsNullOrWhiteSpace(nameClaim)
                    && nameClaim.StartsWith("session:", StringComparison.OrdinalIgnoreCase);

                pubPerms = ExtractNatsPermissionsFromElement(natsElement, "pub");
                subPerms = ExtractNatsPermissionsFromElement(natsElement, "sub");

                jwtSubject = sub;
                expiresAtUtc = exp.UtcDateTime;

                _logger.LogInformation(
                    "TryValidatePreIssuedNatsJwt: JWT validado. IsSession={IsSession}, Subject={Subject}, " +
                    "Name={Name}, PubCount={PubCount}, SubCount={SubCount}, Exp={ExpUtc}",
                    isSessionToken, jwtSubject, nameClaim, pubPerms.Length, subPerms.Length, expiresAtUtc);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryValidatePreIssuedNatsJwt: failed to parse or validate JWT payload.");
                return false;
            }
        }
    }

    internal static bool TryDecryptRequestPayload(
        byte[] rawData,
        string? xKeySeed,
        string? serverXKey,
        out string requestJwt,
        out string? errorReason)
    {
        requestJwt = string.Empty;
        errorReason = null;

        if (!string.IsNullOrWhiteSpace(xKeySeed))
        {
            if (serverXKey is null)
            {
                errorReason = "Auth request rejected (missing xkey header).";
                return false;
            }

            if (string.IsNullOrWhiteSpace(serverXKey))
            {
                errorReason = "Auth request rejected (empty xkey header).";
                return false;
            }

            try
            {
                using var xKeyPair = KeyPair.FromSeed(xKeySeed);
                var decrypted = xKeyPair.Open(rawData, serverXKey);
                requestJwt = Encoding.UTF8.GetString(decrypted);
                return true;
            }
            catch
            {
                errorReason = "Auth request rejected (xkey decryption failed).";
                return false;
            }
        }

        requestJwt = Encoding.UTF8.GetString(rawData);
        return true;
    }

    private static bool TrySplitAndVerifyHeader(string? token, out string[] parts, out string errorReason)
    {
        parts = [];
        errorReason = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            errorReason = "empty token.";
            return false;
        }

        if (token.Length > NatsJwt.MaxTokenSize)
        {
            errorReason = $"token exceeds MaxTokenSize ({NatsJwt.MaxTokenSize}).";
            return false;
        }

        parts = token.Split('.');
        if (parts.Length != 3
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1])
            || string.IsNullOrWhiteSpace(parts[2]))
        {
            errorReason = "malformed 3-part JWT.";
            return false;
        }

        if (!TryDecodeBase64Url(parts[0], out var headerBytes))
        {
            errorReason = "invalid Base64URL header.";
            return false;
        }

        try
        {
            using var headerDoc = JsonDocument.Parse(headerBytes);
            var header = headerDoc.RootElement;
            if (header.ValueKind != JsonValueKind.Object)
            {
                errorReason = "header is not a JSON object.";
                return false;
            }

            var typ = TryGetStringClaim(header, "typ");
            if (!string.Equals(typ, NatsJwt.TokenTypeJwt, StringComparison.OrdinalIgnoreCase))
            {
                errorReason = $"unsupported JWT typ '{typ}'.";
                return false;
            }

            var alg = TryGetStringClaim(header, "alg");
            if (!string.Equals(alg, NatsJwt.AlgorithmNkey, StringComparison.Ordinal))
            {
                errorReason = $"unsupported JWT alg '{alg}'.";
                return false;
            }

            return true;
        }
        catch
        {
            errorReason = "invalid JSON header.";
            return false;
        }
    }

    private static bool TryDecodeBase64Url(string input, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(input))
            return false;

        foreach (var ch in input)
        {
            var isValidBase64UrlChar = (ch >= 'A' && ch <= 'Z')
                || (ch >= 'a' && ch <= 'z')
                || (ch >= '0' && ch <= '9')
                || ch == '-'
                || ch == '_';
            if (!isValidBase64UrlChar)
                return false;
        }

        try
        {
            var base64 = input.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
                case 1: return false;
            }

            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidNkeyPublicKey(PrefixByte expectedPrefix, string publicKey)
    {
        try
        {
            return KeyPair.IsValidPublicKey(expectedPrefix, publicKey.AsSpan());
        }
        catch (NKeysException)
        {
            return false;
        }
    }

    private static string? TryGetStringClaim(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static bool TryGetUnixTimestamp(JsonElement root, string propertyName, out long unixSeconds)
    {
        unixSeconds = 0;
        if (!root.TryGetProperty(propertyName, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out unixSeconds))
            return true;

        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out unixSeconds))
            return true;

        return false;
    }

    private static bool TryGetNatsElement(JsonElement root, out JsonElement natsElement)
    {
        if (root.TryGetProperty("nats", out natsElement) && natsElement.ValueKind == JsonValueKind.Object)
            return true;
        if (root.TryGetProperty("nat", out natsElement) && natsElement.ValueKind == JsonValueKind.Object)
            return true;
        natsElement = default;
        return false;
    }

    private static string[] ExtractNatsPermissionsFromElement(JsonElement natsElement, string type)
    {
        try
        {
            if (natsElement.TryGetProperty(type, out var typeElement)
                && typeElement.ValueKind == JsonValueKind.Object
                && typeElement.TryGetProperty("allow", out var allowElement)
                && allowElement.ValueKind == JsonValueKind.Array)
            {
                return allowElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToArray();
            }
        }
        catch
        {
            // Permissões vazias são aceitáveis
        }

        return [];
    }

    internal static AuthorizationRequest? ParseAuthRequest(string jwt, string? expectedServerPublicKey = null)
    {
        try
        {
            if (!TrySplitAndVerifyHeader(jwt, out var parts, out _))
                return null;

            if (!TryDecodeBase64Url(parts[2], out var sigBytes) || sigBytes.Length != 64)
                return null;

            if (!TryDecodeBase64Url(parts[1], out var payloadBytes))
                return null;

            using var payloadDoc = JsonDocument.Parse(payloadBytes);
            var root = payloadDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var issuer = TryGetStringClaim(root, "iss");
            if (string.IsNullOrWhiteSpace(issuer) || !IsValidNkeyPublicKey(PrefixByte.Server, issuer))
                return null;

            if (!TryGetNatsElement(root, out var natsElement))
                return null;

            var nats = JsonSerializer.Deserialize<AuthRequestNats>(natsElement.GetRawText(), JsonSerializerOptions.Web);
            if (nats is null || nats.Server is null || string.IsNullOrWhiteSpace(nats.Server.Id))
                return null;

            if (!string.Equals(issuer, nats.Server.Id, StringComparison.Ordinal))
                return null;

            if (!string.IsNullOrWhiteSpace(expectedServerPublicKey))
            {
                var trustedKeys = expectedServerPublicKey.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (trustedKeys.Length == 0 || !trustedKeys.Contains(issuer, StringComparer.Ordinal))
                    return null;
            }

            var sub = TryGetStringClaim(root, "sub");
            if (!string.IsNullOrWhiteSpace(sub)
                && !string.IsNullOrWhiteSpace(nats.UserNkey)
                && !string.Equals(sub, nats.UserNkey, StringComparison.Ordinal))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (root.TryGetProperty("exp", out _))
            {
                if (!TryGetUnixTimestamp(root, "exp", out var expUnix))
                    return null;
                if (DateTimeOffset.FromUnixTimeSeconds(expUnix) <= now.Subtract(JwtClockSkew))
                    return null;
            }

            if (root.TryGetProperty("nbf", out _))
            {
                if (!TryGetUnixTimestamp(root, "nbf", out var nbfUnix))
                    return null;
                if (DateTimeOffset.FromUnixTimeSeconds(nbfUnix) > now.Add(JwtClockSkew))
                    return null;
            }

            var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            using var serverKeyPair = KeyPair.FromPublicKey(issuer.AsSpan());
            if (!serverKeyPair.Verify(signingInput, sigBytes))
                return null;

            return new AuthorizationRequest { Nats = nats };
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> BuildSuccessResponseAsync(AuthorizationRequest request, string userJwt, DateTime expiresAtUtc, IConfigurationService configurationService, CancellationToken ct)
    {
        using var accountKeyPair = await ResolveAccountKeyPairAsync(configurationService, ct);
        var response = NatsJwt.NewAuthorizationResponseClaims(request.Nats.UserNkey);
        // O NATS valida que o `aud` da resposta seja a server public key (server_id.id) que enviou a request.
        response.Audience = request.Nats.Server?.Id ?? string.Empty;
        response.Expires = new DateTimeOffset(expiresAtUtc);
        response.AuthorizationResponse.Jwt = userJwt;
        return NatsJwt.EncodeAuthorizationResponseClaims(response, accountKeyPair);
    }

    private async Task<string> BuildErrorResponseAsync(string error, string? userNkey, string? serverId, IConfigurationService configurationService, CancellationToken ct)
    {
        using var accountKeyPair = await ResolveAccountKeyPairAsync(configurationService, ct);
        var now = DateTime.UtcNow;
        // NatsJwt se recusa a codificar uma resposta sem subject: quando o auth
        // request está corrompido/ilegível (ParseAuthRequest falhou) ou o erro
        // acontece antes de sabermos o user_nkey, usamos um subject PLACEHOLDER
        // para que a NEGAÇÃO chegue ao servidor — antes o throw dentro da
        // codificação engolia a resposta e o servidor esperava o auth_timeout
        // inteiro sem resposta (o cliente via "Authentication Timeout" genérico).
        var subject = !string.IsNullOrWhiteSpace(userNkey)
            ? userNkey
            : (!string.IsNullOrWhiteSpace(serverId) ? serverId : "unknown-requester");
        var response = NatsJwt.NewAuthorizationResponseClaims(subject);
        response.Audience = serverId ?? string.Empty;
        response.Expires = new DateTimeOffset(now.AddMinutes(1));
        response.AuthorizationResponse.Error = error;
        return NatsJwt.EncodeAuthorizationResponseClaims(response, accountKeyPair);
    }

    private Task<KeyPair> ResolveAccountKeyPairAsync(IConfigurationService configurationService, CancellationToken ct)
    {
        _ = configurationService;
        _ = ct;
        var seed = _configuration["Nats:AccountSeed"];
        if (string.IsNullOrWhiteSpace(seed))
            throw new InvalidOperationException("NATS account seed is not configured (Nats:AccountSeed).");

        return Task.FromResult(KeyPair.FromSeed(seed));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string NormalizeAuthToken(string token)
    {
        var normalized = token.Trim();
        const string bearerPrefix = "Bearer ";

        if (normalized.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[bearerPrefix.Length..].Trim();

        if (normalized.Length > 1 && normalized[0] == '"' && normalized[^1] == '"')
            normalized = normalized[1..^1].Trim();

        return normalized;
    }

    internal sealed class AuthorizationRequest
    {
        public AuthRequestNats Nats { get; init; } = new();
    }

    internal sealed class AuthRequestNats
    {
        [System.Text.Json.Serialization.JsonPropertyName("user_nkey")]
        public string UserNkey { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("connect_opts")]
        public AuthRequestConnectOptions ConnectOptions { get; init; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("server_id")]
        public AuthRequestServerId? Server { get; init; }
    }

    internal sealed class AuthRequestServerId
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    internal sealed class AuthRequestConnectOptions
    {
        [System.Text.Json.Serialization.JsonPropertyName("auth_token")]
        public string? AuthToken { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("token")]
        public string? Token { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("jwt")]
        public string? Jwt { get; init; }
    }
}
