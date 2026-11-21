using System.Text.Json;
using Discovery.Core.Cqrs.AgentAuth.AiChat;
using Discovery.Core.Cqrs.Configurations.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

public class AgentAuthAiChatController : AgentAuthControllerBase
{
    private readonly IAiChatService _aiChat;
    private readonly ILogger<AgentAuthAiChatController> _logger;

    /// <summary>B5: limite de toolResults aceitos por request (anti-abuso).</summary>
    private const int MaxToolResultsPerRequest = 20;

    /// <summary>
    /// Limite de tamanho por tool result enviado ao LLM (defesa em profundidade:
    /// o agent Go já trunca em 16 KB; aqui garantimos o teto também server-side).
    /// </summary>
    private const int MaxToolResultLength = 32 * 1024;

    /// <summary>
    /// Teto específico para capturas de tela: o resultado carrega a imagem em
    /// base64 (contrato "image_base64") e precisa sobreviver INTACTO para a
    /// visão do LLM. Limitado a ~6 MB de base64 por captura (o agent já limita
    /// o lado maior da imagem a 1600 px, então o normal fica bem abaixo).
    /// </summary>
    private const int MaxScreenshotToolResultLength = 8 * 1024 * 1024;

    /// <summary>
    /// Teto do detalhe de erro de provedor exposto via SSE (~500 chars, o mesmo
    /// usado por OpenAiProvider.BuildProviderErrorMessage). O corpo já vem
    /// truncado, mas ProviderStreamErrorException monta a mensagem em volta dele.
    /// </summary>
    private const int MaxProviderErrorDetailLength = 500;

    /// <summary>
    /// Ranges IPv4/IPv6 oficiais da Cloudflare (https://www.cloudflare.com/ips/).
    /// Espelha os defaults de ForwardedHeadersOptions em Program.cs — o header
    /// CF-Connecting-IP só é aceito quando a conexão vem de um desses ranges.
    /// </summary>
    private static readonly System.Net.IPNetwork[] CloudflareNetworks =
    [
        new(System.Net.IPAddress.Parse("173.245.48.0"), 20),
        new(System.Net.IPAddress.Parse("103.21.244.0"), 22),
        new(System.Net.IPAddress.Parse("103.22.200.0"), 22),
        new(System.Net.IPAddress.Parse("103.31.4.0"), 22),
        new(System.Net.IPAddress.Parse("141.101.64.0"), 18),
        new(System.Net.IPAddress.Parse("108.162.192.0"), 18),
        new(System.Net.IPAddress.Parse("190.93.240.0"), 20),
        new(System.Net.IPAddress.Parse("188.114.96.0"), 20),
        new(System.Net.IPAddress.Parse("197.234.240.0"), 22),
        new(System.Net.IPAddress.Parse("198.41.128.0"), 17),
        new(System.Net.IPAddress.Parse("162.158.0.0"), 15),
        new(System.Net.IPAddress.Parse("104.16.0.0"), 13),
        new(System.Net.IPAddress.Parse("104.24.0.0"), 14),
        new(System.Net.IPAddress.Parse("172.64.0.0"), 13),
        new(System.Net.IPAddress.Parse("131.0.72.0"), 22),
        new(System.Net.IPAddress.Parse("2400:cb00::"), 32),
        new(System.Net.IPAddress.Parse("2606:4700::"), 32),
        new(System.Net.IPAddress.Parse("2803:f800::"), 32),
        new(System.Net.IPAddress.Parse("2405:b500::"), 32),
        new(System.Net.IPAddress.Parse("2405:8100::"), 32),
        new(System.Net.IPAddress.Parse("2a06:98c0::"), 29),
        new(System.Net.IPAddress.Parse("2c0f:f248::"), 32),
    ];

    private static readonly JsonSerializerOptions SseJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AgentAuthAiChatController(
        IMediator mediator,
        IAgentRepository agentRepo,
        IAiChatService aiChat,
        ILogger<AgentAuthAiChatController> logger)
        : base(mediator, agentRepo)
    {
        _aiChat = aiChat;
        _logger = logger;
    }

