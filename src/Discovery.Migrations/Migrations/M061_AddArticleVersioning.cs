using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria tabela de versionamento de artigos: knowledge_article_versions.
/// Cada transição Draft→Published ou Draft→Internal gera um snapshot imutável.
/// </summary>
[Migration(20260519_061)]
public class M061_AddArticleVersioning : Migration
{
    public override void Up()
    {
        Create.Table("knowledge_article_versions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("article_id").AsGuid().NotNullable()
                .ForeignKey("fk_kav_article", "knowledge_articles", "id")
            .WithColumn("version_number").AsInt32().NotNullable()
            .WithColumn("title").AsString(500).NotNullable()
            .WithColumn("content").AsCustom("text").NotNullable()
            .WithColumn("category").AsString(200).Nullable()
            .WithColumn("tags_json").AsCustom("jsonb").Nullable()
            .WithColumn("status").AsString(20).NotNullable()
            .WithColumn("edited_by").AsString(256).Nullable()
            .WithColumn("change_summary").AsString(500).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_kav_article_id")
            .OnTable("knowledge_article_versions")
            .OnColumn("article_id");

        // Índice composto (article_id, version_number) via SQL raw
        Execute.Sql(@"
            CREATE INDEX ix_kav_article_version
            ON knowledge_article_versions (article_id, version_number);
        ");

        Create.UniqueConstraint("uq_kav_article_version")
            .OnTable("knowledge_article_versions")
            .Columns("article_id", "version_number");

        // Índice para busca por data (consultar histórico)
        Create.Index("ix_kav_created_at")
            .OnTable("knowledge_article_versions")
            .OnColumn("created_at");
    }

    public override void Down()
    {
        Delete.Table("knowledge_article_versions");
    }
}
