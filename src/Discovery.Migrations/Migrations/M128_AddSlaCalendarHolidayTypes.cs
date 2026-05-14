using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260514_128)]
public class M128_AddSlaCalendarHolidayTypes : Migration
{
    public override void Up()
    {
        // Adiciona colunas de tipo ao sla_calendar_holidays
        Alter.Table("sla_calendar_holidays")
            .AddColumn("holiday_type").AsInt32().NotNullable().WithDefaultValue(0);

        Alter.Table("sla_calendar_holidays")
            .AddColumn("relative_month").AsInt32().Nullable();

        Alter.Table("sla_calendar_holidays")
            .AddColumn("relative_day_of_week").AsInt32().Nullable();

        Alter.Table("sla_calendar_holidays")
            .AddColumn("relative_occurrence").AsInt32().Nullable();

        Alter.Table("sla_calendar_holidays")
            .AddColumn("relative_method").AsInt32().Nullable();

        // Para feriados existentes com date fixa, marca como Yearly
        // (pois a maioria dos feriados já cadastrados recorre anualmente)
        Execute.Sql("""
            UPDATE sla_calendar_holidays
            SET holiday_type = 1
            WHERE holiday_type = 0
        """);
    }

    public override void Down()
    {
        Delete.Column("relative_method").FromTable("sla_calendar_holidays");
        Delete.Column("relative_occurrence").FromTable("sla_calendar_holidays");
        Delete.Column("relative_day_of_week").FromTable("sla_calendar_holidays");
        Delete.Column("relative_month").FromTable("sla_calendar_holidays");
        Delete.Column("holiday_type").FromTable("sla_calendar_holidays");
    }
}
