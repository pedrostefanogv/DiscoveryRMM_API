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
        using var scope = _scopeFactory.CreateScope();
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
        await foreach (var msg in _natsConnection.SubscribeAsync<byte[]>(subject, cancellationToken: ct))
        {
            try
            {
                using var msgScope = _scopeFactory.CreateScope();
                var agentAuthService = msgScope.ServiceProvider.GetRequiredService<IAgentAuthService>();
                var jwtService = msgScope.ServiceProvider.GetRequiredService<IJwtService>();
                var permissionService = msgScope.ServiceProvider.GetRequiredService<IPermissionService>();
                var credentialsService = msgScope.ServiceProvider.GetRequiredService<INatsCredentialsService>();
                var msgConfigService = msgScope.ServiceProvider.GetRequiredService<IConfigurationService>();

                var rawData = msg.Data ?? [];

                // Resolve xkey seed (opcional). Quando configurado, o payload esta encriptado.
                var xKeySeedPlain = _configuration["Nats:XKeySeed"];
                xKeySeedPlain = string.IsNullOrWhiteSpace(xKeySeedPlain) ? null : xKeySeedPlain;

                string requestJwt;
                string? serverXKey = null;

                if (!string.IsNullOrWhiteSpace(xKeySeedPlain))
                {
                    // xkey habilitado: decripta payload usando DH curve25519.
                    // Se o header xkey nao estiver presente ou vazio, rejeita a requisicao
                    // para evitar bypass de criptografia.
                    if (msg.Headers == null || !msg.Headers.TryGetValue(ServerXKeyHeader, out var xkeyValue))
                    {
                        _logger.LogWarning(
                            "xkey configurado mas header {Header} ausente na requisicao. Requisicao rejeitada.",
                            ServerXKeyHeader);
                        continue;
                    }

                    serverXKey = xkeyValue.ToString();
                    if (string.IsNullOrWhiteSpace(serverXKey))
                    {
                        _logger.LogWarning(
                            "xkey configurado mas header {Header} esta vazio na requisicao. Requisicao rejeitada.",
                            ServerXKeyHeader);
                        continue;
                    }

                    using var xKeyPair = KeyPair.FromSeed(xKeySeedPlain);
                    var decrypted = xKeyPair.Open(rawData, serverXKey);
                    requestJwt = Encoding.UTF8.GetString(decrypted);
                }
                else
                {
                    // Sem xkey: payload e o JWT diretamente como UTF-8
                    requestJwt = Encoding.UTF8.GetString(rawData);
                }

                var responseJwt = await HandleAuthRequestAsync(
                    requestJwt,
                    agentAuthService, jwtService, permissionService, credentialsService, msgConfigService,
                    ct);

                if (string.IsNullOrWhiteSpace(msg.ReplyTo))
                    continue;

                if (!string.IsNullOrWhiteSpace(xKeySeedPlain) && serverXKey != null)
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
                throw; // propaga para o loop de reload
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process NATS auth callout request.");
            }
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
        var request = ParseAuthRequest(requestJwt);
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

        // Aceita JWT NATS pré-emitido pela API (agent, user, ou sessão remota).
        // Valida assinatura (account key), issuer e validade temporal.
        if (TryValidatePreIssuedNatsJwt(token, out var preIssuedExpiresAtUtc, out var isSessionToken, out var jwtSubject, out var pubPerms, out var subPerms))
        {
            _logger.LogInformation(
                "Auth callout: pre-issued NATS JWT validated. IsSession={IsSession}, Subject={Subject}, " +
                "UserNkey={UserNkey}, PubCount={PubCount}, SubCount={SubCount}, Exp={ExpUtc}",
                isSessionToken, jwtSubject, request.Nats.UserNkey,
                pubPerms.Length, subPerms.Length, preIssuedExpiresAtUtc);

            // Para JWTs de sessão remota, o userNkey do WebSocket não corresponde ao sub do JWT.
            // Reemitimos um novo JWT com sub = request.Nats.UserNkey e as mesmas permissões,
            // mesmo padrão usado para agents via IssueUserJwtForAgentAsync.
            if (isSessionToken)
            {
                _logger.LogInformation(
                    "Auth callout: reissuing session JWT for userNkey={UserNkey}. " +
                    "Original subject={Subject}, Name={Name}, Pub=[{PubPerms}], Sub=[{SubPerms}]",
                    request.Nats.UserNkey, jwtSubject, $"session:{jwtSubject}",
                    string.Join(",", pubPerms), string.Join(",", subPerms));

                var sessionJwt = await credentialsService.IssueSessionJwtForPublicKeyAsync(
                    request.Nats.UserNkey,
                    pubPerms,
                    subPerms,
                    ttlMinutes: 5, // curto, só para autorizar a conexão
                    $"session:{jwtSubject}",
                    ct);

                _logger.LogInformation(
                    "Auth callout: session JWT reissued successfully. NewExp={NewExpUtc}",
                    sessionJwt.ExpiresAtUtc);

                return await BuildSuccessResponseAsync(request, sessionJwt.Jwt, sessionJwt.ExpiresAtUtc, configurationService, ct);
            }

            // Para JWTs de agent/user, valida userNkey normalmente
            if (TryValidatePreIssuedNatsUserJwt(token, request.Nats.UserNkey, out preIssuedExpiresAtUtc))
            {
                _logger.LogInformation(
                    "Auth callout: pre-issued user/agent JWT valid. UserNkey={UserNkey}, Exp={ExpUtc}",
                    request.Nats.UserNkey, preIssuedExpiresAtUtc);
                return await BuildSuccessResponseAsync(request, token, preIssuedExpiresAtUtc, configurationService, ct);
            }
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
    /// Valida um JWT NATS pré-emitido (agent, user ou sessão) sem exigir userNkey correspondente.
    /// Para JWTs de sessão remota (Name = "session:*"), extrai também as permissões pub/sub
    /// para reemitir um JWT com sub = request.Nats.UserNkey no auth callout.
    ///
    /// NOTA: Usa decodificação JWT manual (System.IdentityModel.Tokens.Jwt) em vez de
    /// NatsJwt.DecodeUserClaims porque a versão 1.0.1 da lib NATS.Jwt contém um bug
    /// que causa NatsJwtException em JWTs válidos gerados por NatsJwt.EncodeUserClaims.
    /// </summary>
    private bool TryValidatePreIssuedNatsJwt(string token, out DateTime expiresAtUtc, out bool isSessionToken, out string jwtSubject, out string[] pubPerms, out string[] subPerms)
    {
        expiresAtUtc = default;
        isSessionToken = false;
        jwtSubject = string.Empty;
        pubPerms = [];
        subPerms = [];

        // Decodificação manual do JWT usando System.IdentityModel.Tokens.Jwt
        // (já usado em ParseAuthRequest neste mesmo arquivo).
        // Evita bug do NatsJwt.DecodeUserClaims v1.0.1.
        System.IdentityModel.Tokens.Jwt.JwtSecurityToken jwtToken;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            jwtToken = handler.ReadJwtToken(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryValidatePreIssuedNatsJwt: failed to decode JWT manually.");
            return false;
        }

        // Extrai claims do JWT decode manual
        var sub = jwtToken.Subject ?? jwtToken.Payload.Sub
            ?? jwtToken.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
        if (string.IsNullOrWhiteSpace(sub))
        {
            _logger.LogWarning("TryValidatePreIssuedNatsJwt: rejected due to empty subject.");
            return false;
        }

        // Valida assinatura: verifica issuer (account public key)
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

        var expectedIssuer = accountKeyPair.GetPublicKey();
        var issuer = jwtToken.Issuer ?? jwtToken.Payload.Iss
            ?? jwtToken.Claims.FirstOrDefault(c => c.Type == "iss")?.Value;
        if (string.IsNullOrWhiteSpace(issuer)
            || !string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "TryValidatePreIssuedNatsJwt: issuer mismatch. Expected={Expected}, Actual={Actual}",
                expectedIssuer, issuer);
            return false;
        }

        // Valida expiração
        var now = DateTimeOffset.UtcNow;
        var expUnix = jwtToken.Payload.Expiration
            ?? (long.TryParse(jwtToken.Claims.FirstOrDefault(c => c.Type == "exp")?.Value, out var e) ? e : null);
        if (!expUnix.HasValue)
        {
            _logger.LogWarning("TryValidatePreIssuedNatsJwt: no expiration claim.");
            return false;
        }

        var exp = DateTimeOffset.FromUnixTimeSeconds(expUnix.Value);
        if (exp <= now.Subtract(JwtClockSkew))
        {
            _logger.LogWarning("TryValidatePreIssuedNatsJwt: JWT expired. Exp={ExpUtc}", exp);
            return false;
        }

        var nbfUnix = jwtToken.Payload.NotBefore
            ?? (long.TryParse(jwtToken.Claims.FirstOrDefault(c => c.Type == "nbf")?.Value, out var n) ? n : null);
        if (nbfUnix.HasValue)
        {
            var nbf = DateTimeOffset.FromUnixTimeSeconds(nbfUnix.Value);
            if (nbf > now.Add(JwtClockSkew))
            {
                _logger.LogWarning("TryValidatePreIssuedNatsJwt: not-before in the future. Nbf={NbfUtc}", nbf);
                return false;
            }
        }

        // Detecta JWT de sessão remota pelo padrão "session:" no campo Name (claim "name")
        var nameClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "name" || c.Type == "sub_name")?.Value;
        isSessionToken = !string.IsNullOrWhiteSpace(nameClaim)
            && nameClaim.StartsWith("session:", StringComparison.OrdinalIgnoreCase);

        // Extrai permissões pub/sub do claim "nats" (JSON object)
        pubPerms = ExtractNatsClaimPermissions(jwtToken, "pub");
        subPerms = ExtractNatsClaimPermissions(jwtToken, "sub");

        jwtSubject = sub;
        expiresAtUtc = exp.UtcDateTime;

        _logger.LogInformation(
            "TryValidatePreIssuedNatsJwt: JWT validado. IsSession={IsSession}, Subject={Subject}, " +
            "Name={Name}, PubCount={PubCount}, SubCount={SubCount}, Exp={ExpUtc}",
            isSessionToken, jwtSubject, nameClaim, pubPerms.Length, subPerms.Length, expiresAtUtc);

        return true;
    }

    /// <summary>
    /// Extrai permissões pub/sub do claim "nats" do JWT.
    /// O claim "nats" contém um JSON: {"pub":{"allow":[...]},"sub":{"allow":[...]}}
    /// </summary>
    private static string[] ExtractNatsClaimPermissions(JwtSecurityToken jwt, string type)
    {
        try
        {
            var natsClaim = jwt.Claims.FirstOrDefault(c =>
                c.Type == "nats" || c.Type == "nat");
            if (natsClaim is null) return [];

            using var doc = JsonDocument.Parse(natsClaim.Value);
            var root = doc.RootElement;

            if (root.TryGetProperty(type, out var typeElement)
                && typeElement.TryGetProperty("allow", out var allowElement)
                && allowElement.ValueKind == JsonValueKind.Array)
            {
                return allowElement.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToArray();
            }
        }
        catch
        {
            // Log silencioso — permissões vazias são aceitáveis
        }

        return [];
    }

    private bool TryValidatePreIssuedNatsUserJwt(string token, string expectedUserNkey, out DateTime expiresAtUtc)
    {
        expiresAtUtc = default;

        NatsUserClaims claims;
        try
        {
            claims = NatsJwt.DecodeUserClaims(token);
        }
        catch (NatsJwtException)
        {
            return false;
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(claims.Subject)
            || !string.Equals(claims.Subject, expectedUserNkey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Rejected pre-issued NATS JWT due to subject mismatch. Expected={Expected}, Actual={Actual}",
                expectedUserNkey,
                claims.Subject);
            return false;
        }

        var accountSeed = _configuration["Nats:AccountSeed"];
        if (string.IsNullOrWhiteSpace(accountSeed))
            return false;

        var expectedIssuer = KeyPair.FromSeed(accountSeed).GetPublicKey();
        if (string.IsNullOrWhiteSpace(claims.Issuer)
            || !string.Equals(claims.Issuer, expectedIssuer, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Rejected pre-issued NATS JWT due to issuer mismatch. Expected={Expected}, Actual={Actual}",
                expectedIssuer,
                claims.Issuer);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (claims.NotBefore.HasValue && claims.NotBefore.Value > now.Add(JwtClockSkew))
        {
            _logger.LogWarning("Rejected pre-issued NATS JWT due to not-before in the future. Nbf={NbfUtc}", claims.NotBefore);
            return false;
        }

        if (!claims.Expires.HasValue)
        {
            _logger.LogWarning("Rejected pre-issued NATS JWT without expiration.");
            return false;
        }

        if (claims.Expires.Value <= now.Subtract(JwtClockSkew))
        {
            _logger.LogWarning("Rejected pre-issued NATS JWT due to expiration. Exp={ExpUtc}", claims.Expires);
            return false;
        }

        expiresAtUtc = claims.Expires.Value.UtcDateTime;
        return true;
    }

    private static AuthorizationRequest? ParseAuthRequest(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(jwt);
            if (!token.Payload.TryGetValue("nats", out var natsObj))
                return null;

            var json = JsonSerializer.Serialize(natsObj);
            var nats = JsonSerializer.Deserialize<AuthRequestNats>(json, JsonSerializerOptions.Web);
            if (nats is null)
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
        var accountKeyPair = await ResolveAccountKeyPairAsync(configurationService, ct);
        var response = NatsJwt.NewAuthorizationResponseClaims(request.Nats.UserNkey);
        // O NATS valida que o `aud` da resposta seja a server public key (server_id.id) que enviou a request.
        response.Audience = request.Nats.Server?.Id ?? string.Empty;
        response.Expires = new DateTimeOffset(expiresAtUtc);
        response.AuthorizationResponse.Jwt = userJwt;
        return NatsJwt.EncodeAuthorizationResponseClaims(response, accountKeyPair);
    }

    private async Task<string> BuildErrorResponseAsync(string error, string? userNkey, string? serverId, IConfigurationService configurationService, CancellationToken ct)
    {
        var accountKeyPair = await ResolveAccountKeyPairAsync(configurationService, ct);
        var now = DateTime.UtcNow;
        var response = NatsJwt.NewAuthorizationResponseClaims(userNkey ?? string.Empty);
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

    private sealed class AuthorizationRequest
    {
        public AuthRequestNats Nats { get; init; } = new();
    }

    private sealed class AuthRequestNats
    {
        [System.Text.Json.Serialization.JsonPropertyName("user_nkey")]
        public string UserNkey { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("connect_opts")]
        public AuthRequestConnectOptions ConnectOptions { get; init; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("server_id")]
        public AuthRequestServerId? Server { get; init; }
    }

    private sealed class AuthRequestServerId
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class AuthRequestConnectOptions
    {
        [System.Text.Json.Serialization.JsonPropertyName("auth_token")]
        public string? AuthToken { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("token")]
        public string? Token { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("jwt")]
        public string? Jwt { get; init; }
    }
}
