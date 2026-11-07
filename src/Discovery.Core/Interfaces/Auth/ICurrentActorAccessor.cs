namespace Discovery.Core.Interfaces.Auth;

/// <summary>
/// Identidade do ator da operação em curso (quem está executando agora).
///
/// Existe para que a camada de Infrastructure (que NÃO referencia ASP.NET Core,
/// por decisão de arquitetura) consiga auditar mutações de identidade sem depender
/// de <c>IHttpContextAccessor</c>. A implementação lê de HttpContext.Items na API.
/// </summary>
public interface ICurrentActorAccessor
{
    /// <summary>Id do usuário autenticado; Guid.Empty quando não há sessão.</summary>
    Guid UserId { get; }

    /// <summary>Login do usuário autenticado; null quando indisponível.</summary>
    string? Login { get; }
}
