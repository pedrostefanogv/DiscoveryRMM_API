using Discovery.Core.Cqrs.Mfa;
using Discovery.Core.Cqrs.Mfa.Queries;
using Discovery.Core.DTOs.Mfa;
using Discovery.Api.Filters;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/mfa")]
public class MfaController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetKeys([FromQuery] Guid userId)
    {
        var result = await mediator.Send(new ListMfaKeysQuery(userId));
        return result.ToActionResult();
    }

    [HttpGet("keys")]
    public async Task<IActionResult> GetMyKeys()
    {
        if (HttpContext.Items["UserId"] is Guid userId)
        {
            var result = await mediator.Send(new ListMfaKeysQuery(userId));
            return result.ToActionResult();
        }
        return Unauthorized(new { error = "Not authenticated." });
    }

    /// <summary>
    /// Inicia o registro de uma nova chave FIDO2.
    /// Requiere token mfa_setup o sesión completa.
    /// </summary>
    [HttpPost("fido2/register/begin")]
    [AllowAnonymous]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> BeginFido2Registration()
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "Not authenticated." });

        var result = await mediator.Send(new BeginFido2RegistrationQuery(userId));
        return result.Match<IActionResult>(
            success: r => Ok(new { options = r.OptionsJson }),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Completa el registro de chave FIDO2 y la persiste.
    /// Requiere token mfa_setup o sesión completa.
    /// </summary>
    [HttpPost("fido2/register/complete")]
    [AllowAnonymous]
    [RequireMfaSetupOrFullSession]
    public async Task<IActionResult> CompleteFido2Registration([FromBody] CompleteFido2RegistrationDto dto)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "Not authenticated." });

        var cmd = new CompleteFido2RegistrationCommand(userId, dto.AttestationResponseJson, dto.KeyName);
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: r => Ok(new { keyId = r.KeyId, message = r.Message }),
            failure: errors => errors[0].Code switch
            {
                "Unauthorized" => Unauthorized(new { errors = errors.Select(e => new { e.Code, e.Message }) }),
                _ => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) })
            });
    }
}
