namespace Discovery.Core.Interfaces;

public interface IEmbeddingProvider
{
    /// <summary>
    /// Gera embedding de texto via API compatível com OpenAI.
    /// baseUrlOverride permite redirecionar a chamada para outro endpoint (ex: OpenAI direto quando o chat usa OpenRouter).
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(string text, string? modelOverride = null, string? apiKeyOverride = null, string? baseUrlOverride = null, CancellationToken ct = default);

    /// <summary>
    /// Gera embeddings em batch (um vetor por string de input).
    /// Muito mais eficiente que chamar GenerateEmbeddingAsync individualmente.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> inputs,
        string? modelOverride = null,
        string? apiKeyOverride = null,
        string? baseUrlOverride = null,
        CancellationToken ct = default);
}
