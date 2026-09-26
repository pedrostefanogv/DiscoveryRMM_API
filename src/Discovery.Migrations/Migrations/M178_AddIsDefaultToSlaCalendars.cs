using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Marca o calendário de SLA padrão do escopo (global ou por cliente), usado
/// para pré-selecionar o calendário ao criar um perfil de workflow.
/// </summary>
[Migration(20260929_178)]
public class M178_AddIsDefaultToSlaCalendars : Migration
{
    public override void Up()
    {
        if (Schema.Table("sla_calendars").Exists()
            && !Schema.Table("sla_calendars").Column("is_default").Exists())
        {
            Alter.Table("sla_calendars")
                .AddColumn("is_default").AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }

    public override void Down()
    {
        if (Schema.Table("sla_calendars").Exists()
            && Schema.Table("sla_calendars").Column("is_default").Exists())
        {
            Delete.Column("is_default").FromTable("sla_calendars");
        }
    }
}
