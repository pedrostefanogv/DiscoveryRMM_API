using System.Text.Json;
using System.Text.RegularExpressions;
using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

/// <summary>
/// Pergunta do mini questionário de um template de chamado. NÃO é um campo do
/// chamado: as respostas são coletadas na abertura e registradas no snapshot.
/// </summary>
public sealed record TicketTemplateQuestion(
    string Key,
    string Label,
    CustomFieldDataType DataType,
    bool IsRequired,
    IReadOnlyList<string> Options,
    string? ValidationRegex,
    string? InputMask,
    int? MinLength,
    int? MaxLength,
    decimal? MinValue,
    decimal? MaxValue,
    string? HelpText);

public sealed record TicketTemplateAnswerError(string Key, string Message);

/// <summary>Parse/serialização e validação das perguntas do template.</summary>
public static class TicketTemplateQuestions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<TicketTemplateQuestion> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<TicketTemplateQuestion>();
        try
        {
            var items = JsonSerializer.Deserialize<List<TicketTemplateQuestion>>(json, JsonOptions);
            return items?
                .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Label))
                .Select(Normalize)
                .ToList() ?? (IReadOnlyList<TicketTemplateQuestion>)Array.Empty<TicketTemplateQuestion>();
        }
        catch (JsonException)
        {
            return Array.Empty<TicketTemplateQuestion>();
        }
    }

    public static string Serialize(IReadOnlyList<TicketTemplateQuestion>? questions)
    {
        if (questions is null || questions.Count == 0) return "[]";
        var clean = questions
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Label))
            .Select(Normalize)
            .ToList();
        return clean.Count == 0 ? "[]" : JsonSerializer.Serialize(clean, JsonOptions);
    }

    /// <summary>
    /// Valida a definição das perguntas antes de salvar o template (chave única,
    /// opções, limites e regex). Evita regex inválida cadastrada que quebraria a
    /// criação do chamado.
    /// </summary>
    public static IReadOnlyList<TicketTemplateAnswerError> ValidateDefinitions(
        IReadOnlyList<TicketTemplateQuestion> questions)
    {
        var errors = new List<TicketTemplateAnswerError>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.Key))
            {
                errors.Add(new TicketTemplateAnswerError(string.Empty, "Pergunta sem chave."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(question.Label))
            {
                errors.Add(new TicketTemplateAnswerError(question.Key, $"Pergunta '{question.Key}' sem rótulo."));
                continue;
            }
            if (!seenKeys.Add(question.Key))
            {
                errors.Add(new TicketTemplateAnswerError(question.Key, $"Chave de pergunta duplicada: '{question.Key}'."));
                continue;
            }

            if (question.DataType is CustomFieldDataType.Dropdown or CustomFieldDataType.ListBox
                && question.Options.Count == 0)
            {
                errors.Add(new TicketTemplateAnswerError(
                    question.Key, $"'{question.Label}': selecione pelo menos uma opção."));
            }

            if (question.MinLength is < 0 || question.MaxLength is < 0)
                errors.Add(new TicketTemplateAnswerError(question.Key, $"'{question.Label}': tamanho não pode ser negativo."));
            if (question.MinLength.HasValue && question.MaxLength.HasValue && question.MinLength > question.MaxLength)
                errors.Add(new TicketTemplateAnswerError(question.Key, $"'{question.Label}': tamanho mínimo maior que o máximo."));
            if (question.MinValue.HasValue && question.MaxValue.HasValue && question.MinValue > question.MaxValue)
                errors.Add(new TicketTemplateAnswerError(question.Key, $"'{question.Label}': valor mínimo maior que o máximo."));

            if (!string.IsNullOrWhiteSpace(question.ValidationRegex))
            {
                try
                {
                    _ = new Regex(question.ValidationRegex);
                }
                catch (ArgumentException)
                {
                    errors.Add(new TicketTemplateAnswerError(
                        question.Key, $"'{question.Label}': regex de validação inválida."));
                }
            }
        }

        return errors;
    }

    public static IReadOnlyList<TicketTemplateAnswerError> ValidateAnswers(
        IReadOnlyList<TicketTemplateQuestion> questions,
        IReadOnlyDictionary<string, JsonElement>? answers)
    {
        var errors = new List<TicketTemplateAnswerError>();
        var map = answers ?? new Dictionary<string, JsonElement>();

        foreach (var question in questions)
        {
            var has = map.TryGetValue(question.Key, out var value);
            var isBlank = !has
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

            if (isBlank)
            {
                if (question.IsRequired)
                    errors.Add(new TicketTemplateAnswerError(question.Key, $"'{question.Label}' é obrigatório."));
                continue;
            }

            try
            {
                ValidateValue(question, value);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new TicketTemplateAnswerError(question.Key, $"'{question.Label}': {ex.Message}"));
            }
        }

        return errors;
    }

    public static string FormatAnswer(TicketTemplateQuestion question, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return string.Empty;
            case JsonValueKind.True:
                return "Sim";
            case JsonValueKind.False:
                return "Não";
            case JsonValueKind.Array:
                return string.Join(", ", value.EnumerateArray().Select(item => item.ToString()));
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            default:
                return value.ToString();
        }
    }

    private static TicketTemplateQuestion Normalize(TicketTemplateQuestion question) => question with
    {
        Key = question.Key.Trim(),
        Label = question.Label.Trim(),
        Options = question.Options?
            .Select(option => (option ?? string.Empty).Trim())
            .Where(option => option.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
        ValidationRegex = string.IsNullOrWhiteSpace(question.ValidationRegex) ? null : question.ValidationRegex.Trim(),
        InputMask = string.IsNullOrWhiteSpace(question.InputMask) ? null : question.InputMask.Trim(),
        HelpText = string.IsNullOrWhiteSpace(question.HelpText) ? null : question.HelpText.Trim(),
    };

    private static void ValidateValue(TicketTemplateQuestion question, JsonElement value)
    {
        switch (question.DataType)
        {
            case CustomFieldDataType.Text:
                ValidateText(question, value);
                break;
            case CustomFieldDataType.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var integer))
                    throw new InvalidOperationException("espera um número inteiro.");
                if (question.MinValue.HasValue && integer < (double)question.MinValue.Value)
                    throw new InvalidOperationException($"valor mínimo é {question.MinValue}.");
                if (question.MaxValue.HasValue && integer > (double)question.MaxValue.Value)
                    throw new InvalidOperationException($"valor máximo é {question.MaxValue}.");
                break;
            case CustomFieldDataType.Decimal:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
                    throw new InvalidOperationException("espera um número.");
                if (question.MinValue.HasValue && number < question.MinValue.Value)
                    throw new InvalidOperationException($"valor mínimo é {question.MinValue}.");
                if (question.MaxValue.HasValue && number > question.MaxValue.Value)
                    throw new InvalidOperationException($"valor máximo é {question.MaxValue}.");
                break;
            case CustomFieldDataType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidOperationException("espera Sim ou Não.");
                break;
            case CustomFieldDataType.Date:
            case CustomFieldDataType.DateTime:
                if (value.ValueKind != JsonValueKind.String || !DateTime.TryParse(value.GetString(), out _))
                    throw new InvalidOperationException("espera uma data válida.");
                break;
            case CustomFieldDataType.Dropdown:
                if (value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("espera um valor de texto.");
                if (question.Options.Count > 0 &&
                    !question.Options.Contains(value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("valor não é uma opção válida.");
                break;
            case CustomFieldDataType.ListBox:
                if (value.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("espera uma lista de valores.");
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("itens devem ser texto.");
                    if (question.Options.Count > 0 &&
                        !question.Options.Contains(item.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidOperationException("um ou mais valores não são opções válidas.");
                }
                break;
            default:
                break;
        }
    }

    private static void ValidateText(TicketTemplateQuestion question, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("espera um valor de texto.");

        var text = value.GetString() ?? string.Empty;
        if (question.MinLength.HasValue && text.Length < question.MinLength.Value)
            throw new InvalidOperationException($"texto deve ter no mínimo {question.MinLength} caracteres.");
        if (question.MaxLength.HasValue && text.Length > question.MaxLength.Value)
            throw new InvalidOperationException($"texto deve ter no máximo {question.MaxLength} caracteres.");

        if (!string.IsNullOrWhiteSpace(question.ValidationRegex))
        {
            try
            {
                if (!Regex.IsMatch(text, question.ValidationRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    throw new InvalidOperationException("valor não corresponde ao formato esperado.");
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InvalidOperationException("timeout na validação do formato.");
            }
            catch (ArgumentException)
            {
                // Regex inválida cadastrada no template: não derruba a criação.
                throw new InvalidOperationException("formato de validação inválido.");
            }
        }
    }
}
