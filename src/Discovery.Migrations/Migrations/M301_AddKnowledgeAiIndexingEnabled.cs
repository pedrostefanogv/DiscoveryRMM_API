using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona controle por artigo para permitir/impedir análise por IA e geração de embeddings
/// (`ai_indexing_enabled`, default true para preservar o comportamento dos artigos existentes).
/// </summary>
[Migration(20270129_301)]
public class M301_AddKnowledgeAiIndexingEnabled : Migration
{
    private const string TableName = "knowledge_articles";
    private const string ColumnName = "ai_indexing_enabled";

    public override void Up()
    {
        if (!Schema.Table(TableName).Column(ColumnName).Exists())
        {
            Alter.Table(TableName)
                .AddColumn(ColumnName)
                .AsBoolean()
                .NotNullable()
                .WithDefaultValue(true);
        }
    }

    public override void Down()
    {
        if (Schema.Table(TableName).Column(ColumnName).Exists())
            Delete.Column(ColumnName).FromTable(TableName);
    }
}
