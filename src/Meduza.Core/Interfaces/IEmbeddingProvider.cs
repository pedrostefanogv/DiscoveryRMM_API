namespace Meduza.Core.Interfaces;

public interface IEmbeddingProvider
{
    /// <summary>
    /// Gera embedding de texto via text-embedding-3-small (1536 dims)
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(string text, string? modelOverride = null, string? apiKeyOverride = null, CancellationToken ct = default);
}
