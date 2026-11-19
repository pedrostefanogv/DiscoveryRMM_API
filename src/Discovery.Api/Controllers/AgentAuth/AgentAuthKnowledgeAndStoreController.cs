using Discovery.Core.Cqrs.AgentAuth.Knowledge;
using Discovery.Core.Cqrs.AgentAuth.Misc;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

public class AgentAuthKnowledgeAndStoreController : AgentAuthControllerBase
{
    private readonly ISiteRepository _siteRepo;
    private readonly IAttachmentService _attachmentService;
    private readonly IAttachmentRepository _attachmentRepository;
    private readonly IKnowledgeArticleRepository _knowledgeArticleRepository;

    public AgentAuthKnowledgeAndStoreController(
        IMediator mediator,
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IAttachmentService attachmentService,
        IAttachmentRepository attachmentRepository,
        IKnowledgeArticleRepository knowledgeArticleRepository)
        : base(mediator, agentRepo)
    {
        _siteRepo = siteRepo;
        _attachmentService = attachmentService;
        _attachmentRepository = attachmentRepository;
        _knowledgeArticleRepository = knowledgeArticleRepository;
    }

    // ── Knowledge ─────────────────────────────────────────────────────────

    [HttpGet("knowledge")]
    public async Task<IActionResult> GetKnowledgeArticles([FromQuery] string? category = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        // Paginação keyset: cursor opaco da página anterior (hasMore/nextCursor no envelope).
        return MapResult(await _mediator.Send(new GetKnowledgeArticlesQuery(id, category, cursor, limit), ct), Ok);
    }

