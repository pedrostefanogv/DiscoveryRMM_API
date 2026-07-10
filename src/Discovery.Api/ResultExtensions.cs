using Discovery.Core.Cqrs;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api;

/// <summary>
/// Extension methods to convert CQRS Result&lt;T&gt; into standardized IActionResult responses.
/// </summary>
public static class ResultExtensions
{
    /// <summary>
    /// Converts a Result&lt;T&gt; to IActionResult with standardized error format:
    /// { "errors": [{ "code": "...", "message": "..." }] }
    /// </summary>
    public static IActionResult ToActionResult<T>(this Result<T> result)
        where T : notnull
    {
        return result.Match<IActionResult>(
            success: value => new OkObjectResult(value),
            failure: errors =>
            {
                var first = errors[0];
                var errorList = errors.Select(e => new { e.Code, e.Message, e.Field }).ToList();

                return first.Code switch
                {
                    "NotFound" => new NotFoundObjectResult(new { errors = errorList }),
                    "Validation" => new BadRequestObjectResult(new { errors = errorList }),
                    "Unauthorized" => new UnauthorizedObjectResult(new { errors = errorList }),
                    "Forbidden" => new ObjectResult(new { errors = errorList }) { StatusCode = 403 },
                    _ => new BadRequestObjectResult(new { errors = errorList })
                };
            });
    }

    /// <summary>
    /// Converts a Result&lt;T&gt; to IActionResult with CreatedAtAction on success.
    /// </summary>
    public static IActionResult ToCreatedAtActionResult<T>(
        this Result<T> result,
        string actionName,
        object routeValues,
        ControllerBase controller)
        where T : notnull
    {
        return result.Match<IActionResult>(
            success: value => controller.CreatedAtAction(actionName, routeValues, value),
            failure: errors =>
            {
                var errorList = errors.Select(e => new { e.Code, e.Message, e.Field }).ToList();
                return new BadRequestObjectResult(new { errors = errorList });
            });
    }
}
