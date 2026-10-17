using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services.Ai;

/// <summary>
/// Carrega as imagens anexadas a um chamado como data URLs para o modelo
/// multimodal (triagem por IA e consultas de ticket).
///
/// Regras deliberadamente conservadoras: só tipos de imagem que o contrato de
/// visão aceita, teto por arquivo e no máximo 3 imagens (o mesmo limite do
/// chat). Falha de download/storage nunca derruba a triagem — a imagem é
/// simplesmente omitida e o chamado é analisado pelo texto.
/// </summary>
public sealed class TicketAttachmentImageSource(
    IAttachmentService attachments,
    ILogger<TicketAttachmentImageSource> logger) : ITicketAttachmentImageSource
{
    /// <summary>Tipos de imagem aceitos pelo contrato de visão (assinatura + MIME).</summary>
    public static readonly IReadOnlyList<string> AllowedContentTypes =
    [
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/webp",
        "image/gif"
    ];

    /// <summary>Máximo de imagens enviadas por prompt (mesmo teto do chat).</summary>
    public const int MaxImagesPerPrompt = 3;

    /// <inheritdoc />
    public int MaxImages => MaxImagesPerPrompt;

    /// <summary>Teto por arquivo. Acima disso o anexo não vai para o LLM.</summary>
    public const long MaxBytesPerImage = 4 * 1024 * 1024;

    /// <summary>
    /// Seleciona os anexos que podem virar imagem no prompt: imagem aceita,
    /// não deletada, dentro do teto de tamanho, mais recentes primeiro.
    /// </summary>
    /// <inheritdoc />
    public IReadOnlyList<Attachment> SelectImageAttachments(IEnumerable<Attachment> items) =>
        SelectImageAttachmentsCore(items);

    /// <summary>Versão estática (testável sem instanciar o serviço).</summary>
    public static IReadOnlyList<Attachment> SelectImageAttachmentsCore(IEnumerable<Attachment> items)
    {
        if (items is null) return [];

        return items
            .Where(a => a is not null && !a.IsDeleted)
            .Where(a => !string.IsNullOrWhiteSpace(a.ContentType)
                        && AllowedContentTypes.Contains(NormalizeContentType(a.ContentType)))
            .Where(a => a.SizeBytes > 0 && a.SizeBytes <= MaxBytesPerImage)
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Take(MaxImagesPerPrompt)
            .ToList();
    }

    /// <summary>Normaliza o MIME (minúsculo, sem parâmetros como "; charset=...").</summary>
    public static string NormalizeContentType(string contentType)
    {
        var value = (contentType ?? string.Empty).Trim();
        var semicolon = value.IndexOf(';');
        if (semicolon >= 0) value = value[..semicolon];
        return value.Trim().ToLowerInvariant();
    }

    /// <summary>Monta a data URL do anexo; devolve null quando o tipo não é aceito.</summary>
    public static string? ToDataUrl(ReadOnlySpan<byte> content, string contentType)
    {
        if (content.Length == 0) return null;
        var mime = NormalizeContentType(contentType);
        if (!AllowedContentTypes.Contains(mime)) return null;
        return $"data:{mime};base64,{Convert.ToBase64String(content)}";
    }

    /// <summary>
    /// Lê os anexos de imagem do chamado e devolve as data URLs (no máximo
    /// <see cref="MaxImages"/>). Nunca lança: qualquer falha é logada e o
    /// resultado sai vazio.
    /// </summary>
    public async Task<IReadOnlyList<string>> LoadTicketImagesAsync(
        Guid ticketId, CancellationToken ct = default)
    {
        if (ticketId == Guid.Empty) return [];

        try
        {
            var all = await attachments.GetAttachmentsForEntityAsync("Ticket", ticketId, ct);
            var selected = SelectImageAttachments(all);
            if (selected.Count == 0) return [];

            var urls = new List<string>(selected.Count);
            foreach (var attachment in selected)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = await attachments.DownloadAttachmentAsync(attachment.Id, ct);
                    var bytes = await ReadCappedAsync(stream, MaxBytesPerImage, ct);
                    // SizeBytes é informado pelo cliente no upload; o tamanho real
                    // manda. Arquivo acima do teto depois de lido é descartado.
                    if (bytes is null) continue;

                    var dataUrl = ToDataUrl(bytes, attachment.ContentType);
                    if (dataUrl is not null) urls.Add(dataUrl);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex,
                        "Falha ao ler o anexo {AttachmentId} do chamado {TicketId} para a IA.",
                        attachment.Id, ticketId);
                }
            }

            return urls;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Falha ao listar anexos do chamado {TicketId} para a IA.", ticketId);
            return [];
        }
    }

    /// <summary>
    /// Lê o stream com teto de bytes: devolve null quando passa do limite (evita
    /// carregar arquivo gigante só para descobrir que não serve).
    /// </summary>
    private static async Task<byte[]?> ReadCappedAsync(
        Stream stream, long maxBytes, CancellationToken ct)
    {
        if (stream is null) return null;

        using var buffer = new MemoryStream(capacity: (int)Math.Min(maxBytes, 256 * 1024));
        var chunk = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }
}
