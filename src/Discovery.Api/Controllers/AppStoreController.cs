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

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/app-store")]
public class AppStoreController(IMediator mediator) : ControllerBase
{
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
            return Ok(asset);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    /// <summary>
    /// Abre uma sessão de upload em PARTES (blocos de 50 MiB, padronizado).
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
    // cada bloco de 50 MiB e o navegador faz PUT direto na URL assinada. Isso é
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

            await SaveMultipartSessionAsync(redis, session);

            return Ok(new
            {
                uploadId = session.UploadId,
                objectKey = session.ObjectKey,
                partSizeBytes = session.PartSizeBytes,
                totalParts = (int)((session.SizeBytes + session.PartSizeBytes - 1) / session.PartSizeBytes),
                partUrlTtlSeconds = session.PartUrlTtlMinutes * 60,
                fileName = session.FileName,
                sizeBytes = session.SizeBytes,
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
        var session = await LoadMultipartSessionAsync(redis, uploadId);
        if (session is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sessão de upload não encontrada ou expirada." } } });

        var received = await assets.ListUploadedPartsAsync(session.ObjectKey, session.UploadId, ct);
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
        var session = await LoadMultipartSessionAsync(redis, uploadId);
        if (session is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sessão de upload não encontrada ou expirada." } } });

        var totalParts = (int)((session.SizeBytes + session.PartSizeBytes - 1) / session.PartSizeBytes);
        var requested = (request?.PartNumbers is { Count: > 0 })
            ? request.PartNumbers.Distinct().OrderBy(x => x).ToArray()
            : Enumerable.Range(1, totalParts).ToArray();

        if (requested.Any(part => part < 1 || part > totalParts))
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = $"Número de bloco fora do intervalo 1..{totalParts}." } } });

        var urls = new List<MultipartUploadPartUrlDto>(requested.Length);
        foreach (var part in requested)
            urls.Add(await assets.GetUploadPartUrlAsync(session.ObjectKey, session.UploadId, part, ct));

        return Ok(new { uploadId, totalParts, parts = urls });
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
        var session = await LoadMultipartSessionAsync(redis, uploadId);
        if (session is null)
            return NotFound(new { errors = new[] { new { Code = "NotFound", Message = "Sessão de upload não encontrada ou expirada." } } });

        try
        {
            var parts = (request?.Parts is { Count: > 0 })
                ? request.Parts
                    .Select(part => new MultipartUploadPartStateDto(part.PartNumber, NormalizeETag(part.ETag), part.SizeBytes))
                    .ToArray()
                : (await assets.ListUploadedPartsAsync(session.ObjectKey, session.UploadId, ct)).ToArray();

            var asset = await assets.CompleteMultipartUploadAsync(
                session.ObjectKey,
                session.Kind,
                session.FileName,
                session.ContentType,
                session.SizeBytes,
                session.UploadId,
                parts,
                ct);

            await redis.DeleteAsync(MultipartSessionKey(uploadId));
            return Ok(asset);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { errors = new[] { new { Code = "Validation", Message = ex.Message } } });
        }
    }

    /// <summary>Aborta o multipart (o storage descarta as partes enviadas).</summary>
    [HttpDelete("custom/assets/multipart/{uploadId}")]
    [RequirePermission(ResourceType.AppStore, ActionType.Edit)]
    public async Task<IActionResult> AbortCustomAssetMultipartUpload(
        string uploadId,
        [FromServices] ICustomPackageAssetService assets,
        [FromServices] IRedisService redis,
        CancellationToken ct)
    {
        var session = await LoadMultipartSessionAsync(redis, uploadId);
        if (session is not null)
        {
            try
            {
                await assets.AbortMultipartUploadAsync(session.ObjectKey, session.UploadId, ct);
            }
            catch (Exception)
            {
                // best-effort: o importante é liberar a sessão (TTL de 24h no storage).
            }

            await redis.DeleteAsync(MultipartSessionKey(uploadId));
        }

        return NoContent();
    }

    // ── Multipart direto: estado (Redis) e helpers ────────────────────────
    private sealed class CustomAssetMultipartSession
    {
        public string UploadId { get; set; } = string.Empty;
        public string ObjectKey { get; set; } = string.Empty;
        public string Kind { get; set; } = "installer";
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "application/octet-stream";
        public long SizeBytes { get; set; }
        public long PartSizeBytes { get; set; } = 50L * 1024 * 1024;
        public int PartUrlTtlMinutes { get; set; } = 60;
    }

    /// <summary>Sessões sobrevivem a restart da API (estado em Redis, TTL 24h).</summary>
    private static string MultipartSessionKey(string uploadId) => $"appstore:asset-multipart:{uploadId}";
    private static readonly TimeSpan MultipartSessionTtl = TimeSpan.FromHours(24);

    private static async Task SaveMultipartSessionAsync(IRedisService redis, MultipartUploadSessionDto session)
    {
        var state = new CustomAssetMultipartSession
        {
            UploadId = session.UploadId,
            ObjectKey = session.ObjectKey,
            Kind = session.Kind,
            FileName = session.FileName,
            ContentType = session.ContentType,
            SizeBytes = session.SizeBytes,
            PartSizeBytes = session.PartSizeBytes,
            PartUrlTtlMinutes = session.PartUrlTtlMinutes,
        };

        await redis.SetAsync(MultipartSessionKey(session.UploadId), System.Text.Json.JsonSerializer.Serialize(state), (int)MultipartSessionTtl.TotalSeconds);
    }

    private static async Task<CustomAssetMultipartSession?> LoadMultipartSessionAsync(IRedisService redis, string uploadId)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
            return null;

        var json = await redis.GetAsync(MultipartSessionKey(uploadId));
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CustomAssetMultipartSession>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static object BuildStatusResponse(CustomAssetMultipartSession session, IReadOnlyList<MultipartUploadPartStateDto> received)
    {
        var totalParts = (int)((session.SizeBytes + session.PartSizeBytes - 1) / session.PartSizeBytes);
        var receivedNumbers = received.Select(part => part.PartNumber).ToHashSet();

        return new
        {
            uploadId = session.UploadId,
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
    /// <summary>Bloco padrão de 50 MiB (o edge do Cloudflare recusa ~100 MB+).</summary>
    private const long CustomAssetPartBytes = 50L * 1024 * 1024;
    /// <summary>Limite do corpo por bloco (folga de protocolo sobre os 50 MiB).</summary>
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
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Delete an approval rule.</summary>
    [HttpDelete("approvals/{ruleId:guid}")]
    [RequirePermission(ResourceType.AppStore, ActionType.Delete)]
    public async Task<IActionResult> DeleteApproval(
        Guid ruleId,
        [FromQuery] string? reason = null,
        [FromQuery] string? changedBy = null,
        [FromQuery] string? ipAddress = null)
    {
        var result = await mediator.Send(new DeleteAppApprovalRuleCommand(ruleId, reason, changedBy, ipAddress));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
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
