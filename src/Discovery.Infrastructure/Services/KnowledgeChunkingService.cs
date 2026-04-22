using System.Text.RegularExpressions;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Divide artigos Markdown em chunks por seção (H1/H2/H3).
/// Artigos curtos (< 500 tokens estimados) ficam em 1 chunk.
/// Chunks grandes (> 800 tokens) são sub-divididos por parágrafo com overlap.
/// </summary>
public class KnowledgeChunkingService : IKnowledgeChunkingService
{
    private const int SmallArticleTokenThreshold = 500;
    private const int MaxChunkTokens = 800;
    private const int OverlapTokens = 50; // ~2 frases de overlap entre chunks consecutivos

    private static readonly Regex HeaderRegex =
        new(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public List<KnowledgeArticleChunk> ChunkArticle(KnowledgeArticle article)
    {
        var fullText = StripMarkdown(article.Content);
        var estimatedTotal = EstimateTokens(fullText);

        if (estimatedTotal <= SmallArticleTokenThreshold)
        {
            // Artigo pequeno: 1 único chunk
            return
            [
                new KnowledgeArticleChunk
                {
                    ChunkIndex = 0,
                    SectionTitle = null,
                    Content = fullText,
                    TokenCount = estimatedTotal
                }
            ];
        }

        // Dividir por headers H1/H2/H3
        var sections = SplitByHeaders(article.Content);
        var chunks = new List<KnowledgeArticleChunk>();
        string? prevOverlap = null;

        foreach (var (header, rawContent) in sections)
        {
            var plainContent = StripMarkdown(rawContent).Trim();
            if (string.IsNullOrWhiteSpace(plainContent)) continue;

            // Prefixar com overlap do chunk anterior
            var contentWithOverlap = prevOverlap != null
                ? prevOverlap + "\n\n" + plainContent
                : plainContent;

            var tokenCount = EstimateTokens(contentWithOverlap);

            if (tokenCount <= MaxChunkTokens)
            {
                chunks.Add(new KnowledgeArticleChunk
                {
                    ChunkIndex = chunks.Count,
                    SectionTitle = header,
                    Content = contentWithOverlap,
                    TokenCount = tokenCount
                });
            }
            else
            {
                // Sub-dividir por parágrafo
                var subChunks = SplitByParagraph(contentWithOverlap, header, chunks.Count);
                chunks.AddRange(subChunks);
            }

            // Calcular overlap para próximo chunk (últimas ~50 tokens ≈ últimas 2-3 frases)
            prevOverlap = ExtractOverlap(plainContent);
        }

        // Corrigir índices
        for (var i = 0; i < chunks.Count; i++)
            chunks[i].ChunkIndex = i;

        return chunks;
    }

    private static List<KnowledgeArticleChunk> SplitByParagraph(string content, string? sectionTitle, int startIndex)
    {
        var paragraphs = content
            .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var chunks = new List<KnowledgeArticleChunk>();
        var buffer = string.Empty;

        foreach (var para in paragraphs)
        {
            var candidate = string.IsNullOrEmpty(buffer) ? para : buffer + "\n\n" + para;
            if (EstimateTokens(candidate) > MaxChunkTokens && !string.IsNullOrEmpty(buffer))
            {
                chunks.Add(new KnowledgeArticleChunk
                {
                    ChunkIndex = startIndex + chunks.Count,
                    SectionTitle = sectionTitle,
                    Content = buffer.Trim(),
                    TokenCount = EstimateTokens(buffer)
                });
                buffer = para;
            }
            else
            {
                buffer = candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(buffer))
        {
            chunks.Add(new KnowledgeArticleChunk
            {
                ChunkIndex = startIndex + chunks.Count,
                SectionTitle = sectionTitle,
                Content = buffer.Trim(),
                TokenCount = EstimateTokens(buffer)
            });
        }

        return chunks;
    }

    /// <summary>
    /// Divide conteúdo Markdown nas seções delimitadas por headers.
    /// Retorna lista de (HeaderTitle, RawSectionContent).
    /// </summary>
    private static List<(string? Header, string Content)> SplitByHeaders(string markdown)
    {
        var result = new List<(string?, string)>();
        var matches = HeaderRegex.Matches(markdown);

        if (matches.Count == 0)
        {
            result.Add((null, markdown));
            return result;
        }

        // Conteúdo antes do primeiro header
        var firstMatchStart = matches[0].Index;
        if (firstMatchStart > 0)
        {
            var preHeaderContent = markdown[..firstMatchStart].Trim();
            if (!string.IsNullOrEmpty(preHeaderContent))
                result.Add((null, preHeaderContent));
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var header = matches[i].Groups[1].Value.Trim();
            var contentStart = matches[i].Index + matches[i].Length;
            var contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var content = markdown[contentStart..contentEnd].Trim();
            result.Add((header, content));
        }

        return result;
    }

    private static string StripMarkdown(string markdown)
    {
        // Remove headers
        var text = HeaderRegex.Replace(markdown, "$1");
        // Remove bold/italic
        text = Regex.Replace(text, @"\*{1,3}(.+?)\*{1,3}", "$1");
        text = Regex.Replace(text, @"_{1,3}(.+?)_{1,3}", "$1");
        // Remove inline code
        text = Regex.Replace(text, @"`{1,3}[^`]*`{1,3}", "[código]");
        // Remove links
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        // Remove imagens
        text = Regex.Replace(text, @"!\[([^\]]*)\]\([^\)]+\)", "");
        // Remove HTML tags
        text = Regex.Replace(text, @"<[^>]+>", "");
        // Normaliza espaços
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string ExtractOverlap(string text)
    {
        var words = text.Split(' ');
        if (words.Length <= OverlapTokens) return text;
        return string.Join(" ", words[^OverlapTokens..]);
    }

    // Estimativa simples: palavras * 1.3 (sem tokenizer exato)
    private static int EstimateTokens(string text)
        => (int)(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 1.3);
}
