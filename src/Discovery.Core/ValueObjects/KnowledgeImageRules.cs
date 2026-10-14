namespace Discovery.Core.ValueObjects;

/// <summary>
/// Regras fixas das imagens de artigos da base de conhecimento.
///
/// Diferente dos anexos de ticket (configuráveis em ServerConfiguration), as
/// imagens da KB têm limites fechados de propósito: o caso de uso é foto de
/// procedimento (print de tela, foto de equipamento), não repositório de
/// arquivos. Sem configuração nova não há migração nem tela adicional — o
/// storage em si continua vindo de ObjectStorageProviderFactory (interno RustFS
/// ou externo S3-compatível).
/// </summary>
public static class KnowledgeImageRules
{
    /// <summary>
    /// EntityType gravado em attachments.entity_type. `EntityId` é o ID do
    /// ARTIGO (não da sub-página): assim home e sub-páginas compartilham o mesmo
    /// conjunto de imagens e a checagem de propriedade é sempre no artigo.
    /// </summary>
    public const string EntityType = "KnowledgeImage";

    /// <summary>Esquema do token gravado no Markdown: ![alt](kbimage:&lt;guid&gt;).</summary>
    public const string MarkdownScheme = "kbimage";

    /// <summary>Teto por imagem. Imagem de procedimento acima disso é ruído.</summary>
    public const long MaxFileSizeBytes = 5L * 1024 * 1024; // 5 MB

    /// <summary>TTL da URL pré-assinada de UPLOAD (PUT direto no storage).</summary>
    public const int UploadUrlTtlMinutes = 15;

    /// <summary>
    /// TTL da URL pré-assinada de LEITURA. Curto o bastante para o link vazado
    /// (DevTools/histórico/proxy) perder validade rápido, e longo o bastante
    /// para o usuário ler o artigo sem o link expirar no meio.
    /// </summary>
    public const int ReadUrlTtlHours = 4;

    /// <summary>Tipos aceitos. Sem SVG (vetor é executável no navegador).</summary>
    public static readonly string[] AllowedContentTypes =
    [
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    ];

    public static bool IsAllowedContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var normalized = contentType.Trim();
        return AllowedContentTypes.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAllowedSize(long sizeBytes) => sizeBytes > 0 && sizeBytes <= MaxFileSizeBytes;

    /// <summary>
    /// Token canônico inserido no Markdown. GUID sempre em minúsculas para o
    /// regex do console/agente e a checagem de "já referenciada" baterem.
    /// </summary>
    /// <param name="size">
    /// Tamanho opcional ("50%" ou "420"), gravado no título do Markdown — o
    /// Markdown não tem atributo de largura e HTML cru não é renderizado pelo
    /// viewer. Valor fora do padrão é ignorado (largura natural).
    /// </param>
    public static string BuildMarkdownToken(string? altText, Guid attachmentId, string? size = null)
    {
        // Sem colchetes (fechariam o alt e quebrariam a sintaxe) e sem quebra de
        // linha (um alt multilinha geraria markdown atípico). Parênteses são
        // inofensivos dentro do alt.
        var alt = (altText ?? string.Empty)
            .Replace("[", string.Empty)
            .Replace("]", string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        var normalizedSize = Discovery.Core.Helpers.KnowledgeImageMarkdown.NormalizeSize(size);
        var title = normalizedSize is null ? string.Empty : $" \"w={normalizedSize}\"";

        return $"![{alt}]({MarkdownScheme}:{attachmentId:D}{title})";
    }
}
