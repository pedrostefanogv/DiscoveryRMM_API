using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Backfill único para o conteúdo das sub-páginas passar a ser indexado.
/// O chunking agora inclui as sub-páginas (estilo Notion) dos artigos; artigos
/// que já tinham sido chunkados antes dessa mudança precisam ser re-chunkados.
/// Marcar last_chunked_at = NULL faz o KnowledgeEmbeddingJob reprocessá-los no
/// próximo ciclo. Somente artigos que realmente possuem sub-páginas.
/// </summary>
[Migration(20260927_170)]
public class M170_BackfillChunkArticlesWithPages : Migration
{
    public override void Up()
    {
        // Ignora silenciosamente se a tabela de páginas ainda não existir
        // (ambientes antigos antes das migrations M141/M142).
        Execute.Sql(@"
            DO $$
            BEGIN
                IF to_regclass('public.knowledge_article_pages') IS NOT NULL THEN
                    UPDATE knowledge_articles
                    SET last_chunked_at = NULL
                    WHERE deleted_at IS NULL
                      AND id IN (SELECT DISTINCT article_id FROM knowledge_article_pages);
                END IF;
            END $$;
        ");
    }

    public override void Down()
    {
        // No-op: dado derivado (chunks/embeddings) reconstruído pelo job.
    }
}
