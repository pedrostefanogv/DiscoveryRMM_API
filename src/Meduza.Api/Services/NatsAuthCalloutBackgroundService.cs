using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Core.Interfaces.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Jwt;
using NATS.NKeys;

namespace Meduza.Api.Services;

public class NatsAuthCalloutBackgroundService : BackgroundService
{
    private const string ServerXKeyHeader = "Nats-Server-Xkey";

    private readonly NatsConnection _natsConnection;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Meduza.Core.Interfaces.Security.ISecretProtector _secretProtector;
    private readonly INatsAuthCalloutReloadSignal _reloadSignal;
    private readonly ILogger<NatsAuthCalloutBackgroundService> _logger;

    public NatsAuthCalloutBackgroundService(
        NatsConnection natsConnection,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        Meduza.Core.Interfaces.Security.ISecretProtector secretProtector,
        INatsAuthCalloutReloadSignal reloadSignal,
        ILogger<NatsAuthCalloutBackgroundService> logger)
    {
        _natsConnection = natsConnection;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _secretProtector = secretProtector;
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
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        if (!serverConfig.NatsAuthEnabled)
        {
            _logger.LogInformation("NATS auth callout service aguardando — NatsAuthEnabled = false.");
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(serverConfig.NatsAccountSeed))
        {
            _logger.LogWarning("NATS auth callout service aguardando — NatsAccountSeed não configurado.");
            await Task.Delay(Timeout.Infinite, ct);
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
                var msgServerConfig = await msgConfigService.GetServerConfigAsync();
                var xKeySeedPlain = string.IsNullOrWhiteSpace(msgServerConfig.NatsXKeySeed)
                    ? null
                    : _secretProtector.UnprotectOrSelf(msgServerConfig.NatsXKeySeed);

                string requestJwt;
                string? serverXKey = null;

                if (!string.IsNullOrWhiteSpace(xKeySeedPlain))
                {
                    // xkey habilitado: decripta payload usando DH curve25519
                    if (msg.Headers == null || !msg.Headers.TryGetValue(ServerXKeyHeader, out var xkeyValue))
                    {
                        _logger.LogWarning("xkey configurado mas header {Header} ausente na requisicao.", ServerXKeyHeader);
                        continue;
                    }

                    serverXKey = xkeyValue.ToString();
                    if (string.IsNullOrWhiteSpace(serverXKey))
                    {
                        _logger.LogWarning("xkey configurado mas header {Header} esta vazio.", ServerXKeyHeader);
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
            return await BuildErrorResponseAsync("Invalid auth request.", null, configurationService, ct);

        if (string.IsNullOrWhiteSpace(request.Nats.UserNkey))
            return await BuildErrorResponseAsync("Missing user nkey.", null, configurationService, ct);

        var token = request.Nats.ConnectOptions.AuthToken
            ?? request.Nats.ConnectOptions.Token
            ?? request.Nats.ConnectOptions.Jwt;
        if (string.IsNullOrWhiteSpace(token))
            return await BuildErrorResponseAsync("Missing auth token.", request.Nats.UserNkey, configurationService, ct);

        if (token.StartsWith("mdz_", StringComparison.OrdinalIgnoreCase))
        {
            var agentToken = await agentAuthService.ValidateTokenAsync(token);
            if (agentToken is null)
                return await BuildErrorResponseAsync("Invalid agent token.", request.Nats.UserNkey, configurationService, ct);

            var jwt = await credentialsService.IssueUserJwtForAgentAsync(
                request.Nats.UserNkey,
                agentToken.AgentId,
                ct);

            return await BuildSuccessResponseAsync(request, jwt.Jwt, jwt.ExpiresAtUtc, configurationService, ct);
        }

        var principal = jwtService.ValidateToken(token);
        if (principal is null)
            return await BuildErrorResponseAsync("Invalid user token.", request.Nats.UserNkey, configurationService, ct);

        var userIdValue = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                          ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdValue, out var userId))
            return await BuildErrorResponseAsync("Invalid user token.", request.Nats.UserNkey, configurationService, ct);

        if (principal.FindFirst("mfa_pending")?.Value == "true" || principal.FindFirst("mfa_setup")?.Value == "true")
            return await BuildErrorResponseAsync("MFA pending token is not allowed.", request.Nats.UserNkey, configurationService, ct);

        var scopeAccess = await permissionService.GetScopeAccessAsync(userId, Meduza.Core.Enums.Identity.ResourceType.Dashboard, Meduza.Core.Enums.Identity.ActionType.View);
        if (!scopeAccess.HasGlobalAccess && scopeAccess.AllowedClientIds.Count == 0 && scopeAccess.AllowedSiteIds.Count == 0)
            return await BuildErrorResponseAsync("User has no dashboard access.", request.Nats.UserNkey, configurationService, ct);

        var userJwt = await credentialsService.IssueUserJwtForUserAsync(request.Nats.UserNkey, userId, scopeAccess, ct);
        return await BuildSuccessResponseAsync(request, userJwt.Jwt, userJwt.ExpiresAtUtc, configurationService, ct);
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
        response.Expires = new DateTimeOffset(expiresAtUtc);
        response.AuthorizationResponse.Jwt = userJwt;
        return NatsJwt.EncodeAuthorizationResponseClaims(response, accountKeyPair);
    }

    private async Task<string> BuildErrorResponseAsync(string error, string? userNkey, IConfigurationService configurationService, CancellationToken ct)
    {
        var accountKeyPair = await ResolveAccountKeyPairAsync(configurationService, ct);
        var now = DateTime.UtcNow;
        var response = NatsJwt.NewAuthorizationResponseClaims(userNkey ?? string.Empty);
        response.Expires = new DateTimeOffset(now.AddMinutes(1));
        response.AuthorizationResponse.Error = error;
        return NatsJwt.EncodeAuthorizationResponseClaims(response, accountKeyPair);
    }

    private async Task<KeyPair> ResolveAccountKeyPairAsync(IConfigurationService configurationService, CancellationToken ct)
    {
        var config = await configurationService.GetServerConfigAsync();
        var seed = _secretProtector.UnprotectOrSelf(config.NatsAccountSeed);
        if (string.IsNullOrWhiteSpace(seed))
            throw new InvalidOperationException("NATS account seed is not configured.");

        return KeyPair.FromSeed(seed);
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
