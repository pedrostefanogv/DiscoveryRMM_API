using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260323_069)]
public class M069_AddNatsAuthSettings : Migration
{
    public override void Up()
    {
        Alter.Table("server_configurations")
            .AddColumn("nats_auth_enabled").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("nats_account_seed").AsCustom("text").NotNullable().WithDefaultValue(string.Empty)
            .AddColumn("nats_agent_jwt_ttl_minutes").AsInt32().NotNullable().WithDefaultValue(15)
            .AddColumn("nats_user_jwt_ttl_minutes").AsInt32().NotNullable().WithDefaultValue(15)
            .AddColumn("nats_use_scoped_subjects").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("nats_include_legacy_subjects").AsBoolean().NotNullable().WithDefaultValue(true);
    }

    public override void Down()
    {
        Delete.Column("nats_include_legacy_subjects").FromTable("server_configurations");
        Delete.Column("nats_use_scoped_subjects").FromTable("server_configurations");
        Delete.Column("nats_user_jwt_ttl_minutes").FromTable("server_configurations");
        Delete.Column("nats_agent_jwt_ttl_minutes").FromTable("server_configurations");
        Delete.Column("nats_account_seed").FromTable("server_configurations");
        Delete.Column("nats_auth_enabled").FromTable("server_configurations");
    }
}
