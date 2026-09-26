using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Proteção do vínculo do chamado com o template de abertura:
/// - `tickets.template_name`: snapshot histórico do nome (sobrevive à exclusão
///   do template e mantém detalhe/relatórios legíveis);
/// - FKs `ON DELETE SET NULL` em `tickets.template_id` e
///   `ticket_answers.template_id`, eliminando referências quebradas.
/// </summary>
[Migration(20260926_165)]
public class M165_AddTicketTemplateNameAndTemplateFks : Migration
{
    public override void Up()
    {
        var hasTickets = Schema.Table("tickets").Exists();
        var hasTemplates = Schema.Table("ticket_templates").Exists();
        var hasAnswers = Schema.Table("ticket_answers").Exists();

        if (hasTickets && !Schema.Table("tickets").Column("template_name").Exists())
            Alter.Table("tickets").AddColumn("template_name").AsString(200).Nullable();

        // Backfill do nome a partir do template atual (histórico do que foi usado).
        if (hasTickets && hasTemplates && Schema.Table("tickets").Column("template_name").Exists())
        {
            Execute.Sql(@"
                UPDATE tickets t
                   SET template_name = tt.name
                  FROM ticket_templates tt
                 WHERE t.template_id = tt.id
                   AND t.template_name IS NULL");
        }

        // Dados legados podem ter template_id apontando para template removido
        // (não havia FK). Limpa antes de criar as constraints.
        if (hasTickets)
        {
            Execute.Sql(@"
                UPDATE tickets
                   SET template_id = NULL
                 WHERE template_id IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM ticket_templates tt WHERE tt.id = tickets.template_id)");
        }

        if (hasAnswers)
        {
            Execute.Sql(@"
                UPDATE ticket_answers
                   SET template_id = NULL
                 WHERE template_id IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM ticket_templates tt WHERE tt.id = ticket_answers.template_id)");
        }

        if (hasTickets && hasTemplates && !Schema.Table("tickets").Constraint("fk_tickets_template").Exists())
        {
            Create.ForeignKey("fk_tickets_template")
                .FromTable("tickets").ForeignColumn("template_id")
                .ToTable("ticket_templates").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.SetNull);
        }

        if (hasTickets && hasTemplates && hasAnswers
            && !Schema.Table("ticket_answers").Constraint("fk_ticket_answers_template").Exists())
        {
            Create.ForeignKey("fk_ticket_answers_template")
                .FromTable("ticket_answers").ForeignColumn("template_id")
                .ToTable("ticket_templates").PrimaryColumn("id")
                .OnDelete(System.Data.Rule.SetNull);
        }
    }

    public override void Down()
    {
        if (Schema.Table("ticket_answers").Exists()
            && Schema.Table("ticket_answers").Constraint("fk_ticket_answers_template").Exists())
        {
            Delete.ForeignKey("fk_ticket_answers_template").OnTable("ticket_answers");
        }

        if (Schema.Table("tickets").Exists()
            && Schema.Table("tickets").Constraint("fk_tickets_template").Exists())
        {
            Delete.ForeignKey("fk_tickets_template").OnTable("tickets");
        }

        if (Schema.Table("tickets").Exists() && Schema.Table("tickets").Column("template_name").Exists())
            Delete.Column("template_name").FromTable("tickets");
    }
}
