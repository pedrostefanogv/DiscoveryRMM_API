using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Embeddings das respostas do questionário para busca semântica (pgvector),
/// + flag de sensibilidade (pergunta marcada como não-indexável).
///
/// Convenção de estado (evita reprocessar em loop respostas que não devem ser
/// embedadas): `embedding IS NULL AND embedding_generated_at IS NULL` = pendente;
/// `embedding_generated_at` preenchido = já processada (embedada ou pulada).
///
/// A coluna nasce com a MESMA dimensão configurada para a KB
/// (server_configurations.current_embedding_dimensions) e é realinhada pelo
/// KnowledgeEmbeddingResetService quando o modelo/dimensão muda.
/// </summary>
[Migration(20260926_168)]
public class M168_AddEmbeddingsToTicketAnswers : Migration
{
    public override void Up()
    {
        if (!Schema.Table("ticket_answers").Exists()) return;

        Execute.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

        // Dimensão alinhada com a KB (fallback 1536 se ainda não configurada).
        Execute.Sql(@"
            DO $$
            DECLARE dim int;
            BEGIN
                SELECT COALESCE(current_embedding_dimensions, 1536)
                  INTO dim
                  FROM server_configurations
                 LIMIT 1;

                IF dim IS NULL OR dim <= 0 THEN
                    dim := 1536;
                END IF;

                EXECUTE format(
                    'ALTER TABLE ticket_answers ADD COLUMN IF NOT EXISTS embedding vector(%s)',
                    dim);
            END
            $$;");

        if (!Schema.Table("ticket_answers").Column("embedding_generated_at").Exists())
        {
            Alter.Table("ticket_answers")
                .AddColumn("embedding_generated_at").AsCustom("timestamptz").Nullable();
        }

        // Pergunta sensível (nome/e-mail/CPF...): nunca vai ao provedor e nunca
        // aparece na busca semântica.
        if (!Schema.Table("ticket_answers").Column("is_sensitive").Exists())
        {
            Alter.Table("ticket_answers")
                .AddColumn("is_sensitive").AsBoolean().NotNullable().WithDefaultValue(false);
        }

        // Índice parcial: o ciclo do job consulta apenas o que está pendente.
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_ticket_answers_no_embedding
                ON ticket_answers (created_at)
                WHERE embedding IS NULL AND embedding_generated_at IS NULL");

        // HNSW para cosine distance (criado com a tabela vazia).
        //
        // O pgvector 0.6 (instalado) limita o índice HNSW a 2000 dimensões.
        // Com dimensões maiores (ex.: 4096), o CREATE INDEX falha com
        // "column cannot have more than 2000 dimensions for hnsw index", o que
        // derruba a migração e o processo inteiro no startup. Nesses casos o
        // índice é omitido de forma intencional: a busca semântica continua
        // correta usando varredura exata (operador cosine `<=>`), apenas sem o
        // ganho de performance da busca aproximada.
        Execute.Sql(@"
            DO $$
            DECLARE dim int;
            BEGIN
                SELECT COALESCE(current_embedding_dimensions, 1536)
                  INTO dim
                  FROM server_configurations
                 LIMIT 1;

                IF dim IS NULL OR dim <= 0 THEN
                    dim := 1536;
                END IF;

                IF dim <= 2000 THEN
                    EXECUTE 'CREATE INDEX IF NOT EXISTS ix_ticket_answers_embedding_hnsw
                                 ON ticket_answers
                                 USING hnsw (embedding vector_cosine_ops)
                                 WITH (m = 16, ef_construction = 64)';
                END IF;
            END
            $$;");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ix_ticket_answers_embedding_hnsw;");
        Execute.Sql("DROP INDEX IF EXISTS ix_ticket_answers_no_embedding;");

        foreach (var column in new[] { "is_sensitive", "embedding_generated_at", "embedding" })
        {
            if (Schema.Table("ticket_answers").Exists()
                && Schema.Table("ticket_answers").Column(column).Exists())
            {
                Delete.Column(column).FromTable("ticket_answers");
            }
        }
    }
}
