using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Knowledge.Commands;

public sealed record CreateKnowledgeArticleCommand(
    string Title,
    string Content,
    string? Category,
    List<string>? Tags,
    string? CreatedBy,
    Guid? ClientId,
    Guid? SiteId,
    Guid? DepartmentId = null,
    Guid? ParentId = null,
    int SortOrder = 0,
    bool IsPage = false
) : ICommand<Result<ArticleResponse>>;

public sealed record UpdateKnowledgeArticleCommand(
    Guid Id,
    string Title,
    string Content,
    string? Category,
    List<string>? Tags,
    string? LastEditedBy,
    Guid? ParentId = null,
    int SortOrder = 0,
    bool IsPage = false
) : ICommand<Result<ArticleResponse>>;

public sealed record PublishKnowledgeArticleCommand(
    Guid Id,
    string Status,          // "Published" ou "Internal"
    string? LastEditedBy,
    string? ChangeSummary = null
) : ICommand<Result<ArticleResponse>>;

public sealed record UnpublishKnowledgeArticleCommand(
    Guid Id,
    string? LastEditedBy
) : ICommand<Result<ArticleResponse>>;

public sealed record DeleteKnowledgeArticleCommand(Guid Id) : ICommand<Result<VoidResult>>;

public sealed record GetKnowledgeArticleVersionsQuery(Guid ArticleId) : IQuery<Result<IReadOnlyList<ArticleVersionResponse>>>;
