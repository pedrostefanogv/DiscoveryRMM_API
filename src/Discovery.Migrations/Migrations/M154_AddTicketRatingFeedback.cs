using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona o texto de feedback da avaliação (CSAT) do chamado.
/// Hoje o rating já é persistido em tickets.rating (M032), mas o comentário
/// do usuário/agente era descartado.
/// </summary>
[Migration(20260922_154)]
public class M154_AddTicketRatingFeedback : Migration
{
    public override void Up()
    {
        if (!Schema.Table("tickets").Exists())
            return;

        if (!Schema.Table("tickets").Column("rating_feedback").Exists())
            Alter.Table("tickets").AddColumn("rating_feedback").AsString(2000).Nullable();
    }

    public override void Down()
    {
        if (Schema.Table("tickets").Exists() && Schema.Table("tickets").Column("rating_feedback").Exists())
            Delete.Column("rating_feedback").FromTable("tickets");
    }
}
