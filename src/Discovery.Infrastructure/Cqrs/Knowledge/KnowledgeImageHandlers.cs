using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Knowledge.Commands;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Knowledge;

/// <summary>
/// Exclusão de imagem de artigo: soft delete do anexo (o objeto no storage é
/// removido depois pelo job de retenção) + limpeza da referência no Markdown do
/// artigo e de todas as sub-páginas que a usavam.
/// </summary>
public sealed class DeleteKnowledgeImageCommandHandler(
    IAttachmentRepository attachmentRepository,
    IAttachmentService attachmentService,
    IKnowledgeArticleRepository articleRepository,
    IKnowledgeArticlePageRepository pageRepository
) : IRequestHandler<DeleteKnowledgeImageCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteKnowledgeImageCommand cmd, CancellationToken ct)
    {
        var attachment = await attachmentRepository.GetByIdAsync(cmd.AttachmentId, ct);

        // Mesma checagem de propriedade usada no download: anexo de outro
        // artigo (IDOR) ou já removido não é excluível por esta rota.
        if (attachment is null
            || attachment.IsDeleted
            || !string.Equals(attachment.EntityType, KnowledgeImageRules.EntityType, StringComparison.OrdinalIgnoreCase)
            || attachment.EntityId != cmd.ArticleId)
        {
            return Result<VoidResult>.Failure(
                Error.NotFound($"Image {cmd.AttachmentId} not found in article {cmd.ArticleId}"));
        }

        // ORDEM IMPORTA: o ANEXO primeiro, o CONTEÚDO depois. Se o storage falhar
        // no hard delete, nada foi alterado — o artigo mantém a referência e o
        // anexo continua utilizável para nova tentativa. Ao contrário, remover a
        // referência antes deixaria uma mutação já gravada no artigo (órfã) sob um
        // erro 503/500.
        if (cmd.Permanent)
        {
            // Sem recuperação: some do storage e do banco (revoga também os links
            // pré-assinados vigentes, porque o arquivo deixa de existir).
            var removed = await attachmentService.PermanentlyDeleteAttachmentAsync(cmd.AttachmentId, ct);
            if (!removed)
                return Result<VoidResult>.Failure(Error.NotFound($"Image {cmd.AttachmentId} not found"));

            await RemoveReferencesAsync(cmd, ct);
            return Result<VoidResult>.Success(VoidResult.Value);
        }

        await attachmentService.DeleteAttachmentAsync(cmd.AttachmentId, ct);
        await RemoveReferencesAsync(cmd, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }

    private async Task RemoveReferencesAsync(DeleteKnowledgeImageCommand cmd, CancellationToken ct)
    {
        if (!cmd.RemoveReferences) return;

        await RemoveFromArticleAsync(cmd.ArticleId, cmd.AttachmentId, ct);
        await RemoveFromPagesAsync(cmd.ArticleId, cmd.AttachmentId, ct);
    }

    private async Task RemoveFromArticleAsync(Guid articleId, Guid attachmentId, CancellationToken ct)
    {
        var article = await articleRepository.GetByIdAsync(articleId, ct);
        if (article is null) return;

        var updated = KnowledgeImageMarkdown.RemoveToken(article.Content, attachmentId);
        if (string.Equals(updated, article.Content, StringComparison.Ordinal)) return;

        article.Content = updated;
        article.UpdatedAt = DateTime.UtcNow;
        await articleRepository.UpdateAsync(article, ct);
    }

    private async Task RemoveFromPagesAsync(Guid articleId, Guid attachmentId, CancellationToken ct)
    {
        var pages = await pageRepository.ListByArticleAsync(articleId, ct);
        foreach (var page in pages)
        {
            var updated = KnowledgeImageMarkdown.RemoveToken(page.Content, attachmentId);
            if (string.Equals(updated, page.Content, StringComparison.Ordinal)) continue;

            page.Content = updated;
            page.UpdatedAt = DateTime.UtcNow;
            await pageRepository.UpdateAsync(page, ct);
        }
    }
}
