using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Solicitante do chamado (quem abriu). Usado para restringir a avaliação CSAT
/// ao solicitante e para exibir "Solicitado por".
///
/// Chamados antigos e os abertos pelo chat/agent ficam nulos (o agent já é
/// registrado em tickets.agent_id); nesse caso a avaliação segue liberada.
/// </summary>
[Migration(20260928_173)]
public class M173_AddRequesterToTickets : Migration
{
    public override void Up()
    {
        if (Schema.Table("tickets").Exists()
            && !Schema.Table("tickets").Column("requester_user_id").Exists())
        {
            Alter.Table("tickets").AddColumn("requester_user_id").AsGuid().Nullable();
            Create.Index("ix_tickets_requester").OnTable("tickets").OnColumn("requester_user_id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("tickets").Exists()
            && Schema.Table("tickets").Column("requester_user_id").Exists())
        {
            // O índice depende da coluna: sai primeiro (índice NÃO é constraint,
            // então a checagem é por Index().Exists()).
            if (Schema.Table("tickets").Index("ix_tickets_requester").Exists())
                Delete.Index("ix_tickets_requester").OnTable("tickets");

            Delete.Column("requester_user_id").FromTable("tickets");
        }
    }
}
