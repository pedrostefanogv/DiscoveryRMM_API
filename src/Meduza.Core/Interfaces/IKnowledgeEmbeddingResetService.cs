namespace Meduza.Core.Interfaces;

/// <summary>
/// Invalida todos os embeddings da base de conhecimento e prepara o banco para a nova dimensão de vetor.
/// Deve ser chamado sempre que EmbeddingDimensions mudar nas configurações de IA.
/// Após o reset, o KnowledgeEmbeddingBackgroundService reprocessará tudo automaticamente.
/// </summary>
public interface IKnowledgeEmbeddingResetService
{
    /// <summary>
    /// Invalida todos os embeddings armazenados, recria o índice HNSW e atualiza
    /// current_embedding_dimensions na configuração do servidor.
    /// </summary>
    Task ResetAsync(int newDimensions, string updatedBy, CancellationToken ct = default);
}
