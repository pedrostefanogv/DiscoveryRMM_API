using Asp.Versioning;
using Discovery.Core.Cqrs.AgentRegistration;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// Endpoint público de auto-registro de agent via deploy token.
/// Chamado pelo instalador/agent durante o bootstrap para obter credenciais.
/// O deploy token é enviado no header Authorization: Bearer &lt;token&gt;.
/// Não requer autenticação de usuário — a segurança é garantida pelo deploy token de uso único.
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/agent-register")]
public class AgentRegistrationController : ControllerBase
{
    private readonly IMediator _mediator;

    public AgentRegistrationController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Registra um novo agent usando um deploy token.
    /// O deploy token é consumido (uso único) e o agent recebe:
    /// - token: token mdz_ para autenticação nas chamadas /api/v1/agent-auth/*
    /// - agentId: ID do agent criado
    /// - clientId: ID do cliente ao qual o agent pertence
    /// - siteId: ID do site ao qual o agent pertence
    ///
    /// O agent é criado com ZeroTouchPending=true e precisa ser aprovado por um admin.
    ///
    /// Compatível com o formato enviado pelo agent (cmd, name, macAddress, notes, departmentId).
    /// O deploy token é extraído do header Authorization: Bearer &lt;token&gt;.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Register(
        [FromBody] AgentRegistrationRequest request,
        CancellationToken ct = default)
    {
        // Extrai o deploy token do header Authorization: Bearer <token>
        var authHeader = HttpContext.Request.Headers.Authorization.FirstOrDefault();
        var deployToken = (string?)null;

        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            deployToken = authHeader["Bearer ".Length..].Trim();
        }

        // Fallback: aceita deployToken no corpo (para compatibilidade futura)
        if (string.IsNullOrWhiteSpace(deployToken) && !string.IsNullOrWhiteSpace(request.DeployToken))
        {
            deployToken = request.DeployToken;
        }

        if (string.IsNullOrWhiteSpace(deployToken))
            return Unauthorized(new { error = "Deploy token ausente. Envie no header Authorization: Bearer <token> ou no corpo." });

        // Usa name do corpo (formato do agent) ou hostname (formato alternativo)
        var hostname = request.Name ?? request.Hostname ?? "unknown";

        var cmd = new RegisterAgentFromDeployTokenCommand(
            DeployToken: deployToken,
            Hostname: hostname,
            MacAddress: request.MacAddress,
            Notes: request.Notes
        );

        var result = await _mediator.Send(cmd, ct);
        return result.Match<IActionResult>(
            success: dto => Ok(new
            {
                token = dto.Token,
                agentId = dto.AgentId,
                clientId = dto.ClientId,
                siteId = dto.SiteId
            }),
            failure: errors => errors[0].Code switch
            {
                "NotFound" => NotFound(new { error = errors[0].Message }),
                "Unauthorized" => Unauthorized(new { error = errors[0].Message }),
                _ => BadRequest(new { error = errors[0].Message })
            });
    }
}

/// <summary>
/// Request body para o endpoint de auto-registro de agent.
/// Compatível com o formato enviado pelo agent (cmd, name, macAddress, notes, departmentId)
/// e também com o formato alternativo (hostname, deployToken no corpo).
/// </summary>
public sealed record AgentRegistrationRequest(
    // Formato do agent (installer.go)
    string? Cmd = null,
    string? Name = null,
    string? MacAddress = null,
    string? DepartmentId = null,
    string? Notes = null,
    // Formato alternativo
    string? Hostname = null,
    string? DeployToken = null
);
