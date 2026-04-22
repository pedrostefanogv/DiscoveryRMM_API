using System.Text.Json;
using FluentValidation;
using Discovery.Api.Controllers;

namespace Discovery.Api.Validators;

public class CreateLogRequestValidator : AbstractValidator<CreateLogRequest>
{
    private const int MaxDataJsonLength = 65536;

    public CreateLogRequestValidator()
    {
        RuleFor(x => x)
            .Must(x => x.ClientId.HasValue || x.SiteId.HasValue || x.AgentId.HasValue)
            .WithMessage("clientId, siteId or agentId is required.");

        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Level).IsInEnum();
        RuleFor(x => x.Source).IsInEnum();
        RuleFor(x => x.Message).NotEmpty().Length(3, 4000);

        RuleFor(x => x.DataJson)
            .Must(BeReasonableJsonSize)
            .WithMessage($"DataJson must be <= {MaxDataJsonLength} characters.");
    }

    private static bool BeReasonableJsonSize(JsonElement? json)
    {
        if (!json.HasValue || json.Value.ValueKind == JsonValueKind.Null)
            return true;

        var raw = json.Value.GetRawText();
        return raw.Length <= MaxDataJsonLength;
    }
}
