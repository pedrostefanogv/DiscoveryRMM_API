using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Feriados relativos não têm data própria (o dia é calculado pela regra).
/// A coluna passa a aceitar NULL e a sentinela 2000-01-01 é removida.
/// </summary>
[Migration(20260929_175)]
public class M175_MakeRelativeHolidayDateNullable : Migration
{
    public override void Up()
    {
        if (Schema.Table("sla_calendar_holidays").Exists()
            && Schema.Table("sla_calendar_holidays").Column("date").Exists())
        {
            Alter.Column("date").OnTable("sla_calendar_holidays").AsDate().Nullable();
            Execute.Sql("UPDATE sla_calendar_holidays SET date = NULL WHERE holiday_type = 2");
        }
    }

    public override void Down()
    {
        if (Schema.Table("sla_calendar_holidays").Exists()
            && Schema.Table("sla_calendar_holidays").Column("date").Exists())
        {
            Execute.Sql("UPDATE sla_calendar_holidays SET date = DATE '2000-01-01' WHERE date IS NULL");
            Alter.Column("date").OnTable("sla_calendar_holidays").AsDate().NotNullable();
        }
    }
}
