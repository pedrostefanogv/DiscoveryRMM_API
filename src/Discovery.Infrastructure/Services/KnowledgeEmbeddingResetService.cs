using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Invalida todos os embeddings armazenados quando a dimensão do vetor muda:
/// 1. Zera embedding + embedding_generated_at de todos os chunks
/// 2. Dropa o índice HNSW existente
/// 3. ALTER COLUMN para o novo tipo vector(N) — pgvector exige dimensão fixa no índice HNSW
/// 4. Recria o índice HNSW com a nova dimensão
/// 5. Atualiza current_embedding_dimensions na ServerConfiguration
/// O KnowledgeEmbeddingBackgroundService reprocessará todos os chunks na próxima execução.
/// </summary>
public class KnowledgeEmbeddingResetService(
    DiscoveryDbContext db,
    IServerConfigurationRepository serverRepo,
    ILogger<KnowledgeEmbeddingResetService> logger) : IKnowledgeEmbeddingResetService
{
    public async Task ResetAsync(int newDimensions, string updatedBy, CancellationToken ct = default)
    {
        if (newDimensions <= 0 || newDimensions > 16000)
            throw new ArgumentOutOfRangeException(nameof(newDimensions), "Dimensão deve estar entre 1 e 16000.");

        logger.LogWarning(
            "Iniciando reset de embeddings da KB para nova dimensão={Dim}. Todos os chunks serão reprocessados.",
            newDimensions);

        // 1. Invalida todos os embeddings
        var affected = await db.Database.ExecuteSqlRawAsync(
            "UPDATE knowledge_article_chunks SET embedding = NULL, embedding_generated_at = NULL",
            ct);

        logger.LogInformation("Embeddings invalidados: {Count} chunks afetados.", affected);

        // 2. Dropa o índice HNSW existente (vinculado à dimensão anterior)
        await db.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS ix_kac_embedding_hnsw",
            ct);

        // 3. Altera a coluna para a nova dimensão
        // pgvector NÃO suporta índice HNSW em coluna 'vector' sem dimensão → sempre manter vector(N)
        await db.Database.ExecuteSqlRawAsync(
            FormattableString.Invariant($"ALTER TABLE knowledge_article_chunks ALTER COLUMN embedding TYPE vector({newDimensions}) USING NULL::vector({newDimensions})"),
            ct);

        // 4. Recria o índice HNSW para a nova dimensão (coluna agora está vazia — rápido)
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE INDEX ix_kac_embedding_hnsw
            ON knowledge_article_chunks
            USING hnsw (embedding vector_cosine_ops)
            WITH (m = 16, ef_construction = 64)",
            ct);

        // 5. Atualiza o rastreador de dimensão na configuração do servidor
        var server = await serverRepo.GetOrCreateDefaultAsync();
        server.CurrentEmbeddingDimensions = newDimensions;
        server.UpdatedAt = DateTime.UtcNow;
        server.UpdatedBy = updatedBy;
        await serverRepo.UpdateAsync(server);

        logger.LogInformation(
            "Reset concluído. current_embedding_dimensions atualizado para {Dim}. KB será reprocessada pelo background service.",
            newDimensions);
    }
}
