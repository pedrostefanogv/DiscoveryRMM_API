using FluentMigrator;

[Migration(20260325_081)]
public class M081_AddNatsServerUrlInternalExternal : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_server_host_internal").AsCustom("text").NotNullable().WithDefaultValue("localhost")
            .AddColumn("nats_server_host_external").AsCustom("text").NotNullable().WithDefaultValue("localhost")
            .AddColumn("nats_use_wss_external").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("nats_use_wss_external").FromTable("server_configurations");
        Delete.Column("nats_server_host_external").FromTable("server_configurations");
        Delete.Column("nats_server_host_internal").FromTable("server_configurations");
    }
}
