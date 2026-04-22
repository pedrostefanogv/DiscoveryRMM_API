using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260413_093)]
public class M093_RemoveLegacyNatsIncludeLegacySubjects : Migration
{
    public override void Up()
    {
        if (Schema.Table("server_configurations").Column("nats_include_legacy_subjects").Exists())
            Delete.Column("nats_include_legacy_subjects").FromTable("server_configurations");
    }

    public override void Down()
    {
        if (!Schema.Table("server_configurations").Column("nats_include_legacy_subjects").Exists())
        {
            Alter.Table("server_configurations")
                .AddColumn("nats_include_legacy_subjects")
                .AsBoolean()
                .NotNullable()
                .WithDefaultValue(false);
        }
    }
}
