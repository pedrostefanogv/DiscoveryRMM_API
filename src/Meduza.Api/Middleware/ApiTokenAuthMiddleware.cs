using Meduza.Core.Interfaces.Auth;

namespace Meduza.Api.Middleware;

/// <summary>
/// Middleware que valida tokens de API no formato "ApiKey {tokenIdPublic}.{accessKey}".
/// Quando bem-sucedido, define HttpContext.Items["UserId"] e HttpContext.Items["IsApiTokenAuth"] = true.
/// Executa antes do UserAuthMiddleware não sobrescrever o UserId (ApiKey tem prioridade se presente).
/// </summary>
public class ApiTokenAuthMiddleware
{
    private const string ApiKeyScheme = "ApiKey ";
    private readonly RequestDelegate _next;

    public ApiTokenAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IApiTokenService tokenService)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith(ApiKeyScheme, StringComparison.OrdinalIgnoreCase))
        {
            var rawKey = authHeader[ApiKeyScheme.Length..].Trim();
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
