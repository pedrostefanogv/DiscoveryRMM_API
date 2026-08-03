using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Knowledge.Commands;

// ─── Sub-páginas internas do artigo (estilo Notion) ────────────────

/// <summary>Cria uma sub-página interna em um artigo.</summary>
public sealed record CreateArticlePageCommand(
    Guid ArticleId,
    string Title,
    string Content,
    Guid? ParentPageId = null,
    int SortOrder = 0
) : ICommand<Result<ArticlePageResponse>>;

/// <summary>Atualiza uma sub-página interna.</summary>
public sealed record UpdateArticlePageCommand(
    Guid ArticleId,
    Guid PageId,
    string Title,
    string Content,
    Guid? ParentPageId = null,
    int SortOrder = 0
) : ICommand<Result<ArticlePageResponse>>;

/// <summary>Exclui uma sub-página interna (e suas sub-páginas filhas).</summary>
public sealed record DeleteArticlePageCommand(
    Guid ArticleId,
    Guid PageId
) : ICommand<Result<VoidResult>>;
