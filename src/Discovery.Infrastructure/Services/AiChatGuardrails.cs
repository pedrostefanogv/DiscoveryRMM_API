using System.Text.RegularExpressions;
using Discovery.Core.ValueObjects;

namespace Discovery.Infrastructure.Services;

public static class AiChatGuardrails
{
    private const int MaxMessageSizeBytes = 2048;

    public static void ValidateUserInput(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Mensagem nao pode ser vazia", nameof(message));
        var sizeBytes = System.Text.Encoding.UTF8.GetByteCount(message);
        if (sizeBytes > MaxMessageSizeBytes)
            throw new ArgumentException(
                string.Format("Mensagem excede o limite de {0} bytes (atual: {1} bytes)", MaxMessageSizeBytes, sizeBytes),
                nameof(message));
        var patterns = new[] {
            "<script[^>]*>", "javascript:", "eval\\s*\\(", "on\\w+\\s*=",
            "<iframe[^>]*>", "<object[^>]*>", "<embed[^>]*>"
        };
        foreach (var p in patterns)
            if (Regex.IsMatch(message, p, RegexOptions.IgnoreCase))
                throw new ArgumentException("Mensagem contem padroes nao permitidos", nameof(message));
    }

    public static string ApplyOutputGuardrails(string content, AIIntegrationSettings settings)
    {
        if (!settings.OutputGuardrailsEnabled || string.IsNullOrWhiteSpace(content)) return content;
        var result = content;
        result = Regex.Replace(result, "\\b(sk-[a-zA-Z0-9]{20,})\\b", "***REDACTED_API_KEY***", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, "\\b(eyJ[a-zA-Z0-9_-]{10,}\\.[a-zA-Z0-9_-]{10,}\\.[a-zA-Z0-9_-]{10,})\\b", "***REDACTED_JWT***");
        result = Regex.Replace(result, "(password|senha|passwd|secret|api[_-]?key)\\s*[:=]\\s*\\S+", "$1: ***REDACTED***", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, "\\b\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}\\b", "***.###.###-**");
        return result;
    }
}
