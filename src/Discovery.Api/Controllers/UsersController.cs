using Discovery.Core.Cqrs.Users.Commands;
using Discovery.Core.Cqrs.Users.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/users")]
public class UsersController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListUsersQuery(cursor, limit));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetUserByIdQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteUserCommand(id));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        if (HttpContext.Items["UserId"] is Guid userId)
        {
            var result = await mediator.Send(new GetUserByIdQuery(userId));
            return result.Match<IActionResult>(
                success: Ok,
                failure: errors => errors[0].Code == "NotFound"
                    ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                    : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
        }
        return Unauthorized(new { error = "Not authenticated." });
    }
}
