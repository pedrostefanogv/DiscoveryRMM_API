using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260311_053)]
public class M053_CreateKnowledgeBase : Migration
{
    public override void Up()
    {
        // Habilita extensão pgvector (idempotente)
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

        // ── knowledge_articles ──────────────────────────────────────
        Create.Table("knowledge_articles")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable()
                .ForeignKey("fk_ka_client", "clients", "id")
            .WithColumn("site_id").AsGuid().Nullable()
                .ForeignKey("fk_ka_site", "sites", "id")
            .WithColumn("title").AsString(500).NotNullable()
            .WithColumn("content").AsCustom("text").NotNullable()
            .WithColumn("category").AsString(200).Nullable()
            .WithColumn("tags_json").AsCustom("jsonb").Nullable()
            .WithColumn("author").AsString(256).Nullable()
            .WithColumn("is_published").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("published_at").AsCustom("timestamptz").Nullable()
            .WithColumn("last_chunked_at").AsCustom("timestamptz").Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("deleted_at").AsCustom("timestamptz").Nullable();

        // Índices de performance
        Create.Index("ix_ka_client_id").OnTable("knowledge_articles").OnColumn("client_id");
        Create.Index("ix_ka_site_id").OnTable("knowledge_articles").OnColumn("site_id");
        Create.Index("ix_ka_is_published").OnTable("knowledge_articles").OnColumn("is_published");
        Create.Index("ix_ka_deleted_at").OnTable("knowledge_articles").OnColumn("deleted_at");

        // Constraint de escopo: site_id só pode ser preenchido se client_id também for
        Execute.Sql(@"
            ALTER TABLE knowledge_articles
            ADD CONSTRAINT chk_ka_scope
            CHECK (site_id IS NULL OR client_id IS NOT NULL);
        ");

        // ── knowledge_article_chunks ─────────────────────────────────
        Create.Table("knowledge_article_chunks")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("article_id").AsGuid().NotNullable()
                .ForeignKey("fk_kac_article", "knowledge_articles", "id")
            .WithColumn("chunk_index").AsInt32().NotNullable()
            .WithColumn("section_title").AsString(500).Nullable()
            .WithColumn("content").AsCustom("text").NotNullable()
            .WithColumn("token_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("embedding").AsCustom("vector(1536)").Nullable()
            .WithColumn("embedding_generated_at").AsCustom("timestamptz").Nullable();

        Create.Index("ix_kac_article_id").OnTable("knowledge_article_chunks").OnColumn("article_id");
        Create.Index("ix_kac_no_embedding")
            .OnTable("knowledge_article_chunks")
            .OnColumn("embedding_generated_at");

        // Índice HNSW para busca por cosine distance (performance em produção)
        Execute.Sql(@"
            CREATE INDEX ix_kac_embedding_hnsw
            ON knowledge_article_chunks
            USING hnsw (embedding vector_cosine_ops)
            WITH (m = 16, ef_construction = 64);
        ");

        // ── ticket_knowledge_links ───────────────────────────────────
        Create.Table("ticket_knowledge_links")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable()
                .ForeignKey("fk_tkl_ticket", "tickets", "id")
            .WithColumn("article_id").AsGuid().NotNullable()
                .ForeignKey("fk_tkl_article", "knowledge_articles", "id")
            .WithColumn("linked_by").AsString(256).Nullable()
            .WithColumn("note").AsString(2000).Nullable()
            .WithColumn("linked_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_tkl_ticket_id").OnTable("ticket_knowledge_links").OnColumn("ticket_id");
        Create.UniqueConstraint("uq_tkl_ticket_article")
            .OnTable("ticket_knowledge_links")
            .Columns("ticket_id", "article_id");
    }

    public override void Down()
    {
        Delete.Table("ticket_knowledge_links");
        Execute.Sql("DROP INDEX IF EXISTS ix_kac_embedding_hnsw;");
        Delete.Table("knowledge_article_chunks");
        Delete.Table("knowledge_articles");
    }
}
