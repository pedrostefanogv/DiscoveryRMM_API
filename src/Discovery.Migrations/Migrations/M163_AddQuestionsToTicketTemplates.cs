using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Perguntas próprias do template de chamado (mini questionário). Separadas dos
/// campos personalizados do chamado — as respostas são registradas no snapshot.
/// </summary>
[Migration(20260926_163)]
public class M163_AddQuestionsToTicketTemplates : Migration
{
    public override void Up()
    {
        if (Schema.Table("ticket_templates").Exists()
            && !Schema.Table("ticket_templates").Column("questions_json").Exists())
        {
            Alter.Table("ticket_templates")
                .AddColumn("questions_json").AsCustom("jsonb").NotNullable().WithDefaultValue("[]");
        }
    }

    public override void Down()
    {
        if (Schema.Table("ticket_templates").Exists()
            && Schema.Table("ticket_templates").Column("questions_json").Exists())
        {
            Delete.Column("questions_json").FromTable("ticket_templates");
        }
    }
}
