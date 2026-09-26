using System.Text.RegularExpressions;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Divide artigos Markdown em chunks com estrategias configuraveis.
/// Suporta: "semantic" (headers H1/H2/H3), "paragraph", "fixed".
/// Artigos curtos (< 500 tokens) ficam em 1 chunk.
/// </summary>
public class KnowledgeChunkingService : IKnowledgeChunkingService
{
    private const int SmallArticleTokenThreshold = 500;
    private const int DefaultChunkSizeTokens = 300;
    private const int DefaultOverlapTokens = 50;

    private static readonly Regex HeaderRegex =
        new(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public List<KnowledgeArticleChunk> ChunkArticle(
        KnowledgeArticle article,
        IReadOnlyList<KnowledgeArticlePage>? pages = null)
        => ChunkArticleWithStrategy(article, "semantic", DefaultChunkSizeTokens, DefaultOverlapTokens, pages);

    public List<KnowledgeArticleChunk> ChunkArticleWithStrategy(
        KnowledgeArticle article,
        string strategy,
        int chunkSizeTokens,
        int overlapTokens)
        => ChunkArticleWithStrategy(article, strategy, chunkSizeTokens, overlapTokens, pages: null);

    private List<KnowledgeArticleChunk> ChunkArticleWithStrategy(
        KnowledgeArticle article,
        string strategy,
        int chunkSizeTokens,
        int overlapTokens,
        IReadOnlyList<KnowledgeArticlePage>? pages)
    {
        var maxChunk = chunkSizeTokens > 0 ? chunkSizeTokens : DefaultChunkSizeTokens;
        var overlap = Math.Clamp(overlapTokens, 0, maxChunk / 2);

        // Fonte = artigo + sub-páginas (título da página vira header → SectionTitle).
        var source = ComposeSourceMarkdown(article, pages);

        var fullText = StripMarkdown(source);
        var estimatedTotal = EstimateTokens(fullText);

        if (estimatedTotal <= SmallArticleTokenThreshold)
            return [new KnowledgeArticleChunk { ChunkIndex = 0, SectionTitle = null, Content = fullText, TokenCount = estimatedTotal }];

        var chunks = strategy.ToLowerInvariant() switch
        {
            "paragraph" => SplitByParagraph(fullText, null, 0, maxChunk, overlap),
            "fixed" => SplitFixedSize(fullText, null, maxChunk, overlap),
            _ => SplitByHeadersStrategy(source, maxChunk, overlap),
        };

        for (var i = 0; i < chunks.Count; i++)
            chunks[i].ChunkIndex = i;

        return chunks;
    }

    /// <summary>
    /// Compõe o markdown indexável: conteúdo do artigo + sub-páginas em ordem de
    /// árvore, com o título da página como header (## / ###). Sem pages, é o
    /// próprio conteúdo do artigo (comportamento anterior preservado).
    /// </summary>
    private static string ComposeSourceMarkdown(
        KnowledgeArticle article,
        IReadOnlyList<KnowledgeArticlePage>? pages)
    {
        var content = article.Content ?? string.Empty;
        if (pages is not { Count: > 0 })
            return content;

        var byParent = pages
            .GroupBy(p => p.ParentPageId ?? Guid.Empty)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(p => p.SortOrder)
                      .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                      .ToList());

        var sb = new System.Text.StringBuilder(content);

        void Append(Guid parentId, int depth)
        {
            if (!byParent.TryGetValue(parentId, out var children)) return;
            foreach (var page in children)
            {
                // Depth 0 → "##", 1 → "###", 2+ → "###" (HeaderRegex só aceita 1-3 #).
                var level = Math.Clamp(depth + 2, 2, 3);
                sb.Append("\n\n")
                  .Append(new string('#', level)).Append(' ').Append(page.Title)
                  .Append("\n\n").Append(page.Content);
                Append(page.Id, depth + 1);
            }
        }

        Append(Guid.Empty, 0);
        return sb.ToString();
    }

