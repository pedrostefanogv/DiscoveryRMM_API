using FluentMigrator;

[Migration(20260324_086)]
public class M086_DropLegacyNatsServerUrl : Migration
{
    public override void Up()
    {
        Delete.Column("nats_server_url").FromTable("server_configurations");
    }

    public override void Down()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_server_url").AsCustom("text").NotNullable().WithDefaultValue("nats://localhost:4222");
    }
}
