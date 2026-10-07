using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Push;
using Discovery.Core.Cqrs.Push.Commands;
using Discovery.Core.Cqrs.Push.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Inscricoes de Web Push (notificacoes do navegador) do usuario autenticado.
/// O destinatario e SEMPRE o usuario do token — nunca o corpo da requisicao.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/push")]
public class PushController(IMediator mediator, IWebPushSender webPushSender) : ControllerBase
{
    private Guid CurrentUserId => HttpContext.Items["UserId"] is Guid id ? id : Guid.Empty;

    /// <summary>Estado do Web Push e inscricoes do usuario (a chave publica VAPID vai aqui).</summary>
    [HttpGet]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> GetStatus()
        => (await mediator.Send(new GetPushStatusQuery(CurrentUserId), HttpContext.RequestAborted))
            .ToActionResult();

    /// <summary>Registra (ou reaponta) a inscricao deste navegador para o usuario autenticado.</summary>
    [HttpPost("subscriptions")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> Register([FromBody] RegisterPushSubscriptionRequest request)
    {
        var headerUserAgent = Request.Headers.UserAgent.ToString();
        var userAgent = string.IsNullOrWhiteSpace(headerUserAgent) ? request.UserAgent : headerUserAgent;

        var result = await mediator.Send(
            new RegisterPushSubscriptionCommand(
                CurrentUserId, request.Endpoint, request.P256dh, request.Auth, userAgent),
            HttpContext.RequestAborted);

        return result.ToActionResult();
    }

    /// <summary>Remove a inscricao deste navegador (idempotente).</summary>
    [HttpDelete("subscriptions")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> Unregister([FromQuery] string? endpoint)
        => (await mediator.Send(
                new DeletePushSubscriptionCommand(CurrentUserId, endpoint ?? string.Empty),
                HttpContext.RequestAborted))
            .ToActionResult();

    /// <summary>Envia um push de teste para as inscricoes do usuario autenticado.</summary>
    [HttpPost("test")]
    [RequirePermission(ResourceType.Dashboard, ActionType.View)]
    public async Task<IActionResult> SendTest()
    {
        if (!webPushSender.IsConfigured)
        {
            return BadRequest(new
            {
                errors = new[] { new { code = "PushDisabled", message = "Web Push nao esta habilitado no servidor." } }
            });
        }

        var dispatch = await webPushSender.SendToUserAsync(
            CurrentUserId,
            new WebPushMessage(
                "Discovery RMM",
                "Notificacao de teste do navegador.",
                Severity: "Informational",
                Topic: "push.test",
                EventType: "push.test"),
            HttpContext.RequestAborted);

        return Ok(new
        {
            attempted = dispatch.Attempted,
            delivered = dispatch.Delivered,
            failed = dispatch.Failed,
            removed = dispatch.Removed
        });
    }
}