    [HttpGet("knowledge/{articleId:guid}")]
    public async Task<IActionResult> GetKnowledgeArticle(Guid articleId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetKnowledgeArticleQuery(id, articleId), ct), Ok);
    }

    [HttpGet("knowledge/{articleId:guid}/pages")]
    public async Task<IActionResult> GetKnowledgeArticlePages(Guid articleId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetKnowledgeArticlePagesQuery(id, articleId), ct), Ok);
    }

    /// <summary>
    /// Bytes de uma imagem de artigo (o webview não tem o token do agente, então
    /// o download acontece no processo Go, que devolve data URL à UI).
    ///
    /// Diferente do anexo de chamado (isolado por Ticket.AgentId), aqui a
    /// autorização é a VISIBILIDADE DO ARTIGO: o agente só alcança imagem de
    /// artigo PUBLICADO no seu escopo (site → client → global), a mesma regra da
    /// listagem de artigos. Sem isso o id do anexo vira uma URL pública de fato.
    /// </summary>
    [HttpGet("knowledge/images/{attachmentId:guid}")]
    public async Task<IActionResult> GetKnowledgeImage(Guid attachmentId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (agent is null) return NotFound();

        var attachment = await _attachmentRepository.GetByIdAsync(attachmentId, ct);
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, KnowledgeImageRules.EntityType, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        // Leitura leve: aqui só importam status/escopo, não os chunks/embeddings.
        var article = await _knowledgeArticleRepository.GetWithoutChunksAsync(attachment.EntityId, ct);
        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        if (!KnowledgeArticleScope.IsVisibleToAgent(article, site?.ClientId, agent.SiteId))
            return NotFound();

        try
        {
            var stream = await _attachmentService.DownloadAttachmentAsync(attachmentId, ct);
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;

            Response.Headers["Cache-Control"] = "private, max-age=300";
            // Content-Type declarado no upload: nosniff impede interpretação
            // alternativa (ex.: poliglota servido como HTML).
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, contentType);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Não foi possível baixar a imagem." });
        }
    }

    // ── App Store ─────────────────────────────────────────────────────────

    /// <summary>
    /// Política efetiva da app-store para o agent. Quando <paramref name="paged"/>
    /// é true a resposta é paginada (pageSize + cursor opaco + hasMore/nextCursor);
    /// sem ele, mantém-se a resposta única com a coleção completa para os agents
    /// já instalados.
    /// </summary>
    [HttpGet("me/app-store")]
    public async Task<IActionResult> GetAppStoreEffective(
        [FromQuery] string installationType = "Winget",
        [FromQuery] bool paged = false,
        [FromQuery] int pageSize = 0,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAppStoreEffectiveQuery(id, installationType, paged, pageSize, cursor), ct), Ok);
    }

    /// <summary>
    /// Baixa o instalador de um pacote custom publicado pelo servidor. Exige o
    /// token do agent, que o pacote exista no catalogo E que esteja liberado
    /// pela politica da loja para o escopo do agent (sem isso qualquer agent
    /// autenticado baixaria o instalador de um pacote nao aprovado).
    /// </summary>
    [HttpGet("me/app-store/custom/{packageId}/file")]
    public async Task<IActionResult> DownloadCustomAppFile(
        string packageId,
        [FromServices] IAppPackageRepository appPackages,
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IAppStoreService appStoreService,
        CancellationToken ct = default)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        if (agent is null) return Unauthorized();

        var package = await appPackages.GetByInstallationTypeAndPackageIdAsync(
            AppInstallationType.Custom, packageId, cancellationToken: ct);

        if (package is null || string.IsNullOrWhiteSpace(package.FileObjectKey))
            return NotFound(new { message = "Instalador nao encontrado para este pacote custom." });

        var site = await _siteRepo.GetByIdAsync(agent.SiteId);
        var approved = await appStoreService.IsPackageApprovedAsync(
            site?.ClientId, agent.SiteId, agent.Id, AppInstallationType.Custom, package.PackageId, ct);

        if (!approved)
            return NotFound(new { message = "Pacote custom nao aprovado para este agent." });

        try
        {
            // Stream direto para o corpo da resposta: o arquivo pode ter GBs e
            // NÃO pode ser bufferizado em memória (causava OOM kill da API).
            var contentType = string.IsNullOrWhiteSpace(package.FileContentType)
                ? "application/octet-stream"
                : package.FileContentType;
            var fileName = ResolveCustomInstallerFileName(package.FileName, package.FileObjectKey, package.PackageId);

            Response.ContentType = contentType;
            Response.Headers["Content-Disposition"] =
                $"attachment; filename=\"{fileName.Replace("\"", string.Empty)}\"";
            Response.Headers["Cache-Control"] = "no-store";

            await assets.WriteToAsync(package.FileObjectKey, Response.Body, ct);
            return new EmptyResult();
        }
        catch (Exception) when (!Response.HasStarted)
        {
            // O Content-Type/Content-Disposition do instalador JÁ foram aplicados
            // à resposta acima. Sem desfazê-los, o MVC tenta escrever o JSON deste
            // 404 nesse mesmo media type, não encontra output formatter e troca o
            // status por 406 sem corpo — o agent reportava só "HTTP 406 Not
            // Acceptable" e a causa real (objeto ausente no storage) ficava
            // invisível tanto para o operador quanto no log.
            Response.Headers.Remove("Content-Type");
            Response.Headers.Remove("Content-Disposition");
            Response.Headers.Remove("Cache-Control");
            return NotFound(new { message = "Arquivo nao encontrado no object storage." });
        }
    }

    /// <summary>
    /// Resolve o nome do instalador enviado ao agent. Pacotes custom sem
    /// <c>file_name</c> cadastrado caíam no fallback pelo packageId, devolvendo
    /// um arquivo SEM extensão — e o agent não consegue executá-lo (`cmd /C`
    /// recusa binário sem extensão, mesmo sendo um PE válido). A extensão do
    /// objeto no storage é usada como pista (o upload já valida a extensão) e o
    /// packageId só permanece cru quando não há nenhuma informação.
    /// </summary>
    internal static string ResolveCustomInstallerFileName(string? fileName, string? fileObjectKey, string packageId)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(Path.GetExtension(name)))
            return name;

        var baseName = string.IsNullOrWhiteSpace(name) ? packageId : name;
        var keyExtension = Path.GetExtension(fileObjectKey ?? string.Empty);
        return string.IsNullOrWhiteSpace(keyExtension) ? baseName : baseName + keyExtension;
    }
}
