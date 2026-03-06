using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260306_031)]
public class M031_AddTicketSoftDelete : Migration
{
    public override void Up()
    {
        // Adiciona coluna deleted_at para soft delete em tickets
        Alter.Table("tickets")
            .AddColumn("deleted_at").AsCustom("timestamptz").Nullable();

        // Cria índice para otimizar queries que filtram por deleted_at
        Create.Index("ix_tickets_deleted_at")
            .OnTable("tickets")
            .OnColumn("deleted_at");
    }

    public override void Down()
    {
        Delete.Index("ix_tickets_deleted_at").OnTable("tickets");
        Delete.Column("deleted_at").FromTable("tickets");
    }
}
