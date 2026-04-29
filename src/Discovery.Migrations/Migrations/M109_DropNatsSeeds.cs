using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260428_109)]
public class M109_DropNatsSeeds : Migration
{
    public override void Up()
    {
        Delete.Column("nats_account_seed").FromTable("server_configurations");
        Delete.Column("nats_xkey_seed").FromTable("server_configurations");
    }

    public override void Down()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_account_seed").AsString(int.MaxValue).NotNullable().WithDefaultValue(string.Empty)
            .AddColumn("nats_xkey_seed").AsString(int.MaxValue).NotNullable().WithDefaultValue(string.Empty);
    }
}
