using Discovery.Api;
using Discovery.Core.Cqrs.NatsAuth.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Api.Filters;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/nats-auth")]
public class NatsAuthController(
    IMediator mediator,
    INatsCredentialsService natsCredentials,
    IScopeContext scopeContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetStatus()
    {
        var result = await mediator.Send(new GetNatsStatusQuery());
        return result.ToActionResult();
    }

    /// <summary>
    /// Emite credenciais NATS escopadas para o USUARIO logado (JWT + nkey seed),
    /// usadas pelo portal para conectar no WebSocket do NATS (dashboard, notificacoes,
    /// sessoes remotas).
    ///
    /// BUG 2026-11-21 (tupa.pstefano.com.br): esta rota NAO existia. O portal
    /// chama POST /api/v1/nats-auth/user/credentials desde sempre
    /// (DiscoveryRMM_Site/src/api/nats.ts) e recebia 404 — resultado: o badge do
    /// dashboard mostrava "Falha de rede NATS"/"Offline" mesmo com o broker, o
    /// nginx e o tunnel saudaveis e com o painel "Testar" respondendo "Conectado".
    /// O servico (INatsCredentialsService.IssueForUserAsync) ja existia e era
    /// usado pelos AGENTES e por sessoes remotas; faltava apenas expor ao usuario.
    ///
    /// O escopo (client/site/global) vem do MESMO IScopeContext que as demais
    /// rotas usam — nunca do corpo da requisicao — para que o usuario nao possa
    /// se conceder subjects de outro tenant.
    /// </summary>
    [HttpPost("user/credentials")]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> IssueUserCredentials(
        [FromBody] NatsCredentialsRequest? request,
        CancellationToken ct)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { message = "Autenticação necessária." });

        var access = await scopeContext.GetAccessAsync(ResourceType.Dashboard, ActionType.View);

        var response = await natsCredentials.IssueForUserAsync(
            userId,
            access,
            scopeContext.ResolvedClientId,
            scopeContext.ResolvedSiteId,
            ct);

        return Ok(response);
    }
}
