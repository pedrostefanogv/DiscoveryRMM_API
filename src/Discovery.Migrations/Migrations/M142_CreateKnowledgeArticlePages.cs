using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria a tabela de sub-páginas internas de artigos da base de conhecimento (estilo Notion).
///
/// Diferente do modelo anterior (M141, que vinculava artigos entre si via parent_id em
/// knowledge_articles), esta tabela representa as "partes/páginas" DENTRO de um único artigo.
/// Cada sub-página pertence a um artigo (article_id) e pode ter sub-páginas aninhadas
/// (parent_page_id, até 3 níveis).
///
/// A profundidade máxima (3 níveis) é validada em nível de aplicação (handlers CQRS).
/// </summary>
[Migration(20260803_142)]
public class M142_CreateKnowledgeArticlePages : Migration
{
    public override void Up()
    {
        Create.Table("knowledge_article_pages")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("article_id").AsGuid().NotNullable()
                .ForeignKey("fk_kap_article", "knowledge_articles", "id")
            .WithColumn("parent_page_id").AsGuid().Nullable()
            .WithColumn("title").AsString(500).NotNullable()
            .WithColumn("content").AsCustom("text").NotNullable()
            .WithColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable()
                .WithDefault(SystemMethods.CurrentUTCDateTime);

        // FK auto-referenciada: parent_page_id aponta para outra knowledge_article_pages
        Create.ForeignKey("fk_kap_parent")
            .FromTable("knowledge_article_pages").ForeignColumn("parent_page_id")
            .ToTable("knowledge_article_pages").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.None);

        // Índice para montagem eficiente da árvore de sub-páginas de um artigo
        Create.Index("ix_kap_article_parent")
            .OnTable("knowledge_article_pages")
            .OnColumn("article_id").Ascending()
            .OnColumn("parent_page_id").Ascending()
            .OnColumn("sort_order").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_kap_article_parent").OnTable("knowledge_article_pages");
        Delete.ForeignKey("fk_kap_parent").OnTable("knowledge_article_pages");
        Delete.Table("knowledge_article_pages");
    }
}
