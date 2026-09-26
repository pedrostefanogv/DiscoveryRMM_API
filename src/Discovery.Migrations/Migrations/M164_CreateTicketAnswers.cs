using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Armazenamento estruturado das respostas do mini questionário do template,
/// para filtros e relatórios. O snapshot markdown do chamado continua sendo o
/// registro legível.
/// </summary>
[Migration(20260926_164)]
public class M164_CreateTicketAnswers : Migration
{
    public override void Up()
    {
        if (Schema.Table("tickets").Exists()
            && !Schema.Table("tickets").Column("template_id").Exists())
        {
            Alter.Table("tickets").AddColumn("template_id").AsGuid().Nullable();
            Create.Index("ix_tickets_template_id").OnTable("tickets").OnColumn("template_id").Ascending();
        }

        if (!Schema.Table("ticket_answers").Exists())
        {
            Create.Table("ticket_answers")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("ticket_id").AsGuid().NotNullable()
                .WithColumn("template_id").AsGuid().Nullable()
                .WithColumn("question_key").AsString(100).NotNullable()
                .WithColumn("question_label").AsString(200).NotNullable()
                .WithColumn("value_text").AsCustom("text").Nullable()
                .WithColumn("value_json").AsCustom("jsonb").NotNullable().WithDefaultValue("null")
                .WithColumn("created_at").AsCustom("timestamptz").NotNullable();

            Create.ForeignKey("fk_ticket_answers_ticket")
                .FromTable("ticket_answers").ForeignColumn("ticket_id")
                .ToTable("tickets").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.Cascade);

            Create.Index("ux_ticket_answers_ticket_question")
                .OnTable("ticket_answers")
                .OnColumn("ticket_id").Ascending()
                .OnColumn("question_key").Ascending()
                .WithOptions().Unique();

            Create.Index("ix_ticket_answers_key_value")
                .OnTable("ticket_answers")
                .OnColumn("question_key").Ascending()
                .OnColumn("value_text").Ascending();

            Create.Index("ix_ticket_answers_template")
                .OnTable("ticket_answers")
                .OnColumn("template_id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("ticket_answers").Exists()) Delete.Table("ticket_answers");
        if (Schema.Table("tickets").Exists() && Schema.Table("tickets").Column("template_id").Exists())
            Delete.Column("template_id").FromTable("tickets");
    }
}
