using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/search")]
[RequireUserAuth]
public class SearchController : ControllerBase
{
    private readonly ISearchService _searchService;

    public SearchController(ISearchService searchService)
    {
        _searchService = searchService;
    }

    /// <summary>
    /// Busca universal. Retorna resultados de Agents, Clients, Sites, Tickets e Software
    /// filtrados conforme as permissões de escopo do usuário autenticado.
    /// </summary>
    /// <param name="q">Termo de busca (mínimo 2 caracteres).</param>
    /// <param name="maxResults">Máximo de resultados por grupo (padrão: 10, máximo: 25).</param>
    /// <param name="ct">Token de cancelamento.</param>
    [HttpGet]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] int maxResults = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Ok(new UniversalSearchResult([], 0, DateTime.UtcNow));

        var userId = HttpContext.Items["UserId"] as Guid?;
        if (userId is null)
            return Unauthorized();

        var clampedMax = Math.Clamp(maxResults, 1, 25);
        var result = await _searchService.SearchAsync(userId.Value, q.Trim(), clampedMax, ct);
        return Ok(result);
    }
}
