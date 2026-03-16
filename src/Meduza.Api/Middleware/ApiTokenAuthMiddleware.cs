using Meduza.Core.Interfaces.Auth;

namespace Meduza.Api.Middleware;

/// <summary>
/// Middleware que valida tokens de API por headers dedicados ou formato legado.
/// Novo formato recomendado: X-Api-Key: {tokenIdPublic} + X-Api-Secret: {accessKey}.
/// Formato legado suportado: Authorization: ApiKey {tokenIdPublic}.{accessKey}.
/// Quando bem-sucedido, define HttpContext.Items["UserId"] e HttpContext.Items["IsApiTokenAuth"] = true.
/// Executa antes do UserAuthMiddleware não sobrescrever o UserId (ApiKey tem prioridade se presente).
/// </summary>
public class ApiTokenAuthMiddleware
{
    private const string ApiKeyScheme = "ApiKey ";
    private const string ApiKeyHeader = "X-Api-Key";
    private const string ApiSecretHeader = "X-Api-Secret";
    private readonly RequestDelegate _next;

    public ApiTokenAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IApiTokenService tokenService)
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
