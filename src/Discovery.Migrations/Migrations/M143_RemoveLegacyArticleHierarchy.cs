using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Remove a hierarquia de páginas do modelo antigo (M141), que vinculava artigos
/// entre si via parent_id em knowledge_articles.
///
/// O novo modelo (M142) usa a tabela knowledge_article_pages para sub-páginas
/// INTERNAS ao artigo (estilo Notion), sem vincular artigos entre si.
/// </summary>
[Migration(20260803_143)]
public class M143_RemoveLegacyArticleHierarchy : Migration
{
    public override void Up()
    {
        Delete.Index("ix_ka_parent_id").OnTable("knowledge_articles");
        Delete.ForeignKey("fk_ka_parent").OnTable("knowledge_articles");
        Delete.Column("parent_id").FromTable("knowledge_articles");
        Delete.Column("sort_order").FromTable("knowledge_articles");
        Delete.Column("is_page").FromTable("knowledge_articles");
    }

    public override void Down()
    {
        Alter.Table("knowledge_articles")
            .AddColumn("parent_id").AsGuid().Nullable()
            .AddColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("is_page").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.ForeignKey("fk_ka_parent")
            .FromTable("knowledge_articles").ForeignColumn("parent_id")
            .ToTable("knowledge_articles").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.None);

        Create.Index("ix_ka_parent_id")
            .OnTable("knowledge_articles")
            .OnColumn("parent_id").Ascending()
            .OnColumn("sort_order").Ascending();
    }
}