    private List<KnowledgeArticleChunk> SplitByHeadersStrategy(string markdown, int maxTokens, int overlapTokens)
    {
        var sections = SplitByHeaders(markdown);
        var chunks = new List<KnowledgeArticleChunk>();
        string? prevOverlap = null;

        foreach (var (header, rawContent) in sections)
        {
            var plainContent = StripMarkdown(rawContent).Trim();
            if (string.IsNullOrWhiteSpace(plainContent)) continue;

            var contentWithOverlap = prevOverlap != null
                ? prevOverlap + "\n\n" + plainContent
                : plainContent;

            var tokenCount = EstimateTokens(contentWithOverlap);

            if (tokenCount <= maxTokens)
            {
                chunks.Add(new KnowledgeArticleChunk { ChunkIndex = chunks.Count, SectionTitle = header, Content = contentWithOverlap, TokenCount = tokenCount });
            }
            else
            {
                var sub = SplitByParagraph(contentWithOverlap, header, chunks.Count, maxTokens, overlapTokens);
                chunks.AddRange(sub);
            }

            prevOverlap = ExtractOverlap(plainContent, overlapTokens / 2);
        }

        return chunks;
    }

    private static List<KnowledgeArticleChunk> SplitByParagraph(string content, string? title, int startIdx, int maxTokens, int overlap)
    {
        var paragraphs = content.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return BuildChunks(paragraphs, title, startIdx, maxTokens);
    }

    private static List<KnowledgeArticleChunk> BuildChunks(List<string> paragraphs, string? title, int startIdx, int maxTokens)
    {
        var chunks = new List<KnowledgeArticleChunk>();
        var buffer = string.Empty;

        foreach (var para in paragraphs)
        {
            var candidate = string.IsNullOrEmpty(buffer) ? para : buffer + "\n\n" + para;
            if (EstimateTokens(candidate) > maxTokens && !string.IsNullOrEmpty(buffer))
            {
                chunks.Add(new KnowledgeArticleChunk { ChunkIndex = startIdx + chunks.Count, SectionTitle = title, Content = buffer.Trim(), TokenCount = EstimateTokens(buffer) });
                buffer = para;
            }
            else { buffer = candidate; }
        }

        if (!string.IsNullOrWhiteSpace(buffer))
            chunks.Add(new KnowledgeArticleChunk { ChunkIndex = startIdx + chunks.Count, SectionTitle = title, Content = buffer.Trim(), TokenCount = EstimateTokens(buffer) });

        return chunks;
    }

    private static List<KnowledgeArticleChunk> SplitFixedSize(string content, string? title, int maxTokens, int overlap)
    {
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<KnowledgeArticleChunk>();
        var i = 0; var idx = 0;
        var wpc = (int)(maxTokens / 1.3);

        while (i < words.Length)
        {
            var end = Math.Min(i + wpc, words.Length);
            var text = string.Join(" ", words[i..end]);
            chunks.Add(new KnowledgeArticleChunk { ChunkIndex = idx++, SectionTitle = title, Content = text, TokenCount = EstimateTokens(text) });
            i = end - Math.Min(overlap / 2, end - i);
        }

        return chunks;
    }

    private static List<(string? Header, string Content)> SplitByHeaders(string markdown)
    {
        var result = new List<(string?, string)>();
        var matches = HeaderRegex.Matches(markdown);

        if (matches.Count == 0) { result.Add((null, markdown)); return result; }

        var first = matches[0].Index;
        if (first > 0) { var pre = markdown[..first].Trim(); if (pre.Length > 0) result.Add((null, pre)); }

        for (var i = 0; i < matches.Count; i++)
        {
            var h = matches[i].Groups[1].Value.Trim();
            var s = matches[i].Index + matches[i].Length;
            var e = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            result.Add((h, markdown[s..e].Trim()));
        }

        return result;
    }

    private static string StripMarkdown(string md)
    {
        var t = HeaderRegex.Replace(md, "$1");
        t = Regex.Replace(t, @"\*{1,3}(.+?)\*{1,3}", "$1");
        t = Regex.Replace(t, @"_{1,3}(.+?)_{1,3}", "$1");
        t = Regex.Replace(t, @"`{1,3}[^`]*`{1,3}", "[codigo]");
        t = Regex.Replace(t, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        t = Regex.Replace(t, @"!\[[^\]]*\]\([^\)]+\)", "");
        t = Regex.Replace(t, @"<[^>]+>", "");
        t = Regex.Replace(t, @"[ \t]+", " ");
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Trim();
    }

    private static string ExtractOverlap(string text, int tokenCount)
    {
        var words = text.Split(' ');
        return words.Length <= tokenCount ? text : string.Join(" ", words[^tokenCount..]);
    }

    public static int EstimateTokens(string text)
        => (int)(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length * 1.3);
}