    /// <summary>
    /// Política de anexos aplicada ao chat de IA/LLM. É a MESMA configuração
    /// global de anexos (TicketAttachmentSettings); o cliente filtra a parte
    /// que consegue transportar (imagens) usando imageContentTypes.
    /// </summary>
    [HttpGet("me/ai-chat/attachment-settings")]
    public async Task<IActionResult> GetMyAiChatAttachmentSettings()
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;

        var result = await _mediator.Send(new GetTicketAttachmentSettingsQuery(), HttpContext.RequestAborted);
        if (result.IsFailure || result.Value is null)
            return BadRequest(new { error = "Não foi possível ler a configuração de anexos." });

        var settings = result.Value;
        return Ok(new
        {
            enabled = settings.Enabled,
            maxFileSizeBytes = settings.MaxFileSizeBytes,
            // Devolve a allowlist COMPLETA: o cliente filtra o que consegue
            // anexar (imagens). Não devolver só image/* evita a ambiguidade de
            // "nenhuma imagem permitida" ser confundida com "campo ausente".
            allowedContentTypes = settings.AllowedContentTypes,
            imageContentTypes = settings.GetAllowedImageContentTypes(),
        });
    }

    /// <summary>
    /// Valida as imagens anexadas ao chat de IA antes de iniciar o stream.
    /// A allowlist é a mesma dos anexos de ticket/comentário/suporte e o limite
    /// por imagem é MaxFileSizeBytes (padrão 10 MB). Falha explícita: o usuário
    /// recebe o motivo em vez de a imagem ser descartada em silêncio.
    /// </summary>
    internal static (string? Error, int StatusCode) ValidateAiChatImages(
        IReadOnlyList<string> images, TicketAttachmentSettings settings)
    {
        var allowed = settings.GetAllowedImageContentTypes();
        for (var index = 0; index < images.Count; index++)
        {
            var label = $"Imagem {index + 1}";
            var raw = images[index]?.Trim();
            if (string.IsNullOrEmpty(raw) || !raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return ($"{label}: formato inválido — anexe a imagem como data URL.", StatusCodes.Status400BadRequest);

            var comma = raw.IndexOf(',');
            if (comma <= 5 || !raw.AsSpan(5, comma - 5).Contains(";base64", StringComparison.OrdinalIgnoreCase))
                return ($"{label}: formato inválido — esperado data URL base64.", StatusCodes.Status400BadRequest);

            var header = raw[5..comma];
            var mime = header.Split(';')[0].Trim().ToLowerInvariant();
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || !allowed.Contains(mime, StringComparer.OrdinalIgnoreCase))
            {
                return ($"{label}: tipo {mime} não permitido nas configurações de anexos.", StatusCodes.Status400BadRequest);
            }

            var base64 = raw[(comma + 1)..];
            var decodedBytes = EstimateBase64DecodedBytes(base64);
            if (decodedBytes <= 0)
                return ($"{label}: conteúdo base64 inválido.", StatusCodes.Status400BadRequest);

            if (decodedBytes > settings.MaxFileSizeBytes)
            {
                return (
                    $"{label}: {decodedBytes} bytes excedem o limite de {settings.MaxFileSizeBytes} bytes das configurações de anexos.",
                    StatusCodes.Status413RequestEntityTooLarge);
            }
        }

        return (null, StatusCodes.Status200OK);
    }

    /// <summary>Estimativa barata (sem decodificar) do tamanho binário do base64.</summary>
    internal static long EstimateBase64DecodedBytes(string base64)
    {
        long chars = 0;
        var padding = 0;
        foreach (var c in base64)
        {
            if (c is ' ' or '\r' or '\n' or '\t') continue;
            if (c == '=')
            {
                padding++;
                continue;
            }
            var valid = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '-' or '_';
            if (!valid) return 0;
            chars++;
        }

        if (chars == 0) return 0;
        // chars não inclui o padding; a fórmula correta usa o comprimento total
        // (chars + padding) e subtrai o padding DEPOIS — sem isso o tamanho
        // decodificado era subestimado em 1-2 bytes nas imagens com padding.
        var bytes = ((chars + padding) * 3) / 4 - padding;
        return bytes > 0 ? bytes : 0;
    }

    [HttpPost("me/ai-chat")]
    public async Task<IActionResult> ChatSync([FromBody] ChatSyncCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id, ClientIp = ResolveAgentClientIp() }, ct), Ok);
    }

    [HttpPost("me/ai-chat/async")]
    public async Task<IActionResult> ChatAsync([FromBody] ChatAsyncCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(cmd with { AgentId = id }, ct), dto => Accepted(dto));
    }

    // O chat envia TODAS as imagens anexadas (sem teto de quantidade). O default
    // do Kestrel (~28,6 MB) derrubava com 413 quem anexava várias fotos; 128 MB
    // cobre ~30 prints de 4 MB. O limite por imagem continua no AiChatHelpers.
    [RequestSizeLimit(128_000_000)]
    [HttpPost("me/ai-chat/stream")]
    public async Task ChatStream([FromBody] ChatStreamCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId))
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(new { error = "Agent not authenticated." }, ct);
            return;
        }
        var (_, blocked) = await GetAgentOrBlockAsync(agentId, false);
        if (blocked is not null)
        {
            var obj = (ObjectResult)blocked;
            HttpContext.Response.StatusCode = obj.StatusCode ?? 403;
            await HttpContext.Response.WriteAsJsonAsync(obj.Value ?? new { error = "Agent blocked." }, ct);
            return;
        }

        HttpContext.Response.ContentType = "text/event-stream; charset=utf-8";
        HttpContext.Response.Headers.Append("Cache-Control", "no-cache");
        HttpContext.Response.Headers.Append("Connection", "keep-alive");
        HttpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        // Preenche o ClientIp do comando (antes era campo morto no contrato).
        cmd = cmd with { ClientIp = ResolveAgentClientIp() };

        // O chat de IA/LLM usa a MESMA configuração global de anexos. Valida
        // antes de abrir o SSE: assim o usuário recebe um erro claro (limite ou
        // tipo) em vez de a imagem ser descartada silenciosamente.
        if (cmd.Images is { Count: > 0 } imagesToValidate)
        {
            var attachmentSettingsResult = await _mediator.Send(
                new GetTicketAttachmentSettingsQuery(), ct);
            if (attachmentSettingsResult.IsFailure || attachmentSettingsResult.Value is null)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Não foi possível ler a configuração de anexos." }, ct);
                return;
            }

            var attachmentSettings = attachmentSettingsResult.Value;
            if (!attachmentSettings.Enabled)
            {
                HttpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                await HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Anexos estão desabilitados nas configurações do servidor." }, ct);
                return;
            }

            var (validationError, validationStatus) = ValidateAiChatImages(imagesToValidate, attachmentSettings);
            if (validationError is not null)
            {
                HttpContext.Response.StatusCode = validationStatus;
                await HttpContext.Response.WriteAsJsonAsync(new { error = validationError }, ct);
                return;
            }
        }

        try
        {
            // ToolResults != null → round 2+ (agent executou tools)
            // Message != null → round 1 (nova mensagem do usuário)
            var sessionGuid = Guid.TryParse(cmd.SessionId, out var g) ? g : (Guid?)null;
            var toolResults = cmd.ToolResults?.Select(tr => new ToolResultItem(
                tr.CallId,
                tr.Name,
                TruncateToolResult(tr.Name, tr.Result))).ToList();

            // B5: limita a contagem de toolResults por request (o teto por item
            // é 32 KB, mas N itens podiam estourar o contexto/custo do LLM).
            if (toolResults is { Count: > MaxToolResultsPerRequest })
            {
                toolResults = toolResults!.Take(MaxToolResultsPerRequest).ToList();
            }

            // B4: ToolResults presentes → SEMPRE multi-round, independente de
            // Mode ou de Message (antes, {message:"", toolResults:[...]} caía no
            // branch legado e os toolResults eram descartados silenciosamente).
            IAsyncEnumerable<AiChatStreamChunk> stream;
            if (toolResults is { Count: > 0 })
            {
                stream = _aiChat.StreamMultiRoundAsync(agentId,
                    string.IsNullOrWhiteSpace(cmd.Message) ? null : cmd.Message,
                    sessionGuid, toolResults, cmd.DepartmentId, cmd.SystemNote, ct);
            }
            else
            {
                stream = _aiChat.StreamAsync(agentId, cmd.Message ?? string.Empty,
                    sessionGuid, cmd.DepartmentId, cmd.SystemNote, cmd.Images, ct);
            }

            await foreach (var chunk in stream)
            {
                if (chunk.Type == "error")
                {
                    await WriteSseJsonAsync(HttpContext, new { type = "error", error = chunk.Error ?? "unknown" }, ct);
                    await HttpContext.Response.Body.FlushAsync(ct);
                    return;
                }
                await WriteSseJsonAsync(HttpContext, chunk, ct);
                await HttpContext.Response.Body.FlushAsync(ct);
                if (chunk.Type is "done" or "round_end") break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cliente desconectou — nada a fazer.
        }
        catch (OperationCanceledException ex)
        {
            // B16: cancelamento do lado do provedor (timeout de geração). Antes era
            // engolido em silêncio e o cliente recebia HTTP 200 sem nenhum evento.
            _logger.LogWarning(ex, "[ChatStream] Geração cancelada antes de concluir (timeout do provedor?). TraceId={TraceId}", HttpContext.TraceIdentifier);
            await WriteSseJsonAsync(HttpContext, new { type = "error", error = "A geração da resposta excedeu o tempo limite. Tente novamente." }, CancellationToken.None);
            await HttpContext.Response.Body.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                // B7: não expor exceções internas (DB/paths/stack) ao client.
                // Exceção de provedor é DIFERENTE: a mensagem já é o próprio texto
                // de erro do provedor (truncado), é o que o suporte precisa para
                // distinguir 400 payload malformado / 401 key / 402 créditos / 429
                // rate limit, e antes era substituída pelo texto genérico.
                var traceId = HttpContext.TraceIdentifier;
                var (status, detail) = DescribeChatStreamFailure(ex);
                if (detail is null)
                {
                    _logger.LogError(ex, "[ChatStream] Exceção não tratada. TraceId={TraceId}", traceId);
                    await WriteSseJsonAsync(HttpContext,
                        new { type = "error", error = "Erro interno ao processar o chat.", traceId, status }, ct);
                }
                else
                {
                    _logger.LogError(ex,
                        "[ChatStream] Falha do provedor LLM exposta ao client. TraceId={TraceId} Status={Status}",
                        traceId, status);
                    await WriteSseJsonAsync(HttpContext,
                        new { type = "error", error = detail, traceId, status }, ct);
                }
            }
        }
    }

    [HttpGet("me/ai-chat/jobs/{jobId}")]
    public async Task<IActionResult> GetAiChatJob(Guid jobId, CancellationToken ct)
    {
        if (!TryGetAgentId(out var id)) return Unauthorized();
        var (_, blocked) = await GetAgentOrBlockAsync(id, false);
        if (blocked is not null) return blocked;
        return MapResult(await _mediator.Send(new GetAiChatJobQuery(id, jobId), ct), Ok);
    }

    [HttpPost("me/agent-tools/registry")]
    public async Task<IActionResult> RegisterAgentTools([FromBody] RegisterAgentToolsCommand cmd, CancellationToken ct)
    {
        if (!TryGetAgentId(out var agentId)) return Unauthorized();
        var (agent, blocked) = await GetAgentOrBlockAsync(agentId, false);
        if (blocked is not null) return blocked;

        var tools = cmd.Tools.Select(t => new AgentToolRegistration(
            t.Name, t.Description, t.ParametersSchema.GetRawText())).ToList();
        await _aiChat.RegisterAgentToolsAsync(agentId, agent!.SiteId, tools, ct);
        return Ok(new { registered = tools.Count });
    }

    /// <summary>
    /// Verifica se o IP remoto pertence a um range da Cloudflare.
    /// </summary>
    private static bool IsCloudflareIp(System.Net.IPAddress? remoteIp)
    {
        if (remoteIp is null) return false;
        foreach (var net in CloudflareNetworks)
        {
            if (net.Contains(remoteIp)) return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve o IP real do agent de forma confiável:
    /// 1. Connection.RemoteIpAddress — já resolvido pelo middleware
    ///    ForwardedHeaders (X-Forwarded-For aceito apenas de proxies confiáveis:
    ///    ranges Cloudflare + localhost, configurados em Program.cs).
    /// 2. CF-Connecting-IP — aceito SOMENTE se a conexão direta veio de um IP
    ///    Cloudflare (impede spoof por clients autenticados).
    /// 3. Fallback: RemoteIpAddress cru.
    /// Nunca lê X-Forwarded-For manualmente (spoofável pelo agent).
    /// </summary>
    private string ResolveAgentClientIp()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;

        // 1. ForwardedHeaders middleware já resolveu o IP real de proxy confiável.
        if (remoteIp is not null && !IsCloudflareIp(remoteIp) && !System.Net.IPAddress.IsLoopback(remoteIp))
        {
            return remoteIp.ToString();
        }

        // 2. Conexão direta da Cloudflare: usa o header oficial CF-Connecting-IP.
        if (IsCloudflareIp(remoteIp))
        {
            var cfIp = HttpContext.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(cfIp) && System.Net.IPAddress.TryParse(cfIp.Trim(), out _))
            {
                return cfIp.Trim();
            }
        }

        // 3. Fallback (desenvolvimento/local sem proxy).
        return remoteIp?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Trunca o resultado de uma tool para MaxToolResultLength. Tenta fechar
    /// estruturas JSON abertas; se o resultado truncado não for JSON válido,
    /// devolve texto cru com marcador (nunca JSON quebrado).
    /// </summary>
    internal static string TruncateToolResult(string toolName, string? result)
    {
        var r = result ?? string.Empty;
        // Captura de tela: o teto padrão (32 KB) destruiria a imagem em base64.
        var maxLength = toolName is "capture_screenshot" or "screen" || r.Contains("\"image_base64\"", StringComparison.Ordinal)
            ? MaxScreenshotToolResultLength
            : MaxToolResultLength;
        if (r.Length <= maxLength) return r;

        var cut = r[..maxLength];
        var trimmed = cut.TrimEnd(' ', '\t', '\r', '\n', ',');

        // Fecha estruturas JSON abertas (contagem de delimitadores fora de strings).
        var stack = new Stack<char>();
        var inStr = false;
        var esc = false;
        foreach (var c in trimmed)
        {
            if (inStr)
            {
                if (esc) { esc = false; }
                else if (c == '\\') { esc = true; }
                else if (c == '"') { inStr = false; }
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']': if (stack.Count > 0) stack.Pop(); break;
            }
        }

        var closed = trimmed + string.Concat(stack);
        if (inStr) closed += "\"";

        // Só usa a versão fechada se for JSON válido; caso contrário, texto cru.
        if (IsValidJson(closed) && !inStr)
        {
            return closed;
        }
        return cut + $"\n...[truncado pelo servidor; tool={toolName}; total={r.Length} chars]";
    }

    /// <summary>
    /// Validação leve de JSON usando System.Text.Json.
    /// </summary>
    private static bool IsValidJson(string s)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// F5: os erros de provedor LLM eram engolidos em "Erro interno ao processar o
    /// chat.", o que destruía a diagnóstica (o agent grava status_code=0). Devolve o
    /// texto real do provedor (já truncado por OpenAiProvider.BuildProviderErrorMessage
    /// e re-truncado aqui por segurança) e o status HTTP a reportar no SSE.
    /// Exceções genuinamente inesperadas (DB, NullReference, etc.) continuam com
    /// detail null para o catch cair na mensagem genérica — B7 preservado.
    /// </summary>
    internal static (int Status, string? Detail) DescribeChatStreamFailure(Exception ex)
    {
        switch (ex)
        {
            case Discovery.Infrastructure.Services.ProviderStreamErrorException pse
                when !string.IsNullOrWhiteSpace(pse.Message):
                return (StatusCodes.Status502BadGateway, TruncateErrorDetail(pse.Message));
            case HttpRequestException hre when !string.IsNullOrWhiteSpace(hre.Message):
                // A mensagem já existe e é do provedor (OpenAiProvider:666). O status
                // upstream (400/401/402/429) não vem no HttpRequestException — o
                // provider o embute no texto — então reportamos 502 como status do SSE.
                return (StatusCodes.Status502BadGateway, TruncateErrorDetail(hre.Message));
            default:
                return (StatusCodes.Status500InternalServerError, null);
        }
    }

    private static string TruncateErrorDetail(string detail) =>
        detail.Length <= MaxProviderErrorDetailLength
            ? detail
            : detail[..MaxProviderErrorDetailLength] + "...";

    private static async Task WriteSseJsonAsync(HttpContext httpContext, object value, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value, SseJsonOptions);
        await httpContext.Response.WriteAsync($"data: {json}\n\n", ct);
    }
}
