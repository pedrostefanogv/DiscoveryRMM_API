using FluentValidation;
using Discovery.Api.Controllers;

namespace Discovery.Api.Validators;

public class SoftwareInventoryReportRequestValidator : AbstractValidator<SoftwareInventoryReportRequest>
{
    public SoftwareInventoryReportRequestValidator()
    {
        // Software pode ser null (agente pode enviar payload vazio)
        RuleFor(x => x.Software).NotNull();
        // Cada item é validado pelo validador filho
        RuleForEach(x => x.Software!).SetValidator(new SoftwareInventoryItemRequestValidator())
            .When(x => x.Software is not null);
    }
}

public class SoftwareInventoryItemRequestValidator : AbstractValidator<SoftwareInventoryItemRequest>
{
    public SoftwareInventoryItemRequestValidator()
    {
        // Name é obrigatório, mas não restringimos comprimento para evitar 400 inesperados.
        // Campos longos serão truncados no parser/repositório.
        RuleFor(x => x.Name).NotEmpty();
    }
}
