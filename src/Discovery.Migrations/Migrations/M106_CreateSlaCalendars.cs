using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_106)]
public class M106_CreateSlaCalendars : Migration
{
    public override void Up()
    {
        Create.Table("sla_calendars")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("name").AsString(255).NotNullable()
            .WithColumn("client_id").AsGuid().Nullable()
            .WithColumn("timezone").AsString(100).NotNullable().WithDefaultValue("UTC")
            .WithColumn("work_day_start_hour").AsInt32().NotNullable().WithDefaultValue(8)
            .WithColumn("work_day_end_hour").AsInt32().NotNullable().WithDefaultValue(18)
            .WithColumn("work_days_json").AsString(50).NotNullable().WithDefaultValue("[1,2,3,4,5]")
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable();

        Create.Table("sla_calendar_holidays")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("calendar_id").AsGuid().NotNullable()
            .WithColumn("date").AsDate().NotNullable()
            .WithColumn("name").AsString(255).NotNullable();

        Create.Index("ix_sla_calendar_holidays_calendar_id")
            .OnTable("sla_calendar_holidays")
            .OnColumn("calendar_id");

        Alter.Table("workflow_profiles")
            .AddColumn("sla_calendar_id").AsGuid().Nullable();
    }

    public override void Down()
    {
        Delete.Column("sla_calendar_id").FromTable("workflow_profiles");
        Delete.Table("sla_calendar_holidays");
        Delete.Table("sla_calendars");
    }
}
