using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260706_122)]
public class M122_CreateReportFilterPresets : Migration
{
    public override void Up()
    {
        Create.Table("report_filter_presets")
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("template_id").AsGuid().NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("filters_json").AsString(int.MaxValue).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentDateTimeOffset);

        Create.Index("ix_report_filter_presets_user_template")
            .OnTable("report_filter_presets")
            .OnColumn("user_id").Ascending()
            .OnColumn("template_id").Ascending();

        // Add delivery fields to report_schedules
        Alter.Table("report_schedules")
            .AddColumn("delivery_mode").AsString(20).NotNullable().WithDefaultValue("storage")
            .AddColumn("recipients").AsString(4000).Nullable()
            .AddColumn("webhook_url").AsString(2048).Nullable();

        Create.Index("ix_report_schedules_next_trigger")
            .OnTable("report_schedules")
            .OnColumn("is_active").Ascending()
            .OnColumn("next_trigger_at").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_report_schedules_next_trigger").OnTable("report_schedules");

        Delete.Column("delivery_mode").FromTable("report_schedules");
        Delete.Column("recipients").FromTable("report_schedules");
        Delete.Column("webhook_url").FromTable("report_schedules");

        Delete.Table("report_filter_presets");
    }
}
