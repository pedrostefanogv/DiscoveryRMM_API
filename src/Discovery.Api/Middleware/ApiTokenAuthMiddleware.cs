using Discovery.Core.Interfaces.Auth;

namespace Discovery.Api.Middleware;

/// <summary>
/// Middleware que valida tokens de API por headers dedicados ou formato legado.
/// Novo formato recomendado: X-Api-Key: {tokenIdPublic} + X-Api-Secret: {accessKey}.
/// Formato legado suportado: Authorization: ApiKey {tokenIdPublic}.{accessKey}.
/// Quando bem-sucedido, define HttpContext.Items["UserId"] e HttpContext.Items["IsApiTokenAuth"] = true.
/// Verifica se o usuario tem MFA configurado (quando exigido pelas roles).
/// Executa antes do UserAuthMiddleware não sobrescrever o UserId (ApiKey tem prioridade se presente).
/// </summary>
public class ApiTokenAuthMiddleware
{
    private const string ApiKeyScheme = "ApiKey ";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string ApiSecretHeader = "X-Api-Secret";
    private readonly RequestDelegate _next;

    public ApiTokenAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IApiTokenService tokenService, IUserAuthService userAuthService)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        var hasApiKeyHeader = context.Request.Headers.TryGetValue(ApiKeyHeader, out var apiKeyValues);
        var hasApiSecretHeader = context.Request.Headers.TryGetValue(ApiSecretHeader, out var apiSecretValues);

        string? rawKey = null;

        if (hasApiKeyHeader || hasApiSecretHeader)
        {
            var apiKey = apiKeyValues.FirstOrDefault()?.Trim();
            var apiSecret = apiSecretValues.FirstOrDefault()?.Trim();

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Missing API key headers. Expected X-Api-Key and X-Api-Secret." });
                return;
            }

            rawKey = $"{apiKey}.{apiSecret}";
        }
        else if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith(ApiKeyScheme, StringComparison.OrdinalIgnoreCase))
        {
            rawKey = authHeader[ApiKeyScheme.Length..].Trim();
        }

        if (!string.IsNullOrWhiteSpace(rawKey))
        {
            var userId = await tokenService.AuthenticateAsync(rawKey);
            if (userId.HasValue)
            {
                var (canUse, reason) = await userAuthService.CanUseApiTokensAsync(userId.Value);
                if (!canUse)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = reason ?? "API token access denied." });
                    return;
                }

                context.Items["UserId"] = userId.Value;
                context.Items["IsApiTokenAuth"] = true;
            }
            else
            {
                // Chave inválida — retorna 401 diretamente
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired API key." });
                return;
            }
        }

        await _next(context);
    }
}

public static class ApiTokenAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseApiTokenAuth(this IApplicationBuilder app)
        => app.UseMiddleware<ApiTokenAuthMiddleware>();
}
