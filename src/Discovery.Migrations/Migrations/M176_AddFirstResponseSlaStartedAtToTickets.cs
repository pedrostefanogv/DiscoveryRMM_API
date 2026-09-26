using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Início da contagem do SLA de primeira resposta. Permite reiniciar o FRT ao
/// reabrir um chamado; registros antigos ficam nulos e caem para created_at.
/// </summary>
[Migration(20260929_176)]
public class M176_AddFirstResponseSlaStartedAtToTickets : Migration
{
    public override void Up()
    {
        if (Schema.Table("tickets").Exists()
            && !Schema.Table("tickets").Column("first_response_sla_started_at").Exists())
        {
            Alter.Table("tickets").AddColumn("first_response_sla_started_at").AsDateTimeOffset().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("tickets").Exists()
            && Schema.Table("tickets").Column("first_response_sla_started_at").Exists())
        {
            Delete.Column("first_response_sla_started_at").FromTable("tickets");
        }
    }
}
