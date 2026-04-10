using FluentMigrator;

[Migration(20260325_080)]
public class M080_AddNatsServerUrl : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_server_url").AsCustom("text").NotNullable().WithDefaultValue("nats://localhost:4222");
    }

    public override void Down()
    {
        Delete.Column("nats_server_url").FromTable("server_configurations");
    }
}
