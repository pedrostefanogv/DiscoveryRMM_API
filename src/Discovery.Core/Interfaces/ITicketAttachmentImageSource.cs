using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Fonte de imagens anexadas a um chamado, prontas para o modelo multimodal
/// (triagem por IA). Devolve data URLs (data:image/...) já filtradas por tipo e
/// tamanho; nunca lança — falha de storage apenas omite a imagem.
/// </summary>
public interface ITicketAttachmentImageSource
{
    /// <summary>Máximo de imagens devolvidas por chamado.</summary>
    int MaxImages { get; }

    /// <summary>Seleciona os anexos elegíveis (imagem aceita, dentro do teto).</summary>
    IReadOnlyList<Attachment> SelectImageAttachments(IEnumerable<Attachment> items);

    /// <summary>Lê os anexos de imagem do chamado como data URLs.</summary>
    Task<IReadOnlyList<string>> LoadTicketImagesAsync(Guid ticketId, CancellationToken ct = default);
}
