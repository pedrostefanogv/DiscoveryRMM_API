namespace Discovery.Api.Middleware;

/// <summary>
/// Adiciona headers HTTP defensivos para reduzir superfície de ataque no browser.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        context.Response.OnStarting(() =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isDocsPath = path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase);

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            if (!isDocsPath && configuration.GetValue("Security:Headers:EnableCsp", true))
            {
                var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                context.Items["CspNonce"] = nonce;
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none';" +
                    " base-uri 'none';" +
                    " frame-ancestors 'none';" +
                    " form-action 'none';" +
                    " connect-src 'self' ws: wss:;" +
                    $" script-src 'nonce-{nonce}';" +
                    " style-src 'self' 'unsafe-inline';" +
                    " img-src 'self' data: blob:;" +
                    " font-src 'self';";
            }

            context.Response.Headers.Remove("Server");
            return Task.CompletedTask;
        });

        await _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
