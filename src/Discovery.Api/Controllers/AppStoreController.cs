using Discovery.Api.Filters;
using Discovery.Core.Cqrs.AppStore.Commands;
using Discovery.Core.Cqrs.AppStore.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;
using Discovery.Api.Services;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/app-store")]
public class AppStoreController(
    IMediator mediator,
    ILogger<AppStoreController> _logger) : ControllerBase
{
    /// <summary>
    /// Usuário autenticado (populado pelo UserAuthMiddleware). O autor da regra
    /// de aprovação vem SEMPRE daqui — nunca do corpo/query, que seriam
    /// falsificáveis — para a trilha de auditoria registrar quem realmente fez.
    /// </summary>
    private string Username =>
        HttpContext.Items["Username"] as string
        ?? HttpContext.Items["UserId"]?.ToString()
        ?? "api";

    /// <summary>IP de origem da requisição, para a trilha de auditoria.</summary>
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    // ═══════════════════════════════════════════════════════════════
    // Catalog
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Search/browse catalog by installation type (0=Winget, 1=Chocolatey, 2=Custom).</summary>
    [HttpGet]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> Search(
        [FromQuery] int installationType = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? architecture = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new SearchAppStoreQuery(
            (AppInstallationType)installationType, search, architecture, cursor, limit));
        return result.ToActionResult();
    }

    /// <summary>List catalog with pagination by cursor.</summary>
    [HttpGet("catalog")]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> GetCatalog(
        [FromQuery] int installationType = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? architecture = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new GetAppStoreCatalogQuery(installationType, search, architecture, cursor, limit));
        return result.ToActionResult();
    }

    /// <summary>Get a single package by ID and installation type.</summary>
    [HttpGet("catalog/{packageId}")]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> GetPackageById(
        string packageId,
        [FromQuery] int installationType = 0)
    {
        var result = await mediator.Send(new GetCatalogPackageByIdQuery(installationType, packageId));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    // ═══════════════════════════════════════════════════════════════
    // Custom packages (CRUD)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Create or update a custom package (InstallationType=Custom).</summary>
    [HttpPost("custom")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> UpsertCustomPackage([FromBody] UpsertCustomAppPackageCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>
    /// Envia o instalador ou o icone/capa de um pacote custom para o object storage.
    /// Multipart/form-data com o arquivo no campo "file" e ?kind=installer|icon.
    /// </summary>
    [HttpPost("custom/assets")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    [RequestSizeLimit(5_368_709_120)]
    [RequestFormLimits(MultipartBodyLengthLimit = 5_368_709_120)]
    public async Task<IActionResult> UploadCustomAsset(
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IRedisService redis,
        [FromForm] IFormFile file,
        [FromQuery] string kind = "installer",
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { errors = new[] { new { Code = "FileRequired", Message = "Arquivo é obrigatório." } } });

        if (file.Length > CustomPackageAssetService.MaxAssetBytes)
            return BadRequest(new { errors = new[] { new { Code = "FileTooLarge", Message = $"Arquivo excede o limite de {CustomPackageAssetService.MaxAssetBytes / (1024L * 1024 * 1024)} GiB." } } });

        try
        {
            await using var stream = file.OpenReadStream();
            var asset = await assets.UploadAsync(kind, file.FileName, file.ContentType, stream, ct);

            // Este caminho (arquivo pequeno, corpo único) também publica um objeto
            // no storage. Registra o índice "concluído" para o serviço de limpeza
            // poder descartá-lo caso nunca seja vinculado a um pacote — antes esses
            // arquivos ficavam órfãos para sempre (não tinham sessão/índice).
            await MultipartUploadSessionStore.SaveCompletedIndexAsync(
                redis, $"single-{Guid.NewGuid():N}", asset.ObjectKey);

            return Ok(asset);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    /// <summary>
    /// Abre uma sessão de upload em PARTES (blocos de 10 MiB, padronizado).
    /// Necessário para instaladores grandes: o Cloudflare recusa no edge um corpo
    /// único de vários GiB (413), então o cliente envia bloco a bloco e a API
    /// remonta o arquivo antes de publicar no object storage.
    /// </summary>
    [HttpPost("custom/assets/session")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public IActionResult CreateCustomAssetUploadSession([FromBody] CustomAssetUploadRequest request)
    {
        var kind = (request?.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind is not ("installer" or "icon"))
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = "kind deve ser 'installer' ou 'icon'." } } });
        if (string.IsNullOrWhiteSpace(request!.FileName))
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = "fileName é obrigatório." } } });
        if (request.SizeBytes <= 0)
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = "sizeBytes deve ser maior que zero." } } });
        if (request.SizeBytes > CustomPackageAssetService.MaxAssetBytes)
            return BadRequest(new { errors = new[] { new { Code = "FileTooLarge", Message = $"Arquivo excede o limite de {CustomPackageAssetService.MaxAssetBytes / (1024L * 1024 * 1024)} GiB." } } });

        PruneCustomAssetUploads();
        var uploadId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(CustomAssetUploadsRoot, uploadId);
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "payload.bin");
        using (System.IO.File.Create(filePath)) { }

        CustomAssetUploads[uploadId] = new CustomAssetUploadSession
        {
            Kind = kind,
            FileName = request.FileName!,
            ContentType = string.IsNullOrWhiteSpace(request.ContentType)
                ? "application/octet-stream"
                : request.ContentType!,
            SizeBytes = request.SizeBytes,
            FilePath = filePath,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        return Ok(new
        {
            uploadId,
            partSizeBytes = CustomAssetPartBytes,
            totalParts = (int)((request.SizeBytes + CustomAssetPartBytes - 1) / CustomAssetPartBytes),
        });
    }

    /// <summary>Recebe um bloco do upload em partes (corpo binário cru, até 64 MiB).</summary>
    [HttpPut("custom/assets/session/{uploadId}/part/{index:int}")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    [RequestSizeLimit(CustomAssetPartLimitBytes)]
    public async Task<IActionResult> UploadCustomAssetPart(string uploadId, int index, CancellationToken ct)
    {
        if (!CustomAssetUploads.TryGetValue(uploadId, out var session))
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sessão de upload não encontrada ou expirada." } } });

        var offset = (long)index * CustomAssetPartBytes;
        if (index < 0 || offset >= session.SizeBytes)
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = $"Índice de bloco inválido: {index}." } } });

        var expected = Math.Min(CustomAssetPartBytes, session.SizeBytes - offset);
        await using (var target = new FileStream(session.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            target.Seek(offset, SeekOrigin.Begin);
            var copied = await CopyAtMostAsync(Request.Body, target, expected, ct);
            if (copied != expected)
                return BadRequest(new { errors = new[] { new { Code = "PartSizeMismatch", Message = $"Bloco {index} com {copied} bytes; esperado {expected}." } } });
        }

        return Ok(new { uploadId, index, bytes = expected });
    }

    /// <summary>Finaliza o upload em partes e publica o asset no object storage.</summary>
    [HttpPost("custom/assets/session/{uploadId}/complete")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> CompleteCustomAssetUpload(
        string uploadId,
        [FromServices] ICustomPackageAssetService assets,
        CancellationToken ct)
    {
        if (!CustomAssetUploads.TryRemove(uploadId, out var session))
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sessão de upload não encontrada ou expirada." } } });

        try
        {
            var info = new FileInfo(session.FilePath);
            if (!info.Exists || info.Length != session.SizeBytes)
                return BadRequest(new { errors = new[] { new { Code = "IncompleteUpload", Message = $"Upload incompleto: {info.Length} de {session.SizeBytes} bytes." } } });

            await using var stream = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var asset = await assets.UploadAsync(session.Kind, session.FileName, session.ContentType, stream, ct);
            return Ok(asset);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
        finally
        {
            TryDeleteCustomAssetUpload(session.FilePath);
        }
    }

    /// <summary>Aborta uma sessão de upload em partes e limpa o temporário.</summary>
    [HttpDelete("custom/assets/session/{uploadId}")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public IActionResult AbortCustomAssetUpload(string uploadId)
    {
        if (CustomAssetUploads.TryRemove(uploadId, out var session))
            TryDeleteCustomAssetUpload(session.FilePath);
        return NoContent();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Upload MULTIPART DIRETO no storage (RustFS/S3) — instaladores grandes
    // ═══════════════════════════════════════════════════════════════════
    //
    // O arquivo NÃO passa pela API: ela cria o multipart no storage, pré-assina
    // cada bloco de 10 MiB e o navegador faz PUT direto na URL assinada. Isso é
    // o que permite atravessar o Cloudflare (corpo pequeno por requisição) e
    // RETOMAR o envio de onde parou (o storage guarda as partes até o complete).

    /// <summary>Cria o multipart upload direto no storage.</summary>
    [HttpPost("custom/assets/multipart")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> CreateCustomAssetMultipartUpload(
        [FromBody] CustomAssetMultipartRequest request,
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IRedisService redis,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = "Corpo da requisição é obrigatório." } } });

        try
        {
            var session = await assets.BeginMultipartUploadAsync(
                request.Kind ?? "installer",
                request.FileName ?? string.Empty,
                request.ContentType ?? "application/octet-stream",
                request.SizeBytes,
                ct);

            await SaveMultipartSessionAsync(redis, session, CurrentUserId);

            return Ok(new
            {
                uploadId = session.UploadId,
                objectKey = session.ObjectKey,
                partSizeBytes = session.PartSizeBytes,
                totalParts = (int)((session.SizeBytes + session.PartSizeBytes - 1) / session.PartSizeBytes),
                partUrlTtlSeconds = session.PartUrlTtlMinutes * 60,
                fileName = session.FileName,
                sizeBytes = session.SizeBytes,

                // O cliente pode ADAPTAR o tamanho do bloco no meio do envio (mede
                // a velocidade no primeiro bloco e cresce até o teto). O complete
                // valida por regra do S3, não por tamanho fixo — sem este aviso um
                // cliente novo enviaria blocos de tamanhos diferentes para um
                // servidor antigo e o complete seria recusado.
                supportsAdaptiveParts = true,
                minPartSizeBytes = CustomPackageAssetService.MinPartBytes,
                maxPartSizeBytes = CustomPackageAssetService.MaxPartBytes,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { errors = new[] { new { Code = "Unsupported", Message = ex.Message } } });
        }
    }

    /// <summary>
    /// Estado do upload + partes já recebidas pelo storage. Usado para RETOMAR
    /// um envio interrompido (o storage é a fonte de verdade das partes).
    /// </summary>
    [HttpGet("custom/assets/multipart/{uploadId}")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> GetCustomAssetMultipartStatus(
        string uploadId,
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IRedisService redis,
        CancellationToken ct)
    {
        var session = await LoadOwnedMultipartSessionAsync(redis, uploadId);
        if (session is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sessão de upload não encontrada ou expirada." } } });

        if (session.Completed)
        {
            // O multipart não existe mais no storage (foi finalizado): devolver o
            // estado salvo evita um erro de ListParts na retomada.
            return Ok(BuildStatusResponse(session, session.Parts, completed: true));
        }

        var received = await assets.ListUploadedPartsAsync(session.ObjectKey, session.UploadId, ct);
        // Uso = sessão viva: renova o prazo (um envio longo não pode expirar no meio).
        await RenewMultipartSessionAsync(redis, session, ct);

        return Ok(BuildStatusResponse(session, received));
    }

    /// <summary>
    /// Devolve URLs pré-assinadas para os blocos pedidos (PUT direto no storage).
    /// Blocos já recebidos podem ser reconferidos com GET do estado.
    /// </summary>
    [HttpPost("custom/assets/multipart/{uploadId}/parts")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> GetCustomAssetMultipartPartUrls(
        string uploadId,
        [FromBody] CustomAssetPartUrlRequest? request,
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IRedisService redis,
        CancellationToken ct)
    {
        var session = await LoadOwnedMultipartSessionAsync(redis, uploadId);
        if (session is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sessão de upload não encontrada ou expirada." } } });

        if (session.Completed)
        {
            // O multipart já não existe no storage: assinar blocos dele só faria o
            // navegador receber 404 em cada PUT. Devolve o estado para o cliente
            // pular direto para o complete (idempotente).
            return Conflict(new { errors = new[] { new { Code = "AlreadyCompleted", Message = "Este upload já foi concluído: finalize o vínculo salvando o pacote." } } });
        }

        var totalParts = (int)((session.SizeBytes + session.PartSizeBytes - 1) / session.PartSizeBytes);
        var requested = (request?.PartNumbers is { Count: > 0 })
            ? request.PartNumbers.Distinct().OrderBy(x => x).ToArray()
            : Enumerable.Range(1, totalParts).ToArray();

        if (requested.Any(part => part < 1 || part > totalParts))
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = $"Número de bloco fora do intervalo 1..{totalParts}." } } });

        try
        {
            var urls = new List<MultipartUploadPartUrlDto>(requested.Length);
            foreach (var part in requested)
                urls.Add(await assets.GetUploadPartUrlAsync(session.ObjectKey, session.UploadId, part, ct));

            // Uso = sessão viva: renova o prazo (item D).
            await RenewMultipartSessionAsync(redis, session, ct);

            return Ok(new { uploadId, totalParts, parts = urls });
        }
        catch (InvalidOperationException ex)
        {
            // Ex.: endpoint do storage em loopback — o cliente recebe a orientação
            // do que configurar, não um 500 genérico.
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
        catch (NotSupportedException ex)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { errors = new[] { new { Code = "Unsupported", Message = ex.Message } } });
        }
    }

    /// <summary>
    /// Finaliza o multipart no storage e devolve o asset pronto (mesmo formato do
    /// upload único). Sem ETags no corpo, a API usa as partes que o storage tem.
    /// </summary>
    [HttpPost("custom/assets/multipart/{uploadId}/complete")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> CompleteCustomAssetMultipartUpload(
        string uploadId,
        [FromBody] CustomAssetMultipartCompleteRequest? request,
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IRedisService redis,
        CancellationToken ct)
    {
        var session = await LoadOwnedMultipartSessionAsync(redis, uploadId);
        if (session is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sessão de upload não encontrada ou expirada." } } });

        MultipartUploadPartStateDto[] parts;

        // Sessão concluída mas sem as partes gravadas (falha ao salvar a sessão):
        // se o objeto está no storage, a resposta correta é o asset — e não um 400
        // por "blocos faltando" numa lista local incompleta.
        if (session.Completed && session.Parts.Count == 0)
        {
            var described = await DescribeCompletedIfPublishedAsync(assets, session, ct);
            if (described is not null)
            {
                await MarkCompletedAsync(redis, session, [], described.Checksum, ct);
                return Ok(described);
            }
        }

        try
        {
            if (session.Completed && session.Parts.Count > 0)
            {
                // O estado gravado na conclusão é a verdade — partes que o cliente
                // mandou após a conclusão podem estar desatualizadas.
                parts = session.Parts.ToArray();
            }
            else if (request?.Parts is { Count: > 0 })
            {
                parts = request.Parts
                    .Select(part => new MultipartUploadPartStateDto(part.PartNumber, NormalizeETag(part.ETag), part.SizeBytes))
                    .ToArray();
            }
            else if (session.Parts.Count > 0)
            {
                parts = session.Parts.ToArray();
            }
            else
            {
                parts = (await assets.ListUploadedPartsAsync(session.ObjectKey, session.UploadId, ct)).ToArray();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // O multipart não existe mais no storage (upload já finalizado antes):
            // se o objeto está lá, a resposta correta é o asset, não um erro.
            var described = await DescribeCompletedIfPublishedAsync(assets, session, ct);
            if (described is not null)
            {
                await MarkCompletedAsync(redis, session, session.Parts, described.Checksum, ct);
                return Ok(described);
            }

            // Sem multipart e sem objeto: o envio não existe mais (expirado ou
            // descartado). Resposta clara em vez de 500.
            _logger.LogWarning(ex, "Complete sem multipart/objeto para {UploadId}.", uploadId);
            return Conflict(new
            {
                errors = new[] { new { Code = "UploadGone", Message = "Este envio não existe mais no object storage. Anexe o instalador novamente." } }
            });
        }

        if (parts.Length == 0)
        {
            var described = await DescribeCompletedIfPublishedAsync(assets, session, ct);
            if (described is null)
                return BadRequest(new { errors = new[] { new { Code = "Validation", Message = "Nenhuma parte recebida: não há o que finalizar." } } });

            await MarkCompletedAsync(redis, session, session.Parts, described.Checksum, ct);
            return Ok(described);
        }

        try
        {
            // Sessão já concluída (retentativa após resposta perdida): o serviço
            // detecta que o objeto já existe no storage e devolve o asset.
            var asset = await assets.CompleteMultipartUploadAsync(
                session.ObjectKey,
                session.Kind,
                session.FileName,
                session.ContentType,
                session.SizeBytes,
                session.UploadId,
                parts,
                session.Checksum,
                ct);

            await MarkCompletedAsync(redis, session, parts, asset.Checksum, ct);
            return Ok(asset);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    /// <summary>
    /// Renova o prazo da sessão em andamento. Relê antes de gravar: um complete
    /// concorrente (outra aba) pode ter marcado a sessão como concluída, e
    /// regravar o snapshot antigo (Completed=false) faria o índice voltar a "em
    /// andamento" — o serviço de limpeza então não descartaria o objeto e ainda
    /// tentaria abortar um multipart que não existe mais.
    /// </summary>
    private static async Task RenewMultipartSessionAsync(
        IRedisService redis,
        MultipartUploadSessionState session,
        CancellationToken ct)
    {
        var current = await MultipartUploadSessionStore.LoadAsync(redis, session.UploadId);
        if (current is null || current.Completed)
            return;

        await MultipartUploadSessionStore.SaveAsync(redis, current, MultipartUploadSessionStore.SessionTtl);
        await MultipartUploadSessionStore.SaveIndexAsync(redis, current.UploadId, current.ObjectKey);
    }

    /// <summary>Asset de um upload já publicado no storage (null se não existir).</summary>
    private static async Task<CustomPackageAssetDto?> DescribeCompletedIfPublishedAsync(
        ICustomPackageAssetService assets,
        MultipartUploadSessionState session,
        CancellationToken ct)
    {
        try
        {
            return await assets.DescribeCompletedAssetAsync(
                session.ObjectKey, session.FileName, session.Checksum, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Marca a sessão como concluída e libera o índice de limpeza.</summary>
    private static async Task MarkCompletedAsync(
        IRedisService redis,
        MultipartUploadSessionState session,
        IReadOnlyList<MultipartUploadPartStateDto> parts,
        string? checksum,
        CancellationToken ct)
    {
        session.Completed = true;
        if (parts.Count > 0)
            session.Parts = parts.ToList();
        session.Checksum = string.IsNullOrWhiteSpace(checksum) ? session.Checksum : checksum;

        await MultipartUploadSessionStore.SaveAsync(redis, session, MultipartUploadSessionStore.CompletedSessionTtl);
        // O multipart não existe mais, mas o OBJETO existe: o índice passa a
        // "concluído" e é o que permite descartar o asset caso ele nunca seja
        // vinculado a um pacote (troca do arquivo antes de salvar, aba fechada).
        await MultipartUploadSessionStore.SaveCompletedIndexAsync(redis, session.UploadId, session.ObjectKey);
        await redis.DeleteAsync(MultipartUploadSessionStore.AbortPendingKey(session.UploadId));
    }

    /// <summary>Aborta o multipart (o storage descarta as partes enviadas).</summary>
    [HttpDelete("custom/assets/multipart/{uploadId}")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> AbortCustomAssetMultipartUpload(
        string uploadId,
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IAppPackageRepository packages,
        [FromServices] IRedisService redis,
        CancellationToken ct)
    {
        var session = await LoadOwnedMultipartSessionAsync(redis, uploadId);
        if (session is null)
            return NoContent();

        if (session.Completed)
        {
            // Upload já finalizado: abortar o multipart não faz sentido — o que se
            // descarta aqui é o OBJETO publicado que ainda não foi vinculado a um
            // pacote (trocar o arquivo antes de salvar não pode vazar GiBs).
            if (await packages.IsAssetObjectKeyReferencedAsync(session.ObjectKey, ct))
            {
                return Conflict(new { errors = new[] { new { Code = "Linked", Message = "Este instalador já está vinculado ao pacote. Use \"Excluir instalador\" para removê-lo." } } });
            }

            try
            {
                await assets.DeleteAsync(session.ObjectKey, ct);
            }
            catch (Exception ex)
            {
                // NÃO apaga sessão/índice: eles são a única pista para o serviço de
                // limpeza tentar de novo. O cliente também mantém o estado local.
                _logger.LogWarning(ex, "Falha ao remover o asset {ObjectKey}; mantendo o indice para nova tentativa.", session.ObjectKey);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    errors = new[] { new { Code = "StorageError", Message = "Não foi possível remover o arquivo no object storage. Tente novamente." } }
                });
            }

            await MultipartUploadSessionStore.DeleteAsync(redis, uploadId);
            return NoContent();
        }

        try
        {
            await assets.AbortMultipartUploadAsync(session.ObjectKey, session.UploadId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao abortar o multipart {UploadId}; mantendo o indice para nova tentativa.", session.UploadId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                errors = new[] { new { Code = "StorageError", Message = "Não foi possível cancelar o envio no object storage. Tente novamente." } }
            });
        }

        await MultipartUploadSessionStore.DeleteAsync(redis, uploadId);
        return NoContent();
    }

    /// <summary>Usuário autenticado (preenchido pelo middleware de auth).</summary>
    private Guid? CurrentUserId
    {
        get
        {
            if (HttpContext.Items["UserId"] is Guid uid)
                return uid;

            // Fallback: sem o item do middleware (caminho de auth alternativo), o
            // claim ainda identifica o dono — sem isso a sessão nasceria sem dono
            // e ficaria acessível a qualquer usuário com o uploadId.
            var claim = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(claim, out var claimUserId) ? claimUserId : null;
        }
    }

    // ── Multipart direto: estado em Redis (schema em MultipartUploadSessionStore) ──

    private static async Task SaveMultipartSessionAsync(IRedisService redis, MultipartUploadSessionDto session, Guid? userId)
    {
        var state = new MultipartUploadSessionState
        {
            UserId = userId,
            UploadId = session.UploadId,
            ObjectKey = session.ObjectKey,
            Kind = session.Kind,
            FileName = session.FileName,
            ContentType = session.ContentType,
            SizeBytes = session.SizeBytes,
            PartSizeBytes = session.PartSizeBytes,
            PartUrlTtlMinutes = session.PartUrlTtlMinutes,
        };

        await MultipartUploadSessionStore.SaveAsync(redis, state, MultipartUploadSessionStore.SessionTtl);
        // Índice com a object key, com TTL maior: é o que permite o serviço de
        // limpeza abortar o multipart se a sessão expirar sem finalizar.
        await MultipartUploadSessionStore.SaveIndexAsync(redis, state.UploadId, state.ObjectKey);
    }

    /// <summary>
    /// Carrega a sessão validando que ela pertence ao usuário atual. Devolve null
    /// (-> 404) tanto para sessão inexistente quanto para sessão de terceiros:
    /// responder 403 revelaria a existência do upload de outro usuário.
    /// </summary>
    private async Task<MultipartUploadSessionState?> LoadOwnedMultipartSessionAsync(IRedisService redis, string uploadId)
    {
        var session = await MultipartUploadSessionStore.LoadAsync(redis, uploadId);
        if (session is null)
            return null;

        if (session.UserId is Guid owner && CurrentUserId is Guid current && owner == current)
            return session;

        // Sessão anterior à vinculação por usuário (upgrade em andamento): aceita
        // apenas se não houver dono gravado; caso contrário, nega.
        return session.UserId is null ? session : null;
    }

    private static object BuildStatusResponse(
        MultipartUploadSessionState session,
        IReadOnlyList<MultipartUploadPartStateDto> received,
        bool completed = false)
    {
        var totalParts = (int)((session.SizeBytes + session.PartSizeBytes - 1) / session.PartSizeBytes);
        var receivedNumbers = received.Select(part => part.PartNumber).ToHashSet();

        return new
        {
            uploadId = session.UploadId,
            completed,
            objectKey = session.ObjectKey,
            fileName = session.FileName,
            sizeBytes = session.SizeBytes,
            partSizeBytes = session.PartSizeBytes,
            totalParts,
            partUrlTtlSeconds = session.PartUrlTtlMinutes * 60,
            receivedBytes = received.Sum(part => part.SizeBytes),
            receivedParts = received,
            pendingParts = Enumerable.Range(1, totalParts).Where(part => !receivedNumbers.Contains(part)).ToArray(),
        };
    }

    /// <summary>ETag do S3 sempre com aspas no CompleteMultipartUpload.</summary>
    private static string NormalizeETag(string? etag)
    {
        var value = (etag ?? string.Empty).Trim().Trim('"');
        return '"' + value + '"';
    }

    // ── Upload em partes: estado e helpers ────────────────────────────────
    /// <summary>Bloco padrão de 10 MiB, igual ao upload multipart direto.</summary>
    private const long CustomAssetPartBytes = 10L * 1024 * 1024;
    /// <summary>Limite do corpo por bloco (folga de protocolo sobre os 10 MiB).</summary>
    private const long CustomAssetPartLimitBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan CustomAssetUploadTtl = TimeSpan.FromHours(6);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CustomAssetUploadSession> CustomAssetUploads = new();
    private static string CustomAssetUploadsRoot => Path.Combine(Path.GetTempPath(), "discovery-asset-uploads");

    private sealed class CustomAssetUploadSession
    {
        public string Kind { get; init; } = "installer";
        public string FileName { get; init; } = string.Empty;
        public string ContentType { get; init; } = "application/octet-stream";
        public long SizeBytes { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Corpo do POST que abre a sessão de upload em partes.</summary>
    public sealed class CustomAssetUploadRequest
    {
        public string? Kind { get; set; }
        public string? FileName { get; set; }
        public string? ContentType { get; set; }
        public long SizeBytes { get; set; }
    }

    /// <summary>Corpo do POST que abre o upload multipart DIRETO no storage.</summary>
    public sealed class CustomAssetMultipartRequest
    {
        public string? Kind { get; set; }
        public string? FileName { get; set; }
        public string? ContentType { get; set; }
        public long SizeBytes { get; set; }
    }

    /// <summary>Corpo do POST que pede URLs pré-assinadas de blocos específicos.</summary>
    public sealed class CustomAssetPartUrlRequest
    {
        public List<int>? PartNumbers { get; set; }
    }

    /// <summary>Corpo do POST de complete (ETags coletados pelo cliente).</summary>
    public sealed class CustomAssetMultipartCompleteRequest
    {
        public List<CustomAssetMultipartPart>? Parts { get; set; }
    }

    /// <summary>ETag devolvido pelo storage no PUT de um bloco.</summary>
    public sealed class CustomAssetMultipartPart
    {
        public int PartNumber { get; set; }
        public string? ETag { get; set; }
        public long SizeBytes { get; set; }
    }

    private static async Task<long> CopyAtMostAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (total < maxBytes)
        {
            var toRead = (int)Math.Min(buffer.Length, maxBytes - total);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read <= 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            total += read;
        }
        return total;
    }

    private static void PruneCustomAssetUploads()
    {
        var cutoff = DateTimeOffset.UtcNow - CustomAssetUploadTtl;
        foreach (var entry in CustomAssetUploads)
        {
            if (entry.Value.CreatedAt >= cutoff) continue;
            if (CustomAssetUploads.TryRemove(entry.Key, out var stale))
                TryDeleteCustomAssetUpload(stale.FilePath);
        }
    }

    private static void TryDeleteCustomAssetUpload(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Serve o icone/capa de um pacote custom. Anonimo de proposito: e usado
    /// diretamente em tag &lt;img&gt; pela loja, que nao envia o header Authorization.
    /// Somente imagens sao servidas.
    /// </summary>
    [HttpGet("custom/{packageId}/icon")]
    [AllowAnonymous]
    public async Task<IActionResult> GetCustomIcon(
        string packageId,
        [FromServices] IAppPackageRepository packages,
        [FromServices] ICustomPackageAssetService assets,
        CancellationToken ct = default)
    {
        var package = await packages.GetByInstallationTypeAndPackageIdAsync(
            AppInstallationType.Custom, packageId, ct);

        if (package is null || string.IsNullOrWhiteSpace(package.IconObjectKey))
            return NotFound();

        try
        {
            var asset = await assets.DownloadAsync(package.IconObjectKey, ct);
            if (!asset.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return NotFound();

            return File(asset.Content, asset.ContentType, asset.FileName);
        }
        catch
        {
            return NotFound();
        }
    }

    /// <summary>Remove um pacote custom (e os arquivos no object storage).</summary>
    [HttpDelete("custom/{packageId}")]
    [RequirePermission(ResourceType.AppStore, ActionType.Delete)]
    public async Task<IActionResult> DeleteCustomPackage(string packageId, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteCustomAppPackageCommand(packageId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Remove apenas o instalador publicado de um pacote custom (mantém o pacote e o
    /// ícone), permitindo enviar outro arquivo em seguida. 204 em caso de sucesso.
    /// </summary>
    [HttpDelete("custom/{packageId}/file")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> DeleteCustomPackageFile(string packageId, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteCustomAppPackageFileCommand(packageId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Remove apenas o ícone/capa publicado de um pacote custom (mantém o pacote e o
    /// instalador). O objeto também é apagado do object storage. 204 em caso de sucesso.
    /// </summary>
    [HttpDelete("custom/{packageId}/icon")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> DeleteCustomPackageIcon(string packageId, CancellationToken ct)
    {
        var result = await mediator.Send(new DeleteCustomAppPackageIconCommand(packageId), ct);
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ═══════════════════════════════════════════════════════════════
    // Sync
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Dispara a sincronização do catálogo (Winget 0 / Chocolatey 1) em background e retorna 202 imediatamente.
    /// Consulte o progresso/resultado via GET sync/status. Custom (2) é manual.
    /// </summary>
    [HttpPost("sync")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public IActionResult SyncCatalog(
        [FromServices] AppCatalogBackgroundSyncService backgroundSync,
        [FromQuery] int installationType = 0)
    {
        var type = (AppInstallationType)installationType;
        if (!Enum.IsDefined(type) || type == AppInstallationType.Custom)
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = "Invalid installationType. Use 0 (Winget) or 1 (Chocolatey)." } } });

        var started = backgroundSync.TryStartSync(type);
        var message = started
            ? "Sync started. Poll sync/status for progress."
            : "Sync already in progress for this installation type.";
        return Accepted(new { running = true, installationType, message });
    }

    /// <summary>
    /// Status da sincronização: se há job em andamento e o resultado da última execução.
    /// O último resultado é o mais recente entre a execução em memória (manual) e a
    /// persistida no ServerConfiguration (manual ou automática via Quartz).
    /// </summary>
    [HttpGet("sync/status")]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> GetSyncStatus(
        [FromServices] AppCatalogBackgroundSyncService backgroundSync,
        [FromServices] IAppCatalogSyncStatusStore statusStore,
        [FromQuery] int installationType = 0)
    {
        var type = (AppInstallationType)installationType;
        if (!Enum.IsDefined(type))
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = "Invalid installationType." } } });

        var inMemory = backgroundSync.GetLastResult(type);
        var persisted = await statusStore.GetResultAsync(type);

        var lastResult = (inMemory, persisted) switch
        {
            (null, null) => null,
            (null, var p) => p,
            (var m, null) => m,
            (var m, var p) => p.SyncedAt > m.SyncedAt ? p : m
        };

        return Ok(new AppCatalogSyncStatusDto
        {
            Running = backgroundSync.IsRunning(type),
            LastResult = lastResult
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Effective (approved) apps
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get approved/effective apps for a scope.</summary>
    [HttpGet("effective")]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> GetEffective(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] int installationType = 0)
    {
        var result = await mediator.Send(new GetAppStoreEffectiveAppsQuery(
            clientId, siteId, agentId, (AppInstallationType)installationType));
        return result.ToActionResult();
    }

    // ═══════════════════════════════════════════════════════════════
    // Approval rules
    // ═══════════════════════════════════════════════════════════════

    /// <summary>List approval rules by scope and installation type.</summary>
    [HttpGet("approvals")]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> GetApprovals(
        [FromQuery] int? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] int? installationType = null)
    {
        var sc = scopeType.HasValue ? (AppApprovalScopeType)scopeType.Value : default(AppApprovalScopeType?);
        var result = await mediator.Send(new GetAppStoreApprovalsQuery(sc, scopeId, installationType));
        return result.ToActionResult();
    }

    /// <summary>Create/update an approval rule.</summary>
    [HttpPost("approvals")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> UpsertApproval([FromBody] UpsertAppApprovalRuleCommand cmd)
    {
        var result = await mediator.Send(cmd with { ChangedBy = Username, IpAddress = ClientIp });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Delete an approval rule.</summary>
    [HttpDelete("approvals/{ruleId:guid}")]
    [RequirePermission(ResourceType.AppStore, ActionType.Delete)]
    public async Task<IActionResult> DeleteApproval(
        Guid ruleId,
        [FromQuery] string? reason = null)
    {
        // ChangedBy/IpAddress vêm do usuário autenticado, não da query.
        var result = await mediator.Send(new DeleteAppApprovalRuleCommand(ruleId, reason, Username, ClientIp));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors =>
            {
                var payload = new { errors = errors.Select(e => new { e.Code, e.Message }) };
                return errors.Any(e => e.Code == "NotFound")
                    ? NotFound(payload)
                    : BadRequest(payload);
            });
    }

    // ═══════════════════════════════════════════════════════════════
    // Audit
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Audit history for approval changes.</summary>
    [HttpGet("approvals/audit")]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> GetApprovalAudit(
        [FromQuery] int? installationType = null,
        [FromQuery] string? packageId = null,
        [FromQuery] int? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? changedBy = null,
        [FromQuery] DateTime? changedFrom = null,
        [FromQuery] DateTime? changedTo = null,
        [FromQuery] int? changeType = null,
        [FromQuery] int limit = 50,
        [FromQuery] Guid? cursor = null)
    {
        var sc = scopeType.HasValue ? (AppApprovalScopeType)scopeType.Value : default(AppApprovalScopeType?);
        var ct = changeType.HasValue ? (AppApprovalAuditChangeType)changeType.Value : default(AppApprovalAuditChangeType?);
        var result = await mediator.Send(new GetAppStoreApprovalAuditQuery(
            installationType, packageId, sc, scopeId, changedBy, changedFrom, changedTo, ct, limit, cursor));
        return result.ToActionResult();
    }

    // ═══════════════════════════════════════════════════════════════
    // Package diff
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get diff for a single package across scope hierarchy.</summary>
    [HttpGet("diff/{packageId}")]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> GetPackageDiff(
        string packageId,
        [FromQuery] int installationType = 0,
        [FromQuery] int scopeType = 0,
        [FromQuery] Guid? scopeId = null)
    {
        var result = await mediator.Send(new GetAppStorePackageDiffQuery(
            (AppInstallationType)installationType, packageId, (AppApprovalScopeType)scopeType, scopeId));
        return result.ToActionResult();
    }

    /// <summary>Get effective diffs across a scope.</summary>
    [HttpGet("diff/effective")]
    [RequirePermission(ResourceType.AppStore, ActionType.View)]
    public async Task<IActionResult> GetEffectiveDiffs(
        [FromQuery] int scopeType = 0,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] int installationType = 0,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null)
    {
        var result = await mediator.Send(new GetAppStoreEffectiveDiffsQuery(
            (AppApprovalScopeType)scopeType, scopeId, installationType, search, limit, cursor));
        return result.ToActionResult();
    }
}