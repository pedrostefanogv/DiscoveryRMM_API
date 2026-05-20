using FluentValidation;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Api.Validators;

public class CreateArticleRequestValidator : AbstractValidator<CreateArticleRequest>
{
    public CreateArticleRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Título é obrigatório.")
            .MaximumLength(500).WithMessage("Título deve ter no máximo 500 caracteres.");

        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Conteúdo é obrigatório.");

        RuleFor(x => x.Category)
            .MaximumLength(200).WithMessage("Categoria deve ter no máximo 200 caracteres.");

        RuleFor(x => x.Tags)
            .Must(tags => tags == null || tags.Count <= 10)
            .WithMessage("Máximo de 10 tags por artigo.");

        RuleForEach(x => x.Tags)
            .MaximumLength(50).WithMessage("Cada tag deve ter no máximo 50 caracteres.")
            .When(x => x.Tags != null);

        RuleFor(x => x.CreatedBy)
            .MaximumLength(256).WithMessage("CreatedBy deve ter no máximo 256 caracteres.");

        // Valida escopo: site_id só pode existir se client_id também existir
        RuleFor(x => x.SiteId)
            .Null().When(x => x.ClientId == null)
            .WithMessage("ClientId é obrigatório quando SiteId é informado.");
    }
}

public class UpdateArticleRequestValidator : AbstractValidator<UpdateArticleRequest>
{
    public UpdateArticleRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Título é obrigatório.")
            .MaximumLength(500).WithMessage("Título deve ter no máximo 500 caracteres.");

        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Conteúdo é obrigatório.");

        RuleFor(x => x.Category)
            .MaximumLength(200).WithMessage("Categoria deve ter no máximo 200 caracteres.");

        RuleFor(x => x.Tags)
            .Must(tags => tags == null || tags.Count <= 10)
            .WithMessage("Máximo de 10 tags por artigo.");

        RuleForEach(x => x.Tags)
            .MaximumLength(50).WithMessage("Cada tag deve ter no máximo 50 caracteres.")
            .When(x => x.Tags != null);

        RuleFor(x => x.LastEditedBy)
            .MaximumLength(256).WithMessage("LastEditedBy deve ter no máximo 256 caracteres.");
    }
}

public class PublishArticleRequestValidator : AbstractValidator<PublishArticleRequest>
{
    public PublishArticleRequestValidator()
    {
        RuleFor(x => x.Status)
            .NotEmpty().WithMessage("Status é obrigatório.")
            .Must(s => s == ArticleStatus.Published.ToString() || s == ArticleStatus.Internal.ToString())
            .WithMessage("Status deve ser 'Published' ou 'Internal'.");

        RuleFor(x => x.LastEditedBy)
            .MaximumLength(256).WithMessage("LastEditedBy deve ter no máximo 256 caracteres.");

        RuleFor(x => x.ChangeSummary)
            .MaximumLength(500).WithMessage("ChangeSummary deve ter no máximo 500 caracteres.");
    }
}

public class KbSearchRequestValidator : AbstractValidator<KbSearchRequest>
{
    public KbSearchRequestValidator()
    {
        RuleFor(x => x.Query)
            .NotEmpty().WithMessage("Query é obrigatória.")
            .MaximumLength(1000).WithMessage("Query deve ter no máximo 1000 caracteres.");

        RuleFor(x => x.Mode)
            .Must(m => m is "semantic" or "keyword" or "hybrid")
            .WithMessage("Mode deve ser 'semantic', 'keyword' ou 'hybrid'.");

        RuleFor(x => x.MaxResults)
            .InclusiveBetween(1, 50).WithMessage("MaxResults deve estar entre 1 e 50.");
    }
}
