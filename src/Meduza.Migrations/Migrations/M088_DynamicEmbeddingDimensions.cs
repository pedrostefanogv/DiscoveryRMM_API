using FluentMigrator;

namespace Meduza.Migrations.Migrations;

/// <summary>
/// Prepara suporte a dimensões de embedding dinâmicas:
/// - Adiciona current_embedding_dimensions em server_configurations para rastrear qual dimensão está indexada
/// - Invalida os embeddings existentes (reprocessados pelo background service)
/// A alteração efetiva do tipo da coluna e recriação do índice é feita em runtime pelo
/// KnowledgeEmbeddingResetService quando o administrador muda EmbeddingDimensions nas configurações.
/// Nota: pgvector NÃO suporta criar índice HNSW/IVFFlat em coluna 'vector' sem dimensão definida.
/// A dimensão é sempre mantida explícita no tipo da coluna (ex: vector(1536), vector(3072)).
/// </summary>
[Migration(20260325_088)]
public class M088_DynamicEmbeddingDimensions : Migration
{
    public override void Up()
    {
        // Adiciona coluna de rastreamento da dimensão atual
        Alter.Table("server_configurations")
            .AddColumn("current_embedding_dimensions").AsInt32().NotNullable().WithDefaultValue(1536);

        // Invalida embeddings existentes para forçar reprocessamento com a configuração atual do banco
        Execute.Sql("UPDATE knowledge_article_chunks SET embedding = NULL, embedding_generated_at = NULL;");
    }

    public override void Down()
    {
        Execute.Sql("UPDATE knowledge_article_chunks SET embedding = NULL, embedding_generated_at = NULL;");
        Delete.Column("current_embedding_dimensions").FromTable("server_configurations");
    }
}
