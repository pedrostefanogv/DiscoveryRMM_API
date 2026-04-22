using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260323_068)]
public class M068_CreateKnowledgeEmbeddingQueue : Migration
{
    public override void Up()
    {
        Create.Table("knowledge_embedding_queue")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("article_id").AsGuid().NotNullable()
                .ForeignKey("fk_keq_article", "knowledge_articles", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("Pending")
            .WithColumn("attempts").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("available_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("last_error").AsCustom("text").Nullable()
            .WithColumn("reason").AsString(50).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ux_knowledge_embedding_queue_article")
            .OnTable("knowledge_embedding_queue")
            .OnColumn("article_id")
            .Unique();

        Create.Index("ix_knowledge_embedding_queue_status_available")
            .OnTable("knowledge_embedding_queue")
            .OnColumn("status").Ascending()
            .OnColumn("available_at").Ascending();

        Create.Index("ix_knowledge_embedding_queue_updated_at")
            .OnTable("knowledge_embedding_queue")
            .OnColumn("updated_at").Descending();
    }

    public override void Down()
    {
        Delete.Index("ix_knowledge_embedding_queue_updated_at").OnTable("knowledge_embedding_queue");
        Delete.Index("ix_knowledge_embedding_queue_status_available").OnTable("knowledge_embedding_queue");
        Delete.Index("ux_knowledge_embedding_queue_article").OnTable("knowledge_embedding_queue");
        Delete.Table("knowledge_embedding_queue");
    }
}
