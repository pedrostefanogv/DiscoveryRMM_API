using System.Text.RegularExpressions;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Validação de input do usuário e sanitização de output do LLM (guardrails DLP).
/// </summary>
public static class AiChatGuardrails
{
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

        var patterns = new[]
        {
            @"<script[^>]*>",
            @"javascript:",
            @"eval\s*\(",
            @"on\w+\s*=",  // onclick=, onerror=, etc
            @"<iframe[^>]*>",
            @"<object[^>]*>",
            @"<embed[^>]*>"
        };

        foreach (var p in patterns)
            if (Regex.IsMatch(message, p, RegexOptions.IgnoreCase))
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

        // Detectar API keys no formato comum (sk-..., key-..., etc.)
        result = Regex.Replace(result,
            @"\b(sk-[a-zA-Z0-9]{20,})\b",
            "***REDACTED_API_KEY***",
            RegexOptions.IgnoreCase);

        // Detectar tokens JWT
        result = Regex.Replace(result,
            @"\b(eyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,})\b",
            "***REDACTED_JWT***");

        // Detectar senhas em padrão chave=valor
        result = Regex.Replace(result,
            @"(password|senha|passwd|secret|api[_-]?key)\s*[:=]\s*\S+",
            "$1: ***REDACTED***",
            RegexOptions.IgnoreCase);

        // Detectar CPF (com ou sem pontuação)
        result = Regex.Replace(result,
            @"\b(?:\d{3}\.\d{3}\.\d{3}-\d{2}|\d{11})\b",
            "***.###.###-**");

        // Detectar cartões de crédito (Visa, MasterCard, Amex, etc.)
        result = Regex.Replace(result,
            @"\b(?:\d{4}[ -]?){3}\d{4}\b",
            "****-****-****-****");

        return result;
    }
}

