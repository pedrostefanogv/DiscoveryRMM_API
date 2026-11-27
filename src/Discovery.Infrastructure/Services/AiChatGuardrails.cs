using System.Text.RegularExpressions;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Validação de input do usuário e sanitização de output do LLM (guardrails DLP).
/// </summary>
public static partial class AiChatGuardrails
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    [GeneratedRegex(@"<script[^>]*>|javascript:|eval\s*\(|\bon(?:click|dblclick|mousedown|mouseup|mouseover|mouseout|mousemove|mouseenter|mouseleave|focus|blur|change|input|submit|reset|keydown|keyup|keypress|load|unload|error|resize|scroll|contextmenu|drag|drop)\s*=|<iframe[^>]*>|<object[^>]*>|<embed[^>]*>", RegexOptions.IgnoreCase, 200)]
    private static partial Regex MaliciousInputRegex();

    [GeneratedRegex(@"\b((?:sk-(?:proj-|or-v1-)?[a-zA-Z0-9_-]{20,})|(?:ghp_[a-zA-Z0-9]{20,})|(?:AKIA[0-9A-Z]{16}))\b", RegexOptions.IgnoreCase, 200)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"\b(eyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,})\b", RegexOptions.None, 200)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(password|senha|passwd|secret|api[_-]?key)\s*[:=]\s*\S+", RegexOptions.IgnoreCase, 200)]
    private static partial Regex SecretAssignRegex();

    [GeneratedRegex(@"\b(?:\d{3}\.\d{3}\.\d{3}-\d{2}|\d{11})\b", RegexOptions.None, 200)]
    private static partial Regex CpfCandidateRegex();

    [GeneratedRegex(@"\b(?:\d{4}[ -]?){3}\d{4}\b", RegexOptions.None, 200)]
    private static partial Regex CreditCardRegex();

    /// <summary>
    /// Rejeita: vazio, > maxMessageSizeBytes, padrões maliciosos
    /// </summary>
    public static void ValidateUserInput(string message, int maxMessageSizeBytes = 4096)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Mensagem não pode ser vazia", nameof(message));

        var sizeBytes = System.Text.Encoding.UTF8.GetByteCount(message);
        if (sizeBytes > maxMessageSizeBytes)
            throw new ArgumentException(
                $"Mensagem excede o limite de {maxMessageSizeBytes} bytes (atual: {sizeBytes} bytes)",
                nameof(message));

        if (MaliciousInputRegex().IsMatch(message))
            throw new ArgumentException(
                "Mensagem contém padrões não permitidos", nameof(message));
    }

    /// <summary>
    /// Guardrails de saída: detecta e redige PII/secrets na resposta do LLM.
    /// </summary>
    public static string ApplyOutputGuardrails(string content, AIIntegrationSettings settings)
    {
        if (!settings.OutputGuardrailsEnabled || string.IsNullOrWhiteSpace(content))
            return content;

        var result = content;

        // Detectar API keys no formato comum (sk-..., sk-proj-..., sk-or-v1-..., ghp_..., AKIA...)
        result = ApiKeyRegex().Replace(result, "***REDACTED_API_KEY***");

        // Detectar tokens JWT
        result = JwtRegex().Replace(result, "***REDACTED_JWT***");

        // Detectar senhas em padrão chave=valor
        result = SecretAssignRegex().Replace(result, "$1: ***REDACTED***");

        // Detectar CPF válido (com validação de dígito verificador para não censurar celulares/seriais)
        result = CpfCandidateRegex().Replace(result, m =>
            IsValidCpf(m.Value) ? "***.###.###-**" : m.Value);

        // Detectar cartões de crédito (Visa, MasterCard, Amex, etc.)
        result = CreditCardRegex().Replace(result, "****-****-****-****");

        return result;
    }

    private static bool IsValidCpf(string candidate)
    {
        Span<int> d = stackalloc int[11];
        var len = 0;
        foreach (var ch in candidate)
        {
            if (char.IsDigit(ch))
            {
                if (len >= 11) return false;
                d[len++] = ch - '0';
            }
        }
        if (len != 11) return false;

        var allSame = true;
        for (var i = 1; i < 11; i++)
        {
            if (d[i] != d[0]) { allSame = false; break; }
        }
        if (allSame) return false;

        var sum1 = 0;
        for (var i = 0; i < 9; i++) sum1 += d[i] * (10 - i);
        var rem1 = (sum1 * 10) % 11;
        if (rem1 == 10) rem1 = 0;
        if (rem1 != d[9]) return false;

        var sum2 = 0;
        for (var i = 0; i < 10; i++) sum2 += d[i] * (11 - i);
        var rem2 = (sum2 * 10) % 11;
        if (rem2 == 10) rem2 = 0;
        return rem2 == d[10];
    }
}

