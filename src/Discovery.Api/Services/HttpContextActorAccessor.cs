using Discovery.Core.Interfaces.Auth;

namespace Discovery.Api.Services;

/// <summary>
/// Lê o ator da operação de <see cref="HttpContext"/> (populado por
/// <c>UserAuthMiddleware</c> a partir do JWT).
/// </summary>
public sealed class HttpContextActorAccessor(IHttpContextAccessor accessor) : ICurrentActorAccessor
{
    public Guid UserId
        => accessor.HttpContext?.Items["UserId"] is Guid id ? id : Guid.Empty;

    public string? Login
        => accessor.HttpContext?.Items["Username"] as string;
}
