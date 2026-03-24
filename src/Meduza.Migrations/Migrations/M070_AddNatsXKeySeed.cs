using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260324_070)]
public class M070_AddNatsXKeySeed : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_xkey_seed").AsCustom("text").NotNullable().WithDefaultValue(string.Empty);
    }

    public override void Down()
    {
        Delete.Column("nats_xkey_seed").FromTable("server_configurations");
    }
}
