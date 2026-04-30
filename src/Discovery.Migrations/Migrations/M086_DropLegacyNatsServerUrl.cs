using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260324_086)]
public class M086_DropLegacyNatsServerUrl : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE server_configurations DROP COLUMN IF EXISTS nats_server_url;");
    }

    public override void Down()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_server_url").AsCustom("text").NotNullable().WithDefaultValue("nats://localhost:4222");
    }
}
