using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260315_068)]
public class M068_AddMeshCentralMappingToSiteConfigurations : Migration
{
    public override void Up()
    {
        if (!Schema.Table("site_configurations").Column("meshcentral_group_name").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("meshcentral_group_name").AsString(200).Nullable();
        }

        if (!Schema.Table("site_configurations").Column("meshcentral_mesh_id").Exists())
        {
            Alter.Table("site_configurations")
                .AddColumn("meshcentral_mesh_id").AsString(200).Nullable();
        }

        if (!Schema.Table("site_configurations").Index("ix_site_configurations_meshcentral_mesh_id").Exists())
        {
            Create.Index("ix_site_configurations_meshcentral_mesh_id")
                .OnTable("site_configurations")
                .OnColumn("meshcentral_mesh_id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("site_configurations").Index("ix_site_configurations_meshcentral_mesh_id").Exists())
        {
            Delete.Index("ix_site_configurations_meshcentral_mesh_id").OnTable("site_configurations");
        }

        if (Schema.Table("site_configurations").Column("meshcentral_mesh_id").Exists())
        {
            Delete.Column("meshcentral_mesh_id").FromTable("site_configurations");
        }

        if (Schema.Table("site_configurations").Column("meshcentral_group_name").Exists())
        {
            Delete.Column("meshcentral_group_name").FromTable("site_configurations");
        }
    }
}

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
