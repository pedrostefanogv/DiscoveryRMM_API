using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona hierarquia de páginas à base de conhecimento (estilo Notion).
/// Permite que um artigo tenha subpáginas aninhadas até 3 níveis de profundidade.
///
/// Novas colunas em knowledge_articles:
///   - parent_id   : FK auto-referenciada para a página pai (null = página raiz)
///   - sort_order  : ordenação entre páginas irmãs
///   - is_page     : true quando a página é um container (tem subpáginas)
///
/// A profundidade máxima (3 níveis) é validada em nível de aplicação (handlers CQRS),
/// não no banco, para manter a flexibilidade.
/// </summary>
[Migration(20260802_141)]
public class M141_AddKnowledgePageHierarchy : Migration
{
    public override void Up()
    {
        Alter.Table("knowledge_articles")
            .AddColumn("parent_id").AsGuid().Nullable()
            .AddColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("is_page").AsBoolean().NotNullable().WithDefaultValue(false);

        // FK auto-referenciada: parent_id aponta para outra knowledge_articles
        Create.ForeignKey("fk_ka_parent")
            .FromTable("knowledge_articles").ForeignColumn("parent_id")
            .ToTable("knowledge_articles").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.None);

        // Índice para montagem eficiente da árvore (busca por pai + ordenação)
        Create.Index("ix_ka_parent_id")
            .OnTable("knowledge_articles")
            .OnColumn("parent_id").Ascending()
            .OnColumn("sort_order").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_ka_parent_id").OnTable("knowledge_articles");
        Delete.ForeignKey("fk_ka_parent").OnTable("knowledge_articles");
        Delete.Column("parent_id").FromTable("knowledge_articles");
        Delete.Column("sort_order").FromTable("knowledge_articles");
        Delete.Column("is_page").FromTable("knowledge_articles");
    }
}
